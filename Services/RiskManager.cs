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
    Task<bool> ValidateTradeAsync(ClaudeTradeRecommendation recommendation, decimal bid, decimal ask, CancellationToken ct = default);
    Task<double> GetDynamicMinConfidenceAsync(string symbol, CancellationToken ct = default);
    Task CheckStopLossesAsync(CancellationToken ct = default);
    Task RecordDailyPnLAsync(CancellationToken ct = default);
}

public class RiskManager : IRiskManager
{
    private readonly IOptionsMonitor<RiskSettings> _settingsMonitor;
    private readonly IBrokerService _broker;
    private readonly NotificationService _notification;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RiskManager> _logger;

    /// <summary>Account-ID fuer Multi-Account-Support (Phase 7.1).</summary>
    public string AccountId { get; init; } = "default";

    private volatile bool _killSwitchActive;
    private string? _killSwitchReason;

    /// <summary>BrokerPositionIds die bereits partial-closed wurden (verhindert doppelten Partial Close).</summary>
    private readonly HashSet<string> _partialClosedPositions = new();

    private RiskSettings Settings => _settingsMonitor.CurrentValue;

    public bool IsKillSwitchActive => _killSwitchActive;

    public RiskManager(
        IOptionsMonitor<RiskSettings> settings,
        IBrokerService broker,
        NotificationService notification,
        IServiceScopeFactory scopeFactory,
        ILogger<RiskManager> logger)
    {
        _settingsMonitor = settings;
        _broker = broker;
        _notification = notification;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void ActivateKillSwitch(string reason)
    {
        _killSwitchActive = true;
        _killSwitchReason = reason;
        _logger.LogCritical("KILL SWITCH ACTIVATED: {Reason}", reason);
        _ = _notification.SendAlertAsync($"Kill Switch aktiviert: {reason}");
    }

    public void ResetKillSwitch()
    {
        _killSwitchActive = false;
        _killSwitchReason = null;
        _logger.LogWarning("Kill switch has been manually reset");
    }

    public Task<bool> ValidateTradeAsync(ClaudeTradeRecommendation rec, CancellationToken ct = default)
        => ValidateTradeAsync(rec, 0, 0, ct);

    public async Task<bool> ValidateTradeAsync(ClaudeTradeRecommendation rec, decimal bid, decimal ask, CancellationToken ct = default)
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

        // 3. Confidence-Schwelle (statisch oder dynamisch)
        var minConfidence = Settings.DynamicConfidenceEnabled
            ? await GetDynamicMinConfidenceAsync(rec.Symbol, ct)
            : Settings.MinConfidence;

        if (rec.Confidence < minConfidence)
        {
            _logger.LogInformation("Trade skipped – confidence {Conf:P0} below MinConfidence {Min:P0}{Dynamic}",
                rec.Confidence, minConfidence, Settings.DynamicConfidenceEnabled ? " (dynamisch)" : "");
            return false;
        }

        // 4. Spread-Filter (Phase 8.1)
        if (Settings.MaxSpreadPips > 0 && bid > 0 && ask > 0)
        {
            var spreadPips = (double)PipCalculator.PriceToPips(rec.Symbol, ask - bid);
            if (spreadPips > Settings.MaxSpreadPips)
            {
                _logger.LogInformation(
                    "Trade rejected – spread {Spread:F1} Pips exceeds max {Max:F1} Pips for {Symbol}",
                    spreadPips, Settings.MaxSpreadPips, rec.Symbol);
                return false;
            }
        }

        // Broker-Daten einmalig laden (vermeidet mehrfache API-Aufrufe)
        var portfolioValue = await _broker.GetPortfolioValueAsync(ct);
        var price = await _broker.GetCurrentPriceAsync(rec.Symbol, ct);
        var positions = await _broker.GetPositionsAsync(ct);

        // 4. Maximale Positionsgröße prüfen (quantity in Lots; Notional = Lots × LotSize × Price)
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
        var normalizedAction = rec.Action.Replace("_limit", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_stop", "", StringComparison.OrdinalIgnoreCase);
        if (normalizedAction.Equals("buy", StringComparison.OrdinalIgnoreCase))
        {
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
        if (await IsDailyLossExceededAsync(portfolioValue, ct))
        {
            ActivateKillSwitch("Maximum daily loss exceeded");
            return false;
        }

        // 7. Drawdown vom Peak prüfen (Phase 4.1) – Kill Switch, daher vor Weekly/Monthly
        if (Settings.MaxDrawdownPercent > 0 && await IsDrawdownExceededAsync(portfolioValue, ct))
        {
            ActivateKillSwitch($"Maximum drawdown from peak ({Settings.MaxDrawdownPercent:F1}%) exceeded");
            return false;
        }

        // 8. Wochenverlust prüfen (Phase 4.3)
        if (Settings.MaxWeeklyLossPercent > 0 && await IsWeeklyLossExceededAsync(portfolioValue, ct))
        {
            _ = _notification.SendAlertAsync($"Wochenverlust-Limit ({Settings.MaxWeeklyLossPercent:F1}%) erreicht – neue Trades blockiert");
            return false;
        }

        // 9. Monatsverlust prüfen (Phase 4.3)
        if (Settings.MaxMonthlyLossPercent > 0 && await IsMonthlyLossExceededAsync(portfolioValue, ct))
        {
            _ = _notification.SendAlertAsync($"Monatsverlust-Limit ({Settings.MaxMonthlyLossPercent:F1}%) erreicht – neue Trades blockiert");
            return false;
        }

        // 10. Korrelationscheck (Phase 4.2)
        if (Settings.MaxCorrelatedExposurePercent > 0)
        {
            var correlatedExposure = GetCorrelatedExposurePercent(rec, positions, portfolioValue, price);
            if (correlatedExposure > Settings.MaxCorrelatedExposurePercent)
            {
                _logger.LogWarning(
                    "Trade rejected – correlated exposure {Exp:F1}% exceeds max {Max:F1}% for {Symbol}",
                    correlatedExposure, Settings.MaxCorrelatedExposurePercent, rec.Symbol);
                return false;
            }
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

            // ── 2. Partial Close (Phase 6.4) ─────────────────────────────
            if (Settings.PartialClosePercent > 0
                && gainPips >= Settings.PartialCloseTriggerPips
                && pos.BrokerPositionId != null
                && !_partialClosedPositions.Contains(pos.BrokerPositionId))
            {
                var closeQty = Math.Round(pos.Quantity * (decimal)Settings.PartialClosePercent, 2);
                if (closeQty >= 0.01m)
                {
                    var success = await _broker.ClosePositionAsync(pos.BrokerPositionId, closeQty, ct);
                    if (success)
                    {
                        _partialClosedPositions.Add(pos.BrokerPositionId);
                        _logger.LogInformation(
                            "Partial Close: {Pct:P0} von {Symbol} geschlossen ({CloseQty:F2} von {TotalQty:F2} Lots, Gewinn: {Gain:F1} Pips)",
                            Settings.PartialClosePercent, pos.Symbol, closeQty, pos.Quantity, gainPips);

                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                        db.Trades.Add(new Trade
                        {
                            Symbol = pos.Symbol,
                            Action = isBuy ? TradeAction.Sell : TradeAction.Buy,
                            Status = TradeStatus.Executed,
                            Quantity = closeQty,
                            Price = currentPrice,
                            ExecutedPrice = currentPrice,
                            ExecutedAt = DateTime.UtcNow,
                            ClaudeReasoning = $"Partial Close ({Settings.PartialClosePercent:P0}) bei {gainPips:F1} Pips Gewinn",
                            ClaudeConfidence = 1.0,
                            BrokerPositionId = pos.BrokerPositionId
                        });
                        db.TradingLogs.Add(new TradingLog
                        {
                            Source = "RiskManager",
                            Message = $"Partial Close: {closeQty:F2} Lots {pos.Symbol} @ {currentPrice:F4} ({gainPips:F1} Pips Gewinn)"
                        });
                        await db.SaveChangesAsync(ct);
                    }
                }
            }

            // ── 3. Trailing Stop ───────────────────────────────────────
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

            // ── 4. Lokaler Stop-Loss (Fallback) ───────────────────────
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

        // PeakEquity: Maximum aus bisherigem Peak und aktuellem Wert
        var previousPeak = await db.DailyPnLs
            .OrderByDescending(d => d.Date)
            .Where(d => d.Date < today)
            .Select(d => d.PeakEquity)
            .FirstOrDefaultAsync(ct);
        var currentPeak = Math.Max(previousPeak, portfolioValue);

        var existing = await db.DailyPnLs.FirstOrDefaultAsync(d => d.Date == today, ct);

        if (existing != null)
        {
            existing.PortfolioValue = portfolioValue;
            existing.TradeCount = todayTrades;
            existing.PeakEquity = currentPeak;
        }
        else
        {
            db.DailyPnLs.Add(new DailyPnL
            {
                Date = today,
                PortfolioValue = portfolioValue,
                TradeCount = todayTrades,
                PeakEquity = currentPeak
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

    private async Task<bool> IsDailyLossExceededAsync(decimal currentValue, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var yesterdayRecord = await db.DailyPnLs
            .FirstOrDefaultAsync(d => d.Date == yesterday, ct);

        if (yesterdayRecord == null)
            return false;

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

    // ── Phase 4.1: Drawdown vom Peak ─────────────────────────────────

    private async Task<bool> IsDrawdownExceededAsync(decimal currentValue, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var latestRecord = await db.DailyPnLs
            .OrderByDescending(d => d.Date)
            .FirstOrDefaultAsync(ct);

        if (latestRecord == null || latestRecord.PeakEquity <= 0)
            return false;

        var peakEquity = latestRecord.PeakEquity;
        var drawdown = peakEquity - currentValue;
        var drawdownPercent = drawdown > 0
            ? (double)(drawdown / peakEquity) * 100.0
            : 0.0;

        if (drawdownPercent >= Settings.MaxDrawdownPercent)
        {
            _logger.LogCritical(
                "Drawdown {DD:F1}% from peak equity ${Peak:F2} exceeds limit {Max:F1}%",
                drawdownPercent, peakEquity, Settings.MaxDrawdownPercent);
            return true;
        }

        return false;
    }

    // ── Phase 4.2: Korrelationscheck ─────────────────────────────────

    private double GetCorrelatedExposurePercent(
        ClaudeTradeRecommendation rec, IReadOnlyList<Position> positions,
        decimal portfolioValue, decimal currentPrice)
    {
        if (portfolioValue <= 0) return 0;

        var newLotSize = GetLotSize(rec.Symbol);
        var newTradeValue = rec.Quantity * newLotSize * currentPrice;
        var normalizedDir = rec.Action.Replace("_limit", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_stop", "", StringComparison.OrdinalIgnoreCase);
        var isBuy = normalizedDir.Equals("buy", StringComparison.OrdinalIgnoreCase);

        var correlatedExposure = newTradeValue;

        foreach (var pos in positions)
        {
            var correlation = CorrelationMatrix.GetCorrelation(rec.Symbol, pos.Symbol);
            if (Math.Abs(correlation) < 0.3) continue;

            var posLotSize = GetLotSize(pos.Symbol);
            var posValue = pos.Quantity * posLotSize * pos.CurrentPrice;

            // Gleiche Richtung bei positiver Korrelation = addiert sich
            // Gleiche Richtung bei negativer Korrelation = hebt sich auf
            var posSide = pos.Side.Equals("buy", StringComparison.OrdinalIgnoreCase);
            var sameDirection = isBuy == posSide;
            var effectiveCorrelation = sameDirection ? correlation : -correlation;

            if (effectiveCorrelation > 0)
            {
                correlatedExposure += posValue * (decimal)Math.Abs(effectiveCorrelation);
            }
        }

        return (double)(correlatedExposure / portfolioValue) * 100.0;
    }

    // ── Phase 4.3: Weekly/Monthly Loss Limits ────────────────────────

    /// <summary>Tage seit Montag: So=6, Mo=0, Di=1, ... Sa=5.</summary>
    private static int DaysSinceMonday(DateOnly date) =>
        ((int)date.DayOfWeek + 6) % 7;

    private async Task<bool> IsWeeklyLossExceededAsync(decimal currentValue, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = today.AddDays(-DaysSinceMonday(today));

        // Referenzwert: letzter PnL-Eintrag vor dieser Woche
        var previousRecord = await db.DailyPnLs
            .Where(d => d.Date < weekStart)
            .OrderByDescending(d => d.Date)
            .FirstOrDefaultAsync(ct);

        if (previousRecord == null) return false;

        var weeklyLoss = previousRecord.PortfolioValue - currentValue;
        var weeklyLossPercent = previousRecord.PortfolioValue > 0
            ? (double)(weeklyLoss / previousRecord.PortfolioValue) * 100.0
            : 0.0;

        if (weeklyLossPercent >= Settings.MaxWeeklyLossPercent)
        {
            _logger.LogWarning(
                "Weekly loss {Loss:F1}% exceeds limit {Max:F1}%",
                weeklyLossPercent, Settings.MaxWeeklyLossPercent);
            return true;
        }

        return false;
    }

    private async Task<bool> IsMonthlyLossExceededAsync(decimal currentValue, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        // Referenzwert: letzter PnL-Eintrag vor diesem Monat
        var previousRecord = await db.DailyPnLs
            .Where(d => d.Date < monthStart)
            .OrderByDescending(d => d.Date)
            .FirstOrDefaultAsync(ct);

        if (previousRecord == null) return false;

        var monthlyLoss = previousRecord.PortfolioValue - currentValue;
        var monthlyLossPercent = previousRecord.PortfolioValue > 0
            ? (double)(monthlyLoss / previousRecord.PortfolioValue) * 100.0
            : 0.0;

        if (monthlyLossPercent >= Settings.MaxMonthlyLossPercent)
        {
            _logger.LogWarning(
                "Monthly loss {Loss:F1}% exceeds limit {Max:F1}%",
                monthlyLossPercent, Settings.MaxMonthlyLossPercent);
            return true;
        }

        return false;
    }

    // ── Phase 6.3: Dynamischer Confidence-Threshold ────────────────

    public async Task<double> GetDynamicMinConfidenceAsync(string symbol, CancellationToken ct = default)
    {
        var baseConfidence = Settings.MinConfidence;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var atrAdjustment = 0.0;
        var drawdownAdjustment = 0.0;
        var winRateAdjustment = 0.0;

        // 1. ATR-basiert: hohe Volatilitaet → hoehere Schwelle
        if (Settings.ConfidenceAtrFactor > 0)
        {
            try
            {
                var currentPrice = await _broker.GetCurrentPriceAsync(symbol, ct);
                if (currentPrice > 0)
                {
                    var atr = await GetCurrentAtrAsync(symbol, ct);
                    if (atr > 0)
                    {
                        // ATR als % des Preises – wenn ueber 1% = hohe Volatilitaet
                        var atrPercent = (double)(atr / currentPrice) * 100.0;
                        if (atrPercent > 0.5)
                        {
                            atrAdjustment = Math.Min(Settings.ConfidenceAtrFactor, (atrPercent - 0.5) * Settings.ConfidenceAtrFactor);
                        }
                    }
                }
            }
            catch { /* ATR nicht verfuegbar – kein Adjustment */ }
        }

        // 2. Drawdown-basiert: im Drawdown konservativer
        if (Settings.ConfidenceDrawdownFactor > 0)
        {
            var latestPnl = await db.DailyPnLs
                .OrderByDescending(d => d.Date)
                .FirstOrDefaultAsync(ct);

            if (latestPnl != null && latestPnl.PeakEquity > 0)
            {
                var portfolioValue = await _broker.GetPortfolioValueAsync(ct);
                var drawdownPercent = (double)((latestPnl.PeakEquity - portfolioValue) / latestPnl.PeakEquity) * 100.0;
                if (drawdownPercent > 0)
                {
                    drawdownAdjustment = drawdownPercent * Settings.ConfidenceDrawdownFactor;
                }
            }
        }

        // 3. Win-Rate-basiert: schlechte Win-Rate → hoehere Schwelle
        if (Settings.ConfidenceLossStreakFactor > 0)
        {
            var recentTrades = await db.Trades
                .Where(t => t.Symbol == symbol
                    && t.Status == TradeStatus.Executed
                    && t.RealizedPnL != null)
                .OrderByDescending(t => t.ClosedAt)
                .Take(10)
                .ToListAsync(ct);

            if (recentTrades.Count >= 3)
            {
                var winRate = (double)recentTrades.Count(t => t.RealizedPnL > 0) / recentTrades.Count;
                if (winRate < 0.5)
                {
                    winRateAdjustment = (0.5 - winRate) * Settings.ConfidenceLossStreakFactor * 2;
                }
            }
        }

        var effective = baseConfidence + atrAdjustment + drawdownAdjustment + winRateAdjustment;
        effective = Math.Min(effective, Settings.MaxDynamicConfidence);

        if (effective > baseConfidence)
        {
            _logger.LogDebug(
                "Dynamic confidence for {Symbol}: {Effective:P0} (base={Base:P0}, atr=+{Atr:F3}, dd=+{Dd:F3}, wr=+{Wr:F3})",
                symbol, effective, baseConfidence, atrAdjustment, drawdownAdjustment, winRateAdjustment);
        }

        return effective;
    }

    /// <summary>Aktuellen ATR(14) fuer ein Symbol abrufen.</summary>
    private async Task<decimal> GetCurrentAtrAsync(string symbol, CancellationToken ct)
    {
        var candles = await _broker.GetCandlesAsync(symbol, "1H", 30, ct);
        if (candles.Count < 15)
            return 0;

        var highs = candles.Select(c => c.High).ToList();
        var lows = candles.Select(c => c.Low).ToList();
        var closes = candles.Select(c => c.Close).ToList();

        // ATR(14) berechnen
        var trueRanges = new List<decimal>();
        for (int i = 1; i < candles.Count; i++)
        {
            var hl = highs[i] - lows[i];
            var hc = Math.Abs(highs[i] - closes[i - 1]);
            var lc = Math.Abs(lows[i] - closes[i - 1]);
            trueRanges.Add(Math.Max(hl, Math.Max(hc, lc)));
        }

        if (trueRanges.Count < 14)
            return 0;

        return trueRanges.TakeLast(14).Average();
    }
}
