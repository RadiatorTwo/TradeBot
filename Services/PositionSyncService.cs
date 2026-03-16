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
            await RecoverStateAsync(ctx, stoppingToken);
        }

        _logger.LogInformation("PositionSync: Initialer Sync und State Recovery abgeschlossen.");

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

                    // History-Sync + Stale Order Cleanup (alle 5min)
                    if (DateTime.UtcNow - lastHistorySync > HistorySyncInterval)
                    {
                        await SyncClosedTradesAsync(ctx, stoppingToken);
                        await CleanupStaleOrdersAsync(ctx, stoppingToken);
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

    /// <summary>
    /// State Recovery nach Neustart: Vergleicht letzten Shutdown-Snapshot mit aktuellem Broker-Zustand.
    /// Loggt wiederhergestellte Positionen und storniert ggf. verwaiste Pending Orders.
    /// </summary>
    private async Task RecoverStateAsync(AccountContext ctx, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            // Letzten Shutdown-Snapshot laden
            var lastSnapshot = await db.EngineStateSnapshots
                .Where(s => s.AccountId == ctx.AccountId)
                .OrderByDescending(s => s.ShutdownAt)
                .FirstOrDefaultAsync(ct);

            var brokerPositions = await ctx.EffectiveBroker.GetPositionsAsync(ct);

            if (lastSnapshot != null)
            {
                var downtime = DateTime.UtcNow - lastSnapshot.ShutdownAt;
                _logger.LogInformation(
                    "Recovery [{AccountId}]: Letzter Shutdown {Time:dd.MM.yyyy HH:mm} UTC ({Downtime} Downtime), " +
                    "hatte {SnapshotCount} Positionen, Broker hat jetzt {BrokerCount}",
                    ctx.AccountId, lastSnapshot.ShutdownAt, downtime,
                    lastSnapshot.OpenPositionCount, brokerPositions.Count);

                if (lastSnapshot.CleanShutdown)
                    _logger.LogInformation("Recovery [{AccountId}]: Letzter Shutdown war sauber (Graceful)", ctx.AccountId);
                else
                    _logger.LogWarning("Recovery [{AccountId}]: Letzter Shutdown war NICHT sauber (Crash/Kill)", ctx.AccountId);
            }
            else
            {
                _logger.LogInformation("Recovery [{AccountId}]: Kein vorheriger Shutdown-Snapshot gefunden (Erststart)", ctx.AccountId);
            }

            // Recovery-Log schreiben
            if (brokerPositions.Count > 0)
            {
                db.TradingLogs.Add(new TradingLog
                {
                    AccountId = ctx.AccountId,
                    Level = "Info",
                    Source = "StateRecovery",
                    Message = $"{brokerPositions.Count} Positionen wiederhergestellt nach Neustart"
                });

                foreach (var pos in brokerPositions)
                {
                    _logger.LogInformation(
                        "Recovery [{AccountId}]: Position {Symbol} {Side} {Qty:F2} Lots @ {Price:F5}",
                        ctx.AccountId, pos.Symbol, pos.Side, pos.Quantity, pos.AveragePrice);
                }
            }
            else
            {
                db.TradingLogs.Add(new TradingLog
                {
                    AccountId = ctx.AccountId,
                    Level = "Info",
                    Source = "StateRecovery",
                    Message = "Neustart abgeschlossen – keine offenen Positionen"
                });
            }

            await db.SaveChangesAsync(ct);

            // Pending Orders pruefen und stornieren
            await CancelStalePendingOrdersAsync(ctx, db, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recovery [{AccountId}]: State Recovery fehlgeschlagen", ctx.AccountId);
        }
    }

    /// <summary>
    /// Storniert verwaiste Pending Orders (Limit/Stop) nach Neustart.
    /// Nur Orders die einen passenden Trade in der DB haben (von uns platziert) werden storniert.
    /// Broker-eigene Orders (SL/TP) bleiben bestehen.
    /// </summary>
    private async Task CancelStalePendingOrdersAsync(AccountContext ctx, TradingDbContext db, CancellationToken ct)
    {
        try
        {
            var pendingOrders = await ctx.EffectiveBroker.GetPendingOrdersAsync(ct);
            if (pendingOrders.Count == 0) return;

            // Nur Orders stornieren die wir selbst platziert haben (BrokerOrderId in DB)
            var ourOrderIds = await db.Trades
                .Where(t => t.AccountId == ctx.AccountId
                    && t.Status == TradeStatus.PendingOrder
                    && t.BrokerOrderId != null)
                .Select(t => t.BrokerOrderId!)
                .ToListAsync(ct);

            var orphanedOrders = pendingOrders
                .Where(o => ourOrderIds.Contains(o.OrderId))
                .ToList();

            if (orphanedOrders.Count == 0)
            {
                _logger.LogDebug(
                    "Recovery [{AccountId}]: {Count} Pending Orders beim Broker, keine davon von uns platziert – werden ignoriert",
                    ctx.AccountId, pendingOrders.Count);
                return;
            }

            _logger.LogWarning(
                "Recovery [{AccountId}]: {Count} verwaiste Pending Orders gefunden (von {Total} total), storniere...",
                ctx.AccountId, orphanedOrders.Count, pendingOrders.Count);

            var cancelledCount = 0;
            foreach (var order in orphanedOrders)
            {
                var cancelled = await ctx.EffectiveBroker.CancelOrderAsync(order.OrderId, ct);
                if (cancelled) cancelledCount++;

                _logger.LogInformation(
                    "Recovery [{AccountId}]: {Result} Pending Order {OrderId} ({Symbol} {Side} {Qty:F2} @ {Price:F5})",
                    ctx.AccountId, cancelled ? "Storniert" : "FEHLGESCHLAGEN",
                    order.OrderId, order.Symbol, order.Side, order.Qty, order.Price);
            }

            db.TradingLogs.Add(new TradingLog
            {
                AccountId = ctx.AccountId,
                Level = "Warning",
                Source = "StateRecovery",
                Message = $"{cancelledCount}/{orphanedOrders.Count} verwaiste Pending Orders nach Neustart storniert"
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recovery [{AccountId}]: Pending Order Check fehlgeschlagen", ctx.AccountId);
        }
    }

    /// <summary>
    /// Storniert Pending Orders die aelter als PendingOrderMaxAgeMinutes sind.
    /// Aktualisiert zugehoerige Trade-Eintraege in der DB.
    /// </summary>
    private async Task CleanupStaleOrdersAsync(AccountContext ctx, CancellationToken ct)
    {
        var maxAge = ctx.RiskSettings.PendingOrderMaxAgeMinutes;
        if (maxAge <= 0) return;

        try
        {
            var pendingOrders = await ctx.EffectiveBroker.GetPendingOrdersAsync(ct);
            if (pendingOrders.Count == 0) return;

            var cutoff = DateTime.UtcNow.AddMinutes(-maxAge);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var cancelledCount = 0;

            foreach (var order in pendingOrders.Where(o => o.CreatedAt < cutoff))
            {
                var cancelled = await ctx.EffectiveBroker.CancelOrderAsync(order.OrderId, ct);
                if (!cancelled) continue;

                cancelledCount++;

                // Zugehoerigen Trade in DB als Cancelled markieren
                var trade = await db.Trades
                    .FirstOrDefaultAsync(t => t.BrokerOrderId == order.OrderId
                        && t.Status == TradeStatus.PendingOrder, ct);

                if (trade != null)
                {
                    trade.Status = TradeStatus.Cancelled;
                    trade.ErrorMessage = $"Stale order cancelled (age > {maxAge}min)";
                }

                _logger.LogInformation(
                    "PositionSync [{AccountId}]: Stale Pending Order storniert: {OrderId} ({Symbol} {Side})",
                    ctx.AccountId, order.OrderId, order.Symbol, order.Side);
            }

            if (cancelledCount > 0)
            {
                db.TradingLogs.Add(new TradingLog
                {
                    AccountId = ctx.AccountId,
                    Level = "Info",
                    Source = "PositionSync",
                    Message = $"{cancelledCount} veraltete Pending Orders storniert (Limit: {maxAge}min)"
                });
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PositionSync [{AccountId}]: Stale Order Cleanup fehlgeschlagen", ctx.AccountId);
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
