using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

public class TradingEngine : BackgroundService
{
    private readonly IClaudeService _claude;
    private readonly IBrokerService _broker;
    private readonly IRiskManager _risk;
    private readonly TechnicalAnalysisService _ta;
    private readonly TradingSessionService _session;
    private readonly MarketHoursService _marketHours;
    private readonly EconomicCalendarService _calendar;
    private readonly NewsSentimentService _news;
    private readonly PaperTradingBrokerDecorator _paperTrading;
    private readonly NotificationService _notification;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<RiskSettings> _settingsMonitor;
    private readonly IOptionsMonitor<MultiTimeframeSettings> _mtfSettings;
    private readonly IConfiguration _config;
    private readonly ILogger<TradingEngine> _logger;

    private RiskSettings Settings => _settingsMonitor.CurrentValue;

    /// <summary>Account-ID fuer Multi-Account-Support (Phase 7.1).</summary>
    public string AccountId { get; init; } = "default";

    /// <summary>Per-Account Watchlist. Leer = globale Watchlist aus Config.</summary>
    public string[] AccountWatchList { get; set; } = Array.Empty<string>();

    /// <summary>Optionaler Custom-System-Prompt (Phase 7.3).</summary>
    public string StrategyPrompt { get; set; } = string.Empty;

    private volatile bool _isRunning;
    public bool IsRunning => _isRunning;

    // Externe Steuerung
    private volatile bool _pauseRequested;
    public bool IsPaused => _pauseRequested;
    public void Pause() => _pauseRequested = true;
    public void Resume() => _pauseRequested = false;

    public TradingEngine(
        IClaudeService claude,
        IBrokerService broker,
        IRiskManager risk,
        TechnicalAnalysisService ta,
        TradingSessionService session,
        MarketHoursService marketHours,
        EconomicCalendarService calendar,
        NewsSentimentService news,
        PaperTradingBrokerDecorator paperTrading,
        NotificationService notification,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RiskSettings> settingsMonitor,
        IOptionsMonitor<MultiTimeframeSettings> mtfSettings,
        IConfiguration config,
        ILogger<TradingEngine> logger)
    {
        _claude = claude;
        _broker = broker;
        _risk = risk;
        _ta = ta;
        _session = session;
        _marketHours = marketHours;
        _calendar = calendar;
        _news = news;
        _paperTrading = paperTrading;
        _notification = notification;
        _scopeFactory = scopeFactory;
        _settingsMonitor = settingsMonitor;
        _mtfSettings = mtfSettings;
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

                _logger.LogInformation("Cycle complete. Next run in {Min} minutes.", Settings.TradingIntervalMinutes);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in trading cycle");
                await LogAsync("Error", "TradingEngine", $"Fehler im Trading-Zyklus: {ex.Message}");

                // Auto-Reconnect: bei Verbindungsverlust erneut verbinden
                if (!_broker.IsConnected)
                {
                    _logger.LogWarning("Broker-Verbindung verloren. Versuche Reconnect...");
                    try
                    {
                        await _broker.ConnectAsync(stoppingToken);
                    }
                    catch (Exception reconnectEx)
                    {
                        _logger.LogWarning(reconnectEx, "Reconnect im TradingEngine fehlgeschlagen.");
                    }
                }
            }

