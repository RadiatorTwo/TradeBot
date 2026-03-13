using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

public class TradingEngine : BackgroundService
{
    private readonly IClaudeService _claude;
    private readonly IBrokerService _broker;
    private readonly IRiskManager _risk;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RiskSettings _riskSettings;
    private readonly IConfiguration _config;
    private readonly ILogger<TradingEngine> _logger;

    private bool _isRunning;
    public bool IsRunning => _isRunning;

    // Externe Steuerung
    private bool _pauseRequested;
    public void Pause() => _pauseRequested = true;
    public void Resume() => _pauseRequested = false;

    public TradingEngine(
        IClaudeService claude,
        IBrokerService broker,
        IRiskManager risk,
        IServiceScopeFactory scopeFactory,
        IOptions<RiskSettings> riskSettings,
        IConfiguration config,
        ILogger<TradingEngine> logger)
    {
        _claude = claude;
        _broker = broker;
        _risk = risk;
        _scopeFactory = scopeFactory;
        _riskSettings = riskSettings.Value;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Trading Engine starting...");

        // Datenbank initialisieren
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync(stoppingToken);
        }

        // Broker-Verbindung herstellen
        await _broker.ConnectAsync(stoppingToken);

        await LogAsync("Info", "TradingEngine", "Trading Engine gestartet");

        var interval = TimeSpan.FromMinutes(_riskSettings.TradingIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_pauseRequested || _risk.IsKillSwitchActive)
                {
                    _isRunning = false;
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                _isRunning = true;

                // Haupt-Trading-Zyklus
                await RunTradingCycleAsync(stoppingToken);

                // Stop-Losses prüfen
                await _risk.CheckStopLossesAsync(stoppingToken);

                // Tages-PnL aufzeichnen
                await _risk.RecordDailyPnLAsync(stoppingToken);

                _logger.LogInformation("Cycle complete. Next run in {Min} minutes.", _riskSettings.TradingIntervalMinutes);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in trading cycle");
                await LogAsync("Error", "TradingEngine", $"Fehler im Trading-Zyklus: {ex.Message}");
            }

