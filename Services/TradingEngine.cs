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
            DayChange = recentPrices.Count > 1
                ? ((currentPrice - recentPrices.Last()) / recentPrices.Last()) * 100
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

        var recommendation = await _claude.AnalyzeAsync(request, ct);

        if (recommendation == null)
        {
            _logger.LogWarning("No recommendation received for {Symbol}", symbol);
            return;
        }

        // Risk Check
        var isValid = await _risk.ValidateTradeAsync(recommendation, ct);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var action = recommendation.Action.Equals("buy", StringComparison.OrdinalIgnoreCase)
            ? TradeAction.Buy
            : TradeAction.Sell;

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
                    Status = TradeStatus.Failed,
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

        // Trade ausführen (Lots, StopLoss, TakeProfit)
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

    private async Task LogAsync(string level, string source, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.TradingLogs.Add(new TradingLog { Level = level, Source = source, Message = message });
        await db.SaveChangesAsync();
    }
}
