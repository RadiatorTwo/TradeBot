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
    private readonly RiskSettings _settings;
    private readonly IBrokerService _broker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RiskManager> _logger;

    private bool _killSwitchActive;
    private string? _killSwitchReason;

    public bool IsKillSwitchActive => _killSwitchActive;

    public RiskManager(
        IOptions<RiskSettings> settings,
        IBrokerService broker,
        IServiceScopeFactory scopeFactory,
        ILogger<RiskManager> logger)
    {
        _settings = settings.Value;
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

        // 3. Confidence-Schwelle
        if (rec.Confidence < 0.6)
        {
            _logger.LogInformation("Trade skipped – confidence too low: {Conf:P0}", rec.Confidence);
            return false;
        }

        // 4. Maximale Positionsgröße prüfen (quantity in Lots; Notional = Lots × LotSize × Price)
        var portfolioValue = await _broker.GetPortfolioValueAsync(ct);
        var price = await _broker.GetCurrentPriceAsync(rec.Symbol, ct);
        const decimal standardLotSize = 100_000m; // Forex Standard-Lot
        var tradeValue = rec.Quantity * standardLotSize * price;
        var positionPercent = portfolioValue > 0
            ? (double)(tradeValue / portfolioValue) * 100.0
            : 100.0;

        if (positionPercent > _settings.MaxPositionSizePercent)
        {
            _logger.LogWarning(
                "Trade rejected – position size {Pct:F1}% exceeds max {Max:F1}%",
                positionPercent, _settings.MaxPositionSizePercent);
            return false;
        }

        // 5. Max offene Positionen prüfen (nur bei Kauf)
        if (rec.Action.Equals("buy", StringComparison.OrdinalIgnoreCase))
        {
            var positions = await _broker.GetPositionsAsync(ct);
            if (positions.Count >= _settings.MaxOpenPositions &&
                !positions.Any(p => p.Symbol == rec.Symbol))
            {
                _logger.LogWarning(
                    "Trade rejected – max open positions ({Max}) reached",
                    _settings.MaxOpenPositions);
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
            var lossPercent = pos.AveragePrice > 0
                ? (double)((pos.AveragePrice - currentPrice) / pos.AveragePrice) * 100.0
                : 0.0;

            if (lossPercent >= _settings.StopLossPercent)
            {
                _logger.LogWarning(
                    "🛑 STOP-LOSS triggered for {Symbol}: loss {Loss:F1}% >= {Max:F1}%",
                    pos.Symbol, lossPercent, _settings.StopLossPercent);

                var success = await _broker.PlaceOrderAsync(pos.Symbol, TradeAction.Sell, pos.Quantity, ct);

                // Trade in DB loggen
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

                db.Trades.Add(new Trade
                {
                    Symbol = pos.Symbol,
                    Action = TradeAction.Sell,
                    Status = success ? TradeStatus.Executed : TradeStatus.Failed,
                    Quantity = pos.Quantity,
                    Price = currentPrice,
                    ExecutedPrice = success ? currentPrice : null,
                    ClaudeReasoning = $"Automatischer Stop-Loss bei {lossPercent:F1}% Verlust",
                    ClaudeConfidence = 1.0,
                    ExecutedAt = success ? DateTime.UtcNow : null
                });

                db.TradingLogs.Add(new TradingLog
                {
                    Level = "Warning",
                    Source = "RiskManager",
                    Message = $"Stop-Loss: SELL {pos.Quantity}x {pos.Symbol} @ ${currentPrice:F2} (Verlust: {lossPercent:F1}%)"
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
        if (dailyLoss >= _settings.MaxDailyLossAbsolute)
        {
            _logger.LogCritical("Daily loss ${Loss:F2} exceeds absolute limit ${Max:F2}",
                dailyLoss, _settings.MaxDailyLossAbsolute);
            return true;
        }

        // Prozentualer Verlust-Check
        var lossPercent = yesterdayRecord.PortfolioValue > 0
            ? (double)(dailyLoss / yesterdayRecord.PortfolioValue) * 100.0
            : 0.0;

        if (lossPercent >= _settings.MaxDailyLossPercent)
        {
            _logger.LogCritical("Daily loss {Loss:F1}% exceeds limit {Max:F1}%",
                lossPercent, _settings.MaxDailyLossPercent);
            return true;
        }

        return false;
    }
}