            // Interval bei jedem Durchlauf neu lesen (Config-Reload)
            await Task.Delay(TimeSpan.FromMinutes(Settings.TradingIntervalMinutes), stoppingToken);
        }

        await _broker.DisconnectAsync();
        _isRunning = false;
        _logger.LogInformation("Trading Engine stopped.");
    }

    private async Task RunTradingCycleAsync(CancellationToken ct)
    {
        var isPaper = _paperTrading.IsPaperTradingActive;

        // Markt-Check: wenn Forex-Markt komplett geschlossen, gesamten Zyklus ueberspringen
        // Im Paper-Trading-Modus wird der Markt-Check uebersprungen
        if (!isPaper && !_marketHours.IsMarketOpen())
        {
            var nextOpen = _marketHours.GetNextOpen("EURUSD");
            _logger.LogInformation("Markt geschlossen. Naechste Oeffnung: {NextOpen:dd.MM.yyyy HH:mm} UTC",
                nextOpen);
            return;
        }

        var watchList = AccountWatchList.Length > 0
            ? AccountWatchList
            : _config.GetSection("TradingStrategy:WatchList").Get<string[]>() ?? Array.Empty<string>();
        var cash = await _broker.GetAccountCashAsync(ct);
        var portfolioValue = await _broker.GetPortfolioValueAsync(ct);
        var positions = await _broker.GetPositionsAsync(ct);

        _logger.LogDebug(
            "Starting analysis cycle: {Count} symbols, Cash: ${Cash:F2}, Portfolio: ${PV:F2}",
            watchList.Length, cash, portfolioValue);

        foreach (var symbol in watchList)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Markt-/Session-Checks (im Paper-Trading-Modus uebersprungen)
                if (!isPaper)
                {
                    if (!_marketHours.IsMarketOpen(symbol))
                    {
                        _logger.LogDebug("Ueberspringe {Symbol} – Markt geschlossen", symbol);
                        continue;
                    }

                    if (!_marketHours.IsSafeToOpenPosition(symbol))
                    {
                        _logger.LogDebug("Ueberspringe {Symbol} – zu nah am Marktschluss", symbol);
                        continue;
                    }

                    if (!_session.IsSessionActive(symbol))
                    {
                        _logger.LogDebug("Ueberspringe {Symbol} – ausserhalb der erlaubten Trading-Session", symbol);
                        continue;
                    }
                }

                // News-Filter: Symbol ueberspringen wenn High-Impact-Event bevorsteht
                if (_calendar.IsSymbolAffectedByEvent(symbol))
                {
                    _logger.LogInformation(
                        "Ueberspringe {Symbol} – High-Impact-Event in der Naehe", symbol);
                    continue;
                }

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

        // Preis-Validierung: wenn Broker 0 liefert, Symbol ueberspringen
        if (currentPrice == 0)
        {
            _logger.LogWarning("Preis fuer {Symbol} ist 0 – ueberspringe (Markt evtl. geschlossen)", symbol);
            return;
        }

        var (bid, ask) = await _broker.GetBidAskAsync(symbol, ct);
        var recentPrices = await _broker.GetRecentPricesAsync(symbol, 20, ct);
        var candles1D = await _broker.GetPriceHistoryAsync(symbol, "1D", 30, ct);
        var candles4H = await _broker.GetPriceHistoryAsync(symbol, "4H", 30, ct);
        var candles1H = await _broker.GetPriceHistoryAsync(symbol, "1H", 30, ct);
        var currentPosition = positions.FirstOrDefault(p => p.Symbol == symbol);

        // Technische Indikatoren berechnen (1H-Candles fuer OHLC, mehr Daten fuer EMA200)
        TechnicalIndicators? indicators = null;
        try
        {
            var ohlcCandles = await _broker.GetCandlesAsync(symbol, "1H", 210, ct);
            if (ohlcCandles.Count > 0)
            {
                var closes = ohlcCandles.Select(c => c.Close).ToList();
                var highs = ohlcCandles.Select(c => c.High).ToList();
                var lows = ohlcCandles.Select(c => c.Low).ToList();
                indicators = _ta.Calculate(closes, highs, lows);
                _logger.LogDebug(
                    "Indikatoren fuer {Symbol}: RSI={RSI}, EMA20={EMA20}, MACD={MACD}, ATR={ATR}",
                    symbol, indicators.RSI14, indicators.EMA20, indicators.MACDLine, indicators.ATR14);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fehler bei Indikator-Berechnung fuer {Symbol}", symbol);
        }

        // ── Feedback-Loop: Letzte geschlossene Trades fuer dieses Symbol laden ──
        var recentTradeResults = await LoadRecentTradeResultsAsync(symbol, 10, ct);

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
            Indicators = indicators,
            CurrentPosition = currentPosition,
            AvailableCash = cash,
            PortfolioValue = portfolioValue,
            RecentTradeResults = recentTradeResults,
            NewsHeadlines = _news.GetHeadlines(symbol),
            StrategyPrompt = StrategyPrompt
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

        // ── Multi-Timeframe-Filter (Phase 5.3) ──────────────────────────
        if (!recommendation.Action.Equals("hold", StringComparison.OrdinalIgnoreCase))
        {
            var mtf = _mtfSettings.CurrentValue;
            if (mtf.Enabled)
            {
                var blocked = await CheckMultiTimeframeFilter(symbol, recommendation.Action, mtf, ct);
                if (blocked)
                {
                    _logger.LogInformation(
                        "Multi-Timeframe-Filter: {Action} {Symbol} blockiert – gegen hoeheren Timeframe-Trend",
                        recommendation.Action.ToUpper(), symbol);
                    return;
                }
            }
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
                    AccountId = AccountId,
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
                    AccountId = AccountId,
                    Level = closeSuccess ? "Info" : "Error",
                    Source = "TradingEngine",
                    Message = $"{(closeSuccess ? "✅" : "❌")} Close {pos.Side.ToUpper()} {pos.Quantity:F2} Lots {symbol} @ {currentPrice:F4} (LLM: {recommendation.Action.ToUpper()})"
                });
            }

            await db.SaveChangesAsync(ct);

            // Nach dem Schließen: wenn LLM auch eine neue Position empfiehlt, weiter machen
            // Sonst hier beenden
            if (recommendation.Confidence < Settings.MinConfidence)
                return;
        }

        // Spread berechnen (fuer Risk Check und Trade-Log)
        var spreadPips = (bid > 0 && ask > 0)
            ? PipCalculator.PriceToPips(symbol, ask - bid)
            : 0m;

        // Risk Check (mit Spread-Daten)
        var isValid = await _risk.ValidateTradeAsync(recommendation, bid, ask, ct);

        if (!isValid || recommendation.Action.Equals("hold", StringComparison.OrdinalIgnoreCase))
        {
            if (!recommendation.Action.Equals("hold", StringComparison.OrdinalIgnoreCase) && !isValid)
            {
                db.Trades.Add(new Trade
                {
                    AccountId = AccountId,
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
                AccountId = AccountId,
                Source = "Claude",
                Message = $"{symbol}: {recommendation.Action.ToUpper()} (Confidence: {recommendation.Confidence:P0})",
                Details = recommendation.Reasoning
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        // Gleiche Richtung: Pyramiding pruefen oder blocken
        var sameDirectionPositions = positions
            .Where(p => p.Symbol == symbol && !IsOppositeDirection(p.Side, recommendation.Action))
            .ToList();

        if (sameDirectionPositions.Count > 0)
        {
            var maxPyramid = Settings.MaxPyramidLevels;
            var isPyramidAllowed = maxPyramid > 0
                && sameDirectionPositions.Count < maxPyramid
                && recommendation.Confidence >= Settings.PyramidMinConfidence;

            if (!isPyramidAllowed)
            {
                _logger.LogDebug(
                    "Ueberspringe {Action} {Symbol} – bereits {Count} Position(en) offen{PyramidInfo}.",
                    recommendation.Action.ToUpper(), symbol, sameDirectionPositions.Count,
                    maxPyramid > 0 ? $" (max Pyramid: {maxPyramid}, Conf: {recommendation.Confidence:P0})" : "");
                db.TradingLogs.Add(new TradingLog
                {
                    AccountId = AccountId,
                    Source = "TradingEngine",
                    Message = $"{symbol}: {recommendation.Action.ToUpper()} uebersprungen – bereits Position offen",
                    Details = recommendation.Reasoning
                });
                await db.SaveChangesAsync(ct);
                return;
            }

            _logger.LogInformation(
                "Pyramiding: {Action} {Symbol} – Level {Level}/{Max} (Confidence: {Conf:P0})",
                recommendation.Action.ToUpper(), symbol,
                sameDirectionPositions.Count + 1, maxPyramid, recommendation.Confidence);
        }

        // Risk-based Position Sizing: Lot-Größe aus SL-Distanz berechnen
        var quantity = recommendation.Quantity;
        if (Settings.RiskPerTradePercent > 0 && recommendation.StopLossPrice.HasValue && recommendation.StopLossPrice.Value > 0)
        {
            var calculatedLots = CalculatePositionSize(
                symbol, currentPrice, recommendation.StopLossPrice.Value, portfolioValue);

            if (calculatedLots.HasValue)
            {
                _logger.LogInformation(
                    "Risk-based Sizing für {Symbol}: LLM={LlmQty:F2} → Berechnet={CalcQty:F2} Lots " +
                    "(Risk={Risk}%, SL-Distanz={SlDist:F1} Pips)",
                    symbol, recommendation.Quantity, calculatedLots.Value,
                    Settings.RiskPerTradePercent,
                    PipCalculator.PriceToPips(symbol, Math.Abs(currentPrice - recommendation.StopLossPrice.Value)));
                quantity = calculatedLots.Value;
            }
        }

        // Neue Position eröffnen (Lots, StopLoss, TakeProfit)
        var trade = new Trade
        {
            AccountId = AccountId,
            Symbol = symbol,
            Action = action,
            Quantity = quantity,
            Price = currentPrice,
            SpreadAtEntry = spreadPips > 0 ? spreadPips : null,
            ClaudeReasoning = recommendation.Reasoning,
            ClaudeConfidence = recommendation.Confidence,
            Status = TradeStatus.Pending
        };

        var result = await _broker.PlaceOrderAsync(
            symbol, action, quantity,
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
            AccountId = AccountId,
            Level = result.Success ? "Info" : "Error",
            Source = "TradingEngine",
            Message = $"{(result.Success ? "✅" : "❌")} {action} {quantity:F2} Lots {symbol} @ {currentPrice:F4}",
            Details = recommendation.Reasoning
        });

        await db.SaveChangesAsync(ct);

        // Telegram-Notification
        _ = _notification.SendTradeNotificationAsync(trade);
    }

    /// <summary>
    /// Multi-Timeframe-Filter: Prueft ob die LLM-Empfehlung mit dem hoeheren Timeframe-Trend uebereinstimmt.
    /// Gibt true zurueck wenn der Trade blockiert werden soll.
    /// </summary>
    private async Task<bool> CheckMultiTimeframeFilter(
        string symbol, string action, MultiTimeframeSettings mtf, CancellationToken ct)
    {
        try
        {
            var candleCount = mtf.EmaPeriod + 10;
            var candles = await _broker.GetCandlesAsync(symbol, mtf.HigherTimeframe, candleCount, ct);

            if (candles.Count < mtf.EmaPeriod)
            {
                _logger.LogDebug(
                    "Multi-Timeframe: Nicht genuegend Candles fuer {Symbol} ({Count}/{Required}) – Filter uebersprungen",
                    symbol, candles.Count, mtf.EmaPeriod);
                return false; // Nicht blockieren wenn nicht genug Daten
            }

            var closes = candles.Select(c => c.Close).ToList();
            var ema = TechnicalAnalysisService.CalculateEMA(closes, mtf.EmaPeriod);

            if (!ema.HasValue)
                return false;

            var currentPrice = closes.Last();
            var isUptrend = currentPrice > ema.Value;
            var isBuy = action.Equals("buy", StringComparison.OrdinalIgnoreCase);

            _logger.LogDebug(
                "Multi-Timeframe {Symbol}: EMA{Period}={EMA:F5}, Price={Price:F5}, Trend={Trend}, Action={Action}",
                symbol, mtf.EmaPeriod, ema.Value, currentPrice,
                isUptrend ? "UP" : "DOWN", action.ToUpper());

            // Blockiere Counter-Trend-Trades
            if (isBuy && !isUptrend)
                return true; // Buy blockiert bei Abwaertstrend
            if (!isBuy && isUptrend)
                return true; // Sell blockiert bei Aufwaertstrend

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Multi-Timeframe-Filter Fehler fuer {Symbol} – Filter uebersprungen", symbol);
            return false;
        }
    }

    /// <summary>
    /// Berechnet die Lot-Größe basierend auf Risiko pro Trade.
    /// Formel: Lots = (Portfolio × RiskPercent) / (SL-Distanz-in-Pips × PipWert-pro-Lot)
    /// </summary>
    private decimal? CalculatePositionSize(string symbol, decimal currentPrice, decimal stopLossPrice, decimal portfolioValue)
    {
        var slDistancePips = PipCalculator.PriceToPips(symbol, Math.Abs(currentPrice - stopLossPrice));

        if (slDistancePips <= 0)
        {
            _logger.LogWarning("SL-Distanz ist 0 für {Symbol} – kann Position Size nicht berechnen", symbol);
            return null;
        }

        var pipValuePerLot = PipCalculator.GetPipValuePerLot(symbol, currentPrice);
        if (pipValuePerLot <= 0)
        {
            _logger.LogWarning("Pip-Wert ist 0 für {Symbol} – kann Position Size nicht berechnen", symbol);
            return null;
        }

        var riskAmount = portfolioValue * (decimal)(Settings.RiskPerTradePercent / 100.0);
        var lots = riskAmount / (slDistancePips * pipValuePerLot);

        // Auf 2 Dezimalstellen runden (Micro-Lots: 0.01)
        lots = Math.Round(lots, 2);

        // Minimum: 0.01 Lots (1 Micro-Lot)
        if (lots < 0.01m)
            lots = 0.01m;

        return lots;
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

    /// <summary>
    /// Laedt die letzten N geschlossenen Trades fuer ein Symbol aus der DB (Feedback-Loop fuer das LLM).
    /// </summary>
    private async Task<List<RecentTradeResult>> LoadRecentTradeResultsAsync(
        string symbol, int count, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            var closedTrades = await db.Trades
                .Where(t => t.Symbol == symbol
                    && t.Status == TradeStatus.Executed
                    && t.ClosedAt != null
                    && t.RealizedPnL != null)
                .OrderByDescending(t => t.ClosedAt)
                .Take(count)
                .ToListAsync(ct);

            return closedTrades.Select(t => new RecentTradeResult
            {
                Symbol = t.Symbol,
                Action = t.Action == TradeAction.Buy ? "buy" : "sell",
                EntryPrice = t.Price,
                ExitPrice = t.ExecutedPrice ?? t.Price,
                RealizedPnL = t.RealizedPnL ?? 0,
                Confidence = t.ClaudeConfidence,
                ClosedAt = t.ClosedAt ?? t.CreatedAt
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fehler beim Laden der Trade-Historie fuer {Symbol}", symbol);
            return new List<RecentTradeResult>();
        }
    }

    private async Task LogAsync(string level, string source, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.TradingLogs.Add(new TradingLog { AccountId = AccountId, Level = level, Source = source, Message = message });
        await db.SaveChangesAsync();
    }
}