            await Task.Delay(interval, stoppingToken);
        }

        await _broker.DisconnectAsync();
        _isRunning = false;
        _logger.LogInformation("Trading Engine stopped.");
    }

    private async Task RunTradingCycleAsync(CancellationToken ct)
    {
        var watchList = _config.GetSection("TradingStrategy:WatchList").Get<string[]>() ?? Array.Empty<string>();
        var cash = await _broker.GetAccountCashAsync(ct);
        var portfolioValue = await _broker.GetPortfolioValueAsync(ct);
        var positions = await _broker.GetPositionsAsync(ct);

        _logger.LogInformation(
            "Starting analysis cycle: {Count} symbols, Cash: ${Cash:F2}, Portfolio: ${PV:F2}",
            watchList.Length, cash, portfolioValue);

        foreach (var symbol in watchList)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await AnalyzeAndTradeAsync(symbol, cash, portfolioValue, positions, ct);

                // Kurze Pause zwischen Analysen (Rate Limiting)
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing {Symbol}", symbol);
            }
        }
    }

    private async Task AnalyzeAndTradeAsync(
        string symbol, decimal cash, decimal portfolioValue,
        List<Position> positions, CancellationToken ct)
    {
        var currentPrice = await _broker.GetCurrentPriceAsync(symbol, ct);
        var (bid, ask) = await _broker.GetBidAskAsync(symbol, ct);
        var recentPrices = await _broker.GetRecentPricesAsync(symbol, 20, ct);
        var candles1D = await _broker.GetPriceHistoryAsync(symbol, "1D", 30, ct);
        var candles4H = await _broker.GetPriceHistoryAsync(symbol, "4H", 30, ct);
        var candles1H = await _broker.GetPriceHistoryAsync(symbol, "1H", 30, ct);
        var currentPosition = positions.FirstOrDefault(p => p.Symbol == symbol);

        // Claude um Analyse bitten (Forex/CFD: Bid/Ask, Candles, Lots)
        var request = new ClaudeAnalysisRequest
        {
            Symbol = symbol,
            CurrentPrice = currentPrice,
            Bid = bid,
            Ask = ask,
            DayChange = recentPrices.Count > 1 && recentPrices.First() != 0
                ? ((currentPrice - recentPrices.First()) / recentPrices.First()) * 100
                : 0,
            Volume = 0, // Forex/CFD: optional
            RecentPrices = recentPrices,
            Candles1D = candles1D,
            Candles4H = candles4H,
            Candles1H = candles1H,
            CurrentPosition = currentPosition,
            AvailableCash = cash,
            PortfolioValue = portfolioValue
        };

        _logger.LogDebug(
            "LLM-Input für {Symbol}: Price={Price:F4}, Bid={Bid:F4}, Ask={Ask:F4}, " +
            "RecentPrices={Recent}, Candles1D={C1D}, Candles4H={C4H}, Candles1H={C1H}",
            symbol, currentPrice, bid, ask,
            recentPrices.Count, candles1D.Count, candles4H.Count, candles1H.Count);

        var recommendation = await _claude.AnalyzeAsync(request, ct);

        if (recommendation == null)
        {
            _logger.LogWarning("No recommendation received for {Symbol}", symbol);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var action = recommendation.Action.Equals("buy", StringComparison.OrdinalIgnoreCase)
            ? TradeAction.Buy
            : TradeAction.Sell;

        // Gegenrichtung erkennen: LLM empfiehlt buy aber wir haben sell-Positionen (oder umgekehrt)
        // → bestehende Positionen schließen
        var oppositePositions = positions
            .Where(p => p.Symbol == symbol && IsOppositeDirection(p.Side, recommendation.Action))
            .ToList();

        if (oppositePositions.Count > 0 && !recommendation.Action.Equals("hold", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pos in oppositePositions)
            {
                var posId = pos.BrokerPositionId ?? pos.Symbol;
                _logger.LogInformation(
                    "Schließe {Side}-Position {Symbol} ({Qty} Lots, ID: {PosId}) – LLM empfiehlt {Action}",
                    pos.Side, pos.Symbol, pos.Quantity, posId, recommendation.Action.ToUpper());

                var closeSuccess = await _broker.ClosePositionAsync(posId, null, ct);

                db.Trades.Add(new Trade
                {
                    Symbol = symbol,
                    Action = pos.Side == "buy" ? TradeAction.Sell : TradeAction.Buy,
                    Quantity = pos.Quantity,
                    Price = currentPrice,
                    ExecutedPrice = closeSuccess ? currentPrice : null,
                    ExecutedAt = closeSuccess ? DateTime.UtcNow : null,
                    Status = closeSuccess ? TradeStatus.Executed : TradeStatus.Failed,
                    ClaudeReasoning = $"Position geschlossen: LLM empfiehlt {recommendation.Action.ToUpper()} (Confidence: {recommendation.Confidence:P0}). {recommendation.Reasoning}",
                    ClaudeConfidence = recommendation.Confidence,
                    BrokerPositionId = pos.BrokerPositionId,
                    ErrorMessage = closeSuccess ? null : "Close-Order fehlgeschlagen"
                });

                db.TradingLogs.Add(new TradingLog
                {
                    Level = closeSuccess ? "Info" : "Error",
                    Source = "TradingEngine",
                    Message = $"{(closeSuccess ? "✅" : "❌")} Close {pos.Side.ToUpper()} {pos.Quantity:F2} Lots {symbol} @ {currentPrice:F4} (LLM: {recommendation.Action.ToUpper()})"
                });
            }

            await db.SaveChangesAsync(ct);

            // Nach dem Schließen: wenn LLM auch eine neue Position empfiehlt, weiter machen
            // Sonst hier beenden
            if (recommendation.Confidence < _riskSettings.MinConfidence)
                return;
        }

        // Risk Check
        var isValid = await _risk.ValidateTradeAsync(recommendation, ct);

        if (!isValid || recommendation.Action.Equals("hold", StringComparison.OrdinalIgnoreCase))
        {
            if (!recommendation.Action.Equals("hold", StringComparison.OrdinalIgnoreCase) && !isValid)
            {
                db.Trades.Add(new Trade
                {
                    Symbol = symbol,
                    Action = action,
                    Quantity = recommendation.Quantity,
                    Price = currentPrice,
                    ClaudeReasoning = recommendation.Reasoning,
                    ClaudeConfidence = recommendation.Confidence,
                    Status = TradeStatus.Rejected,
                    ErrorMessage = "Trade rejected by RiskManager"
                });
            }
            db.TradingLogs.Add(new TradingLog
            {
                Source = "Claude",
                Message = $"{symbol}: {recommendation.Action.ToUpper()} (Confidence: {recommendation.Confidence:P0})",
                Details = recommendation.Reasoning
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        // Gleiche Richtung: nicht doppelt öffnen wenn wir schon eine Position haben
        var sameDirectionPositions = positions
            .Where(p => p.Symbol == symbol && !IsOppositeDirection(p.Side, recommendation.Action))
            .ToList();

        if (sameDirectionPositions.Count > 0)
        {
            _logger.LogInformation(
                "Überspringe neue {Action}-Order für {Symbol} – bereits {Count} Position(en) in gleicher Richtung offen.",
                recommendation.Action.ToUpper(), symbol, sameDirectionPositions.Count);
            db.TradingLogs.Add(new TradingLog
            {
                Source = "TradingEngine",
                Message = $"{symbol}: {recommendation.Action.ToUpper()} übersprungen – bereits Position offen",
                Details = recommendation.Reasoning
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        // Neue Position eröffnen (Lots, StopLoss, TakeProfit)
        var trade = new Trade
        {
            Symbol = symbol,
            Action = action,
            Quantity = recommendation.Quantity,
            Price = currentPrice,
            ClaudeReasoning = recommendation.Reasoning,
            ClaudeConfidence = recommendation.Confidence,
            Status = TradeStatus.Pending
        };

        var result = await _broker.PlaceOrderAsync(
            symbol, action, recommendation.Quantity,
            recommendation.StopLossPrice, recommendation.TakeProfitPrice, ct);

        trade.Status = result.Success ? TradeStatus.Executed : TradeStatus.Failed;
        trade.ExecutedPrice = result.Success ? currentPrice : null;
        trade.ExecutedAt = result.Success ? DateTime.UtcNow : null;
        trade.BrokerOrderId = result.BrokerOrderId;
        trade.BrokerPositionId = result.BrokerPositionId;
        if (!result.Success)
            trade.ErrorMessage = "Order execution failed at broker";

        db.Trades.Add(trade);

        db.TradingLogs.Add(new TradingLog
        {
            Level = result.Success ? "Info" : "Error",
            Source = "TradingEngine",
            Message = $"{(result.Success ? "✅" : "❌")} {action} {recommendation.Quantity:F2} Lots {symbol} @ {currentPrice:F4}",
            Details = recommendation.Reasoning
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Prüft ob Position-Side und LLM-Empfehlung in entgegengesetzte Richtungen zeigen.</summary>
    private static bool IsOppositeDirection(string positionSide, string recommendedAction)
    {
        var isBuyPosition = positionSide.Equals("buy", StringComparison.OrdinalIgnoreCase);
        var isSellRecommendation = recommendedAction.Equals("sell", StringComparison.OrdinalIgnoreCase);
        var isSellPosition = positionSide.Equals("sell", StringComparison.OrdinalIgnoreCase);
        var isBuyRecommendation = recommendedAction.Equals("buy", StringComparison.OrdinalIgnoreCase);

        return (isBuyPosition && isSellRecommendation) || (isSellPosition && isBuyRecommendation);
    }

    private async Task LogAsync(string level, string source, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.TradingLogs.Add(new TradingLog { Level = level, Source = source, Message = message });
        await db.SaveChangesAsync();
    }
}
