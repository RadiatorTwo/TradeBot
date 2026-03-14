using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Synchronisiert lokale DB mit TradeLocker-Positionen:
/// 1. Startup: Offene Positionen abgleichen, geschlossene erkennen
/// 2. Periodisch (30s): Positions-DB aktualisieren, geschlossene Trades markieren
/// 3. Periodisch (5min): Trade-History von TradeLocker als Source of Truth
/// Iteriert ueber alle Accounts via AccountManager.
/// </summary>
public class PositionSyncService : BackgroundService
{
    private readonly AccountManager _accountMgr;
    private readonly MarketHoursService _marketHours;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PositionSyncService> _logger;

    private static readonly TimeSpan PositionSyncInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PositionSyncIntervalClosed = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HistorySyncInterval = TimeSpan.FromMinutes(5);

    public PositionSyncService(
        AccountManager accountMgr,
        MarketHoursService marketHours,
        IServiceScopeFactory scopeFactory,
        ILogger<PositionSyncService> logger)
    {
        _accountMgr = accountMgr;
        _marketHours = marketHours;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warten bis AccountManager Accounts geladen hat
        for (int i = 0; i < 30 && _accountMgr.Accounts.Count == 0; i++)
            await Task.Delay(2000, stoppingToken);

        if (_accountMgr.Accounts.Count == 0)
        {
            _logger.LogWarning("PositionSync: Keine Accounts verfuegbar, Service wird nicht gestartet.");
            return;
        }

        // Warten bis mindestens ein Broker verbunden ist
        for (int i = 0; i < 30 && !_accountMgr.Accounts.Any(a => a.EffectiveBroker.IsConnected); i++)
            await Task.Delay(2000, stoppingToken);

        // Startup-Sync fuer alle verbundenen Accounts
        foreach (var ctx in _accountMgr.Accounts.Where(a => a.EffectiveBroker.IsConnected))
        {
            await SyncPositionsAsync(ctx, stoppingToken);
            await SyncClosedTradesAsync(ctx, stoppingToken);
        }

        _logger.LogInformation("PositionSync: Initialer Sync abgeschlossen.");

        var lastHistorySync = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Reduzierte Sync-Frequenz wenn Markt geschlossen
                var anyActive = _accountMgr.Accounts.Any(a =>
                    a.EffectiveBroker.IsConnected && !a.Engine.IsPaused);
                var interval = !_marketHours.IsMarketOpen() || !anyActive
                    ? PositionSyncIntervalClosed
                    : PositionSyncInterval;
                await Task.Delay(interval, stoppingToken);

                foreach (var ctx in _accountMgr.Accounts)
                {
                    if (!ctx.EffectiveBroker.IsConnected || ctx.Engine.IsPaused)
                        continue;

                    // Positionen synchronisieren
                    await SyncPositionsAsync(ctx, stoppingToken);

                    // History-Sync (alle 5min)
                    if (DateTime.UtcNow - lastHistorySync > HistorySyncInterval)
                    {
                        await SyncClosedTradesAsync(ctx, stoppingToken);
                    }
                }

                if (DateTime.UtcNow - lastHistorySync > HistorySyncInterval)
                    lastHistorySync = DateTime.UtcNow;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PositionSync: Fehler im Sync-Zyklus");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Synchronisiert offene Positionen: Broker → DB.
    /// - Neue Broker-Positionen → DB einfügen
    /// - Bestehende → aktualisieren (Preis, Qty)
    /// - In DB aber nicht mehr beim Broker → entfernen (wurde geschlossen)
    /// - Executed-Trades ohne Broker-Position → als geschlossen markieren
    /// </summary>
    private async Task SyncPositionsAsync(AccountContext ctx, CancellationToken ct)
    {
        try
        {
            var brokerPositions = await ctx.EffectiveBroker.GetPositionsAsync(ct);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            var dbPositions = await db.Positions.ToListAsync(ct);

            // 1. Broker-Positionen in DB upserten
            foreach (var bp in brokerPositions)
            {
                var existing = bp.BrokerPositionId != null
                    ? dbPositions.FirstOrDefault(p => p.BrokerPositionId == bp.BrokerPositionId)
                    : null;

                if (existing != null)
                {
                    existing.Quantity = bp.Quantity;
                    existing.CurrentPrice = bp.CurrentPrice;
                    existing.AveragePrice = bp.AveragePrice;
                    existing.BrokerPositionId = bp.BrokerPositionId;
                    existing.Side = bp.Side;
                    existing.LastUpdated = DateTime.UtcNow;
                }
                else
                {
                    db.Positions.Add(new Position
                    {
                        Symbol = bp.Symbol,
                        Side = bp.Side,
                        Quantity = bp.Quantity,
                        AveragePrice = bp.AveragePrice,
                        CurrentPrice = bp.CurrentPrice,
                        BrokerPositionId = bp.BrokerPositionId,
                        LastUpdated = DateTime.UtcNow
                    });
                }
            }

            // 2. DB-Positionen ohne Broker-Gegenstück entfernen
            var brokerIds = brokerPositions
                .Where(p => p.BrokerPositionId != null)
                .Select(p => p.BrokerPositionId)
                .ToHashSet();
            var brokerSymbols = brokerPositions.Select(p => p.Symbol).ToHashSet();

            foreach (var dbPos in dbPositions)
            {
                var stillOpen = (dbPos.BrokerPositionId != null && brokerIds.Contains(dbPos.BrokerPositionId))
                    || (dbPos.BrokerPositionId == null && brokerSymbols.Contains(dbPos.Symbol));

                if (!stillOpen)
                {
                    _logger.LogInformation("PositionSync: Position {Symbol} (ID: {PosId}) wurde beim Broker geschlossen – entferne aus DB.",
                        dbPos.Symbol, dbPos.BrokerPositionId);
                    db.Positions.Remove(dbPos);

                    // Zugehörige Trades als geschlossen markieren
                    await MarkTradesClosedAsync(db, dbPos.Symbol, dbPos.BrokerPositionId, ct);
                }
            }

            // 3. Executed-Trades mit BrokerPositionId prüfen
            var openTradesWithPosId = await db.Trades
                .Where(t => t.Status == TradeStatus.Executed
                    && t.ClosedAt == null
                    && t.BrokerPositionId != null)
                .ToListAsync(ct);

            foreach (var trade in openTradesWithPosId)
            {
                if (!brokerIds.Contains(trade.BrokerPositionId))
                {
                    trade.ClosedAt = DateTime.UtcNow;
                    _logger.LogInformation("PositionSync: Trade #{Id} ({Symbol}) als geschlossen markiert – Position nicht mehr beim Broker.",
                        trade.Id, trade.Symbol);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PositionSync: SyncPositions fehlgeschlagen fuer Account {Id}", ctx.AccountId);
        }
    }

    /// <summary>
    /// Synchronisiert geschlossene Positionen von TradeLocker:
    /// - Erkennt SL/TP-Ausführungen und manuell geschlossene Trades
    /// - Erstellt Trade-Einträge für Broker-initiierte Schließungen
    /// - Aktualisiert RealizedPnL
    /// </summary>
    private async Task SyncClosedTradesAsync(AccountContext ctx, CancellationToken ct)
    {
        try
        {
            var closedPositions = await ctx.EffectiveBroker.GetClosedPositionsAsync(1, ct);
            if (closedPositions.Count == 0) return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            foreach (var closed in closedPositions)
            {
                // Prüfen ob wir diesen Trade schon kennen (via BrokerPositionId)
                var existingTrade = await db.Trades
                    .FirstOrDefaultAsync(t =>
                        t.BrokerPositionId == closed.PositionId
                        && t.Status == TradeStatus.Executed, ct);

                if (existingTrade != null)
                {
                    // Trade als geschlossen markieren mit PnL
                    if (existingTrade.ClosedAt == null)
                    {
                        existingTrade.ClosedAt = closed.ClosedAt;
                        existingTrade.RealizedPnL = closed.PnL;
                        _logger.LogInformation(
                            "PositionSync: Trade #{Id} ({Symbol}) geschlossen mit PnL: {PnL:F2}",
                            existingTrade.Id, existingTrade.Symbol, closed.PnL);
                    }
                    continue;
                }

                // Prüfen ob es schon einen Close-Trade-Eintrag gibt
                var alreadyLogged = await db.Trades
                    .AnyAsync(t =>
                        t.BrokerPositionId == closed.PositionId
                        && t.ClosedAt != null, ct);

                if (alreadyLogged) continue;

                // Neuen Trade-Eintrag für Broker-initiierte Schließung (SL/TP/manuell)
                var action = closed.Side.Equals("buy", StringComparison.OrdinalIgnoreCase)
                    ? TradeAction.Sell  // Buy-Position wird durch Sell geschlossen
                    : TradeAction.Buy;

                db.Trades.Add(new Trade
                {
                    AccountId = ctx.AccountId,
                    Symbol = closed.Symbol,
                    Action = action,
                    Status = TradeStatus.Executed,
                    Quantity = closed.Qty,
                    Price = closed.ClosePrice,
                    ExecutedPrice = closed.ClosePrice,
                    ExecutedAt = closed.ClosedAt,
                    ClosedAt = closed.ClosedAt,
                    RealizedPnL = closed.PnL,
                    BrokerPositionId = closed.PositionId,
                    ClaudeReasoning = "Broker-initiierte Schließung (SL/TP/manuell)",
                    ClaudeConfidence = 1.0
                });

                db.TradingLogs.Add(new TradingLog
                {
                    AccountId = ctx.AccountId,
                    Level = "Info",
                    Source = "PositionSync",
                    Message = $"Position {closed.Symbol} ({closed.Side}) geschlossen @ {closed.ClosePrice:F4}, PnL: {closed.PnL:F2}"
                });

                _logger.LogInformation(
                    "PositionSync: Broker-initiierte Schließung erkannt: {Symbol} {Side} {Qty} Lots, PnL: {PnL:F2}",
                    closed.Symbol, closed.Side, closed.Qty, closed.PnL);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PositionSync: SyncClosedTrades fehlgeschlagen fuer Account {Id}", ctx.AccountId);
        }
    }

    private static async Task MarkTradesClosedAsync(TradingDbContext db, string symbol, string? brokerPositionId, CancellationToken ct)
    {
        var trades = await db.Trades
            .Where(t => t.Symbol == symbol
                && t.Status == TradeStatus.Executed
                && t.ClosedAt == null
                && (brokerPositionId == null || t.BrokerPositionId == brokerPositionId))
            .ToListAsync(ct);

        foreach (var trade in trades)
        {
            trade.ClosedAt = DateTime.UtcNow;
        }
    }
}
