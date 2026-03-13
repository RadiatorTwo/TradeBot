using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;


namespace ClaudeTradingBot.Services;

public interface IRiskManager
{
    bool IsKillSwitchActive { get; }
    void ActivateKillSwitch(string reason);
    void ResetKillSwitch();
    Task<bool> ValidateTradeAsync(ClaudeTradeRecommendation recommendation, CancellationToken ct = default);
    Task CheckStopLossesAsync(CancellationToken ct = default);
    Task RecordDailyPnLAsync(CancellationToken ct = default);
}

public class RiskManager : IRiskManager
{
    private readonly IOptionsMonitor<RiskSettings> _settingsMonitor;
    private readonly IBrokerService _broker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RiskManager> _logger;

    private bool _killSwitchActive;
    private string? _killSwitchReason;

    private RiskSettings Settings => _settingsMonitor.CurrentValue;

    public bool IsKillSwitchActive => _killSwitchActive;

    public RiskManager(
        IOptionsMonitor<RiskSettings> settings,
        IBrokerService broker,
        IServiceScopeFactory scopeFactory,
        ILogger<RiskManager> logger)
    {
        _settingsMonitor = settings;
        _broker = broker;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void ActivateKillSwitch(string reason)
    {
        _killSwitchActive = true;
        _killSwitchReason = reason;
        _logger.LogCritical("🚨 KILL SWITCH ACTIVATED: {Reason}", reason);
    }

    public void ResetKillSwitch()
    {
        _killSwitchActive = false;
        _killSwitchReason = null;
        _logger.LogWarning("Kill switch has been manually reset");
    }

    public async Task<bool> ValidateTradeAsync(ClaudeTradeRecommendation rec, CancellationToken ct = default)
    {
        // 1. Kill Switch prüfen
        if (_killSwitchActive)
        {
            _logger.LogWarning("Trade rejected – kill switch active: {Reason}", _killSwitchReason);
            return false;
        }

        // 2. Hold-Empfehlung braucht keine Validierung
        if (rec.Action.Equals("hold", StringComparison.OrdinalIgnoreCase))
            return true;

        // 3. Confidence-Schwelle (konfigurierbar via MinConfidence)
        if (rec.Confidence < Settings.MinConfidence)
        {
            _logger.LogInformation("Trade skipped – confidence {Conf:P0} below MinConfidence {Min:P0}", rec.Confidence, Settings.MinConfidence);
            return false;
        }

        // 4. Maximale Positionsgröße prüfen (quantity in Lots; Notional = Lots × LotSize × Price)
        var portfolioValue = await _broker.GetPortfolioValueAsync(ct);
        var price = await _broker.GetCurrentPriceAsync(rec.Symbol, ct);
        var lotSize = GetLotSize(rec.Symbol);
        var tradeValue = rec.Quantity * lotSize * price;
        var positionPercent = portfolioValue > 0
            ? (double)(tradeValue / portfolioValue) * 100.0
            : 100.0;

        if (positionPercent > Settings.MaxPositionSizePercent)
        {
            _logger.LogWarning(
                "Trade rejected – position size {Pct:F1}% exceeds max {Max:F1}%",
                positionPercent, Settings.MaxPositionSizePercent);
            return false;
        }

        // 5. Max offene Positionen prüfen (nur bei Kauf)
        if (rec.Action.Equals("buy", StringComparison.OrdinalIgnoreCase))
        {
            var positions = await _broker.GetPositionsAsync(ct);
            if (positions.Count >= Settings.MaxOpenPositions &&
                !positions.Any(p => p.Symbol == rec.Symbol))
            {
                _logger.LogWarning(
                    "Trade rejected – max open positions ({Max}) reached",
                    Settings.MaxOpenPositions);
                return false;
            }
        }

        // 6. Tagesverlust prüfen
        if (await IsDailyLossExceededAsync(ct))
        {
            ActivateKillSwitch("Maximum daily loss exceeded");
            return false;
        }

        _logger.LogInformation(
            "Trade validated: {Action} {Qty:F2} Lots {Symbol} (notional: {Pct:F1}% of portfolio)",
            rec.Action, rec.Quantity, rec.Symbol, positionPercent);

        return true;
    }

    public async Task CheckStopLossesAsync(CancellationToken ct = default)
    {
        var positions = await _broker.GetPositionsAsync(ct);

        foreach (var pos in positions)
        {
            var currentPrice = await _broker.GetCurrentPriceAsync(pos.Symbol, ct);
            if (currentPrice == 0) continue;

            var isBuy = pos.Side.Equals("buy", StringComparison.OrdinalIgnoreCase);

            // Gewinn/Verlust in Pips berechnen (richtungsabhaengig)
            var priceDiff = isBuy
                ? currentPrice - pos.AveragePrice
                : pos.AveragePrice - currentPrice;
            var gainPips = (double)PipCalculator.PriceToPips(pos.Symbol, priceDiff) * Math.Sign((double)priceDiff);

            var lossPercent = pos.AveragePrice > 0
                ? (double)(Math.Abs(priceDiff) / pos.AveragePrice) * 100.0
                : 0.0;

            // ── 1. Breakeven-Stop ──────────────────────────────────────
            if (Settings.BreakevenTriggerPips > 0 && gainPips >= Settings.BreakevenTriggerPips && pos.BrokerPositionId != null)
            {
                // SL auf Einstiegspreis + 1 Pip setzen (nur wenn noch nicht dort)
                var breakevenSL = isBuy
                    ? pos.AveragePrice + PipCalculator.PipsToPrice(pos.Symbol, 1m)
                    : pos.AveragePrice - PipCalculator.PipsToPrice(pos.Symbol, 1m);

                // Nur aktualisieren wenn es eine Verbesserung waere
                var shouldUpdate = isBuy
                    ? breakevenSL > pos.AveragePrice  // Buy: SL nach oben
                    : breakevenSL < pos.AveragePrice; // Sell: SL nach unten

                if (shouldUpdate)
                {
                    var success = await _broker.UpdatePositionStopLossAsync(pos.BrokerPositionId, breakevenSL, ct);
                    if (success)
                    {
                        _logger.LogInformation(
                            "Breakeven-Stop gesetzt fuer {Symbol}: SL auf {SL:F5} (Gewinn: {Gain:F1} Pips)",
                            pos.Symbol, breakevenSL, gainPips);
                    }
                }
            }

            // ── 2. Trailing Stop ───────────────────────────────────────
            if (Settings.TrailingStopPips > 0 && gainPips > Settings.TrailingStopPips && pos.BrokerPositionId != null)
            {
                var trailDistance = PipCalculator.PipsToPrice(pos.Symbol, (decimal)Settings.TrailingStopPips);
                var trailingSL = isBuy
                    ? currentPrice - trailDistance
                    : currentPrice + trailDistance;

                // Nur aktualisieren wenn neuer SL besser als Einstiegspreis ist
                var isBetter = isBuy
                    ? trailingSL > pos.AveragePrice
                    : trailingSL < pos.AveragePrice;

                if (isBetter)
                {
                    var success = await _broker.UpdatePositionStopLossAsync(pos.BrokerPositionId, trailingSL, ct);
                    if (success)
                    {
                        _logger.LogInformation(
                            "Trailing-Stop aktualisiert fuer {Symbol}: SL auf {SL:F5} (Gewinn: {Gain:F1} Pips, Trail: {Trail} Pips)",
                            pos.Symbol, trailingSL, gainPips, Settings.TrailingStopPips);
                    }
                }
            }

            // ── 3. Lokaler Stop-Loss (Fallback) ───────────────────────
            if (priceDiff < 0 && lossPercent >= Settings.StopLossPercent)
            {
                _logger.LogWarning(
                    "STOP-LOSS triggered for {Symbol}: loss {Loss:F1}% >= {Max:F1}%",
                    pos.Symbol, lossPercent, Settings.StopLossPercent);

                var positionIdOrSymbol = pos.BrokerPositionId ?? pos.Symbol;
                var closeAction = isBuy ? TradeAction.Sell : TradeAction.Buy;
                var success = await _broker.ClosePositionAsync(positionIdOrSymbol, null, ct);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

                db.Trades.Add(new Trade
                {
                    Symbol = pos.Symbol,
                    Action = closeAction,
                    Status = success ? TradeStatus.Executed : TradeStatus.Failed,
                    Quantity = pos.Quantity,
                    Price = currentPrice,
                    ExecutedPrice = success ? currentPrice : null,
                    ClaudeReasoning = $"Automatischer Stop-Loss bei {lossPercent:F1}% Verlust ({gainPips:F1} Pips)",
                    ClaudeConfidence = 1.0,
                    ExecutedAt = success ? DateTime.UtcNow : null,
                    BrokerPositionId = pos.BrokerPositionId
                });

                db.TradingLogs.Add(new TradingLog
                {
                    Level = "Warning",
                    Source = "RiskManager",
                    Message = $"Stop-Loss: Close {pos.Quantity:F2} Lots {pos.Symbol} @ {currentPrice:F5} (Verlust: {lossPercent:F1}%)"
                });

                await db.SaveChangesAsync(ct);
            }
        }
    }

    public async Task RecordDailyPnLAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var portfolioValue = await _broker.GetPortfolioValueAsync(ct);
        var todayTrades = await db.Trades
            .Where(t => t.CreatedAt.Date == DateTime.UtcNow.Date)
            .CountAsync(ct);

        var existing = await db.DailyPnLs.FirstOrDefaultAsync(d => d.Date == today, ct);

        if (existing != null)
        {
            existing.PortfolioValue = portfolioValue;
            existing.TradeCount = todayTrades;
        }
        else
        {
            db.DailyPnLs.Add(new DailyPnL
            {
                Date = today,
                PortfolioValue = portfolioValue,
                TradeCount = todayTrades
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Lot-Größe je nach Instrument-Typ. Forex=100k, Gold=100oz, Indizes=1 Kontrakt.</summary>
    private static decimal GetLotSize(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        // Edelmetalle: 1 Lot = 100 Einheiten (oz)
        if (s.StartsWith("XAU") || s.StartsWith("XAG") || s.StartsWith("XPT") || s.StartsWith("XPD"))
            return 100m;
        // Indizes/CFDs: 1 Lot = 1 Kontrakt (Preis × 1)
        if (s.Contains("100") || s.Contains("500") || s.Contains("30") || s.Contains("50") ||
            s.StartsWith("US") || s.StartsWith("UK") || s.StartsWith("DE") || s.StartsWith("JP") ||
            s.StartsWith("CHN") || s.StartsWith("AUS"))
            return 1m;
        // Öl: 1 Lot = 1000 Barrel
        if (s.StartsWith("XTI") || s.StartsWith("XBR") || s.Contains("OIL") || s.Contains("WTI") || s.Contains("BRENT"))
            return 1000m;
        // Forex Standard: 1 Lot = 100.000 Einheiten
        return 100_000m;
    }

    private async Task<bool> IsDailyLossExceededAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var yesterdayRecord = await db.DailyPnLs
            .FirstOrDefaultAsync(d => d.Date == yesterday, ct);

        if (yesterdayRecord == null)
            return false;

        var currentValue = await _broker.GetPortfolioValueAsync(ct);
        var dailyLoss = yesterdayRecord.PortfolioValue - currentValue;

        // Absoluter Verlust-Check
        if (dailyLoss >= Settings.MaxDailyLossAbsolute)
        {
            _logger.LogCritical("Daily loss ${Loss:F2} exceeds absolute limit ${Max:F2}",
                dailyLoss, Settings.MaxDailyLossAbsolute);
            return true;
        }

        // Prozentualer Verlust-Check
        var lossPercent = yesterdayRecord.PortfolioValue > 0
            ? (double)(dailyLoss / yesterdayRecord.PortfolioValue) * 100.0
            : 0.0;

        if (lossPercent >= Settings.MaxDailyLossPercent)
        {
            _logger.LogCritical("Daily loss {Loss:F1}% exceeds limit {Max:F1}%",
                lossPercent, Settings.MaxDailyLossPercent);
            return true;
        }

        return false;
    }
}
