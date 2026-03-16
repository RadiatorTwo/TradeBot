using System.Text.Json;
using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog.Context;

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
    private readonly GridTradingService _gridTrading;
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
        GridTradingService gridTrading,
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
        _gridTrading = gridTrading;
        _scopeFactory = scopeFactory;
        _settingsMonitor = settingsMonitor;
        _mtfSettings = mtfSettings;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // AccountId im LogContext fuer alle Logs dieser Engine-Instanz
        using (LogContext.PushProperty("AccountId", AccountId))
        {
            _logger.LogInformation("[{AccountId}] Trading Engine starting...", AccountId);

            // Datenbank initialisieren
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.EnsureCreatedAsync(stoppingToken);
            }

            // Broker-Verbindung herstellen
            await _broker.ConnectAsync(stoppingToken);

            // Grid-State Recovery nach Neustart
            try { await _gridTrading.RecoverGridsAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Grid Recovery fehlgeschlagen"); }

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

                    // Tages-PnL zuerst aufzeichnen (StartOfDayEquity fuer Daily-Loss-Check)
                    await _risk.RecordDailyPnLAsync(stoppingToken);

                    // Haupt-Trading-Zyklus
                    await RunTradingCycleAsync(stoppingToken);

                    // Stop-Losses prüfen
                    await _risk.CheckStopLossesAsync(stoppingToken);

                    // Tages-PnL aktualisieren
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
            _logger.LogInformation("[{AccountId}] Trading Engine stopped.", AccountId);
        }
    }

    private async Task RunTradingCycleAsync(CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..12];
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            _logger.LogInformation("Trading cycle started: {CorrelationId}", correlationId);

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

            // Portfolio-Rebalancing (Phase 10.3)
            if (Settings.Allocation.Enabled)
            {
                try
                {
                    var rebalanced = await _risk.CheckAndRebalanceAsync(ct);
                    if (rebalanced > 0)
                    {
                        _logger.LogInformation("Portfolio rebalanced: {Count} Positionen angepasst", rebalanced);
                        positions = await _broker.GetPositionsAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Portfolio-Rebalancing fehlgeschlagen");
                }
            }

            // Allokation einmal pro Zyklus berechnen (fuer LLM-Kontext)
            var allocations = new List<SymbolAllocation>();
            if (Settings.Allocation.Enabled)
            {
                try { allocations = await _risk.GetCurrentAllocationsAsync(ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "Allokation-Berechnung fehlgeschlagen"); }
            }

            foreach (var symbol in watchList)
            {
                if (ct.IsCancellationRequested) break;

                using (LogContext.PushProperty("Symbol", symbol))
                {
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

                        await AnalyzeAndTradeAsync(symbol, cash, portfolioValue, positions, allocations, ct);

                        // Kurze Pause zwischen Analysen (Rate Limiting)
                        await Task.Delay(TimeSpan.FromSeconds(Settings.AnalysisDelaySeconds), ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error analyzing {Symbol}", symbol);
                    }
                }
            }

            // Paper-Trading: Pending Limit/Stop Orders pruefen
            if (isPaper)
            {
                try { await _paperTrading.CheckAndFillPendingOrdersAsync(ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Paper-Trading Pending Order Check fehlgeschlagen"); }
            }

            // Aktive Grids verwalten (unabhaengig von LLM-Analyse)
            await _gridTrading.ManageAllActiveGridsAsync(Settings.Grid, ct);

            _logger.LogInformation("Trading cycle completed: {CorrelationId}", correlationId);
        }
    }

    private async Task AnalyzeAndTradeAsync(
        string symbol, decimal cash, decimal portfolioValue,
        List<Position> positions, List<SymbolAllocation> allocations, CancellationToken ct)
    {
        var currentPrice = await _broker.GetCurrentPriceAsync(symbol, ct);

        // Preis-Validierung: wenn Broker 0 liefert, Symbol ueberspringen
        if (currentPrice == 0)
        {
            _logger.LogWarning("Preis fuer {Symbol} ist 0 – ueberspringe (Markt evtl. geschlossen)", symbol);
            return;
        }

        var (bid, ask) = await _broker.GetBidAskAsync(symbol, ct);
        var recentPrices = await _broker.GetRecentPricesAsync(symbol, Settings.RecentPricesCount, ct);
        var candles1D = await _broker.GetPriceHistoryAsync(symbol, "1D", 30, ct);
        var candles4H = await _broker.GetPriceHistoryAsync(symbol, "4H", 30, ct);
        var candles1H = await _broker.GetPriceHistoryAsync(symbol, "1H", 30, ct);
        var currentPosition = positions.FirstOrDefault(p => p.Symbol == symbol);

        // Technische Indikatoren berechnen (1H-Candles fuer OHLC, mehr Daten fuer EMA200)
        TechnicalIndicators? indicators = null;
        try
        {
            var ohlcCandles = await _broker.GetCandlesAsync(symbol, "1H", Settings.IndicatorCandleCount, ct);
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
        var recentTradeResults = await LoadRecentTradeResultsAsync(symbol, Settings.FeedbackLoopTradeCount, ct);

        // Wirtschaftskalender: Events fuer Symbol-Waehrungen laden
        var upcomingEvents = _calendar.GetUpcomingEventsForSymbol(symbol, 10)
            .Select(e => new EconomicEventSummary(e.Title, e.EventTime, e.Impact.ToString(), e.Currency))
            .ToList();

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
            UpcomingEvents = upcomingEvents,
            StrategyPrompt = StrategyPrompt,
            PortfolioAllocations = allocations
        };

        _logger.LogDebug(
            "LLM-Input für {Symbol}: Price={Price:F4}, Bid={Bid:F4}, Ask={Ask:F4}, " +
            "RecentPrices={Recent}, Candles1D={C1D}, Candles4H={C4H}, Candles1H={C1H}",
            symbol, currentPrice, bid, ask,
            recentPrices.Count, candles1D.Count, candles4H.Count, candles1H.Count);

        var llmTimer = System.Diagnostics.Stopwatch.StartNew();
        ClaudeTradeRecommendation? recommendation = null;
        var maxAttempts = 1 + Settings.LlmRetryCount;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            recommendation = await _claude.AnalyzeAsync(request, ct);
            if (recommendation != null)
                break;
            if (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "No recommendation received for {Symbol}, retry {Attempt}/{MaxAttempts} in 2 seconds...",
                    symbol, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        llmTimer.Stop();
        TradingMetrics.LlmAnalysisDuration.WithLabels(_config["Llm:Provider"] ?? "unknown")
            .Observe(llmTimer.Elapsed.TotalSeconds);
        TradingMetrics.LlmCallsTotal.WithLabels(_config["Llm:Provider"] ?? "unknown", recommendation != null ? "success" : "null").Inc();

        if (recommendation == null)
        {
            _logger.LogWarning("No recommendation received for {Symbol} after {Attempts} attempts", symbol, maxAttempts);
            return;
        }

        // ── Grid-Trading (Phase 10.1) ─────────────────────────────────────
        if (recommendation.Action.Equals("grid", StringComparison.OrdinalIgnoreCase))
        {
            if (Settings.Grid.Enabled)
            {
                await _gridTrading.HandleGridRecommendationAsync(
                    symbol, currentPrice, recommendation, Settings.Grid, ct);
            }
            else
            {
                _logger.LogInformation("Grid-Trading empfohlen fuer {Symbol}, aber in Settings deaktiviert", symbol);
            }

            using var gridScope = _scopeFactory.CreateScope();
            var gridDb = gridScope.ServiceProvider.GetRequiredService<TradingDbContext>();
            gridDb.TradingLogs.Add(new TradingLog
            {
                AccountId = AccountId,
                Source = "Claude",
                Message = $"{symbol}: GRID empfohlen (Confidence: {recommendation.Confidence:P0})",
                Details = recommendation.Reasoning
            });
            await gridDb.SaveChangesAsync(ct);
            return;
        }

        // Wenn LLM buy/sell empfiehlt aber ein aktives Grid existiert: Grid deaktivieren
        if (!recommendation.Action.Equals("hold", StringComparison.OrdinalIgnoreCase)
            && Settings.Grid.Enabled
            && await _gridTrading.HasActiveGridAsync(symbol, ct))
        {
            if (recommendation.Confidence >= Settings.GridDeactivationMinConfidence)
            {
                _logger.LogInformation(
                    "LLM empfiehlt {Action} {Symbol} mit hoher Confidence ({Conf:P0}) – deaktiviere Grid",
                    recommendation.Action.ToUpper(), symbol, recommendation.Confidence);
                await _gridTrading.DeactivateGridAsync(symbol, closePositions: true, ct);
            }
            else
            {
                _logger.LogDebug(
                    "Grid aktiv fuer {Symbol}, LLM empfiehlt {Action} aber Confidence zu niedrig ({Conf:P0}) – Grid bleibt aktiv",
                    symbol, recommendation.Action.ToUpper(), recommendation.Confidence);
                return;
            }
        }

        // ── Multi-Timeframe-Filter (Phase 5.3) ──────────────────────────
        var normalizedAction = NormalizeAction(recommendation.Action);
        if (!normalizedAction.Equals("hold", StringComparison.OrdinalIgnoreCase))
        {
            var mtf = _mtfSettings.CurrentValue;
            if (mtf.Enabled)
            {
                var blocked = await CheckMultiTimeframeFilter(symbol, normalizedAction, mtf, ct);
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

        var (action, orderType) = ParseActionType(recommendation.Action);

        // Limit/Stop-Order: entryPrice validieren
        if (orderType != OrderType.Market)
        {
            if (!recommendation.EntryPrice.HasValue || recommendation.EntryPrice.Value <= 0)
            {
                _logger.LogWarning("Limit/Stop-Order fuer {Symbol} ohne gueltige entryPrice – verworfen", symbol);
                return;
            }

            var entryValid = (recommendation.Action.ToLower()) switch
            {
                "buy_limit" => recommendation.EntryPrice.Value < currentPrice,
                "sell_limit" => recommendation.EntryPrice.Value > currentPrice,
                "buy_stop" => recommendation.EntryPrice.Value > currentPrice,
                "sell_stop" => recommendation.EntryPrice.Value < currentPrice,
                _ => true
            };

            if (!entryValid)
            {
                _logger.LogWarning(
                    "Limit/Stop-Order {Action} {Symbol}: entryPrice {Entry:F5} ungueltig relativ zum aktuellen Preis {Price:F5}",
                    recommendation.Action, symbol, recommendation.EntryPrice.Value, currentPrice);
                return;
            }
        }

        // Gegenrichtung erkennen: LLM empfiehlt buy aber wir haben sell-Positionen (oder umgekehrt)
        // → bestehende Positionen schließen
        var oppositePositions = positions
            .Where(p => p.Symbol == symbol && IsOppositeDirection(p.Side, recommendation.Action))
            .ToList();

        if (oppositePositions.Count > 0 && !recommendation.Action.Equals("hold", StringComparison.OrdinalIgnoreCase))
        {
            var minConfForClose = Settings.OppositeDirectionMinConfidence > 0
                ? Settings.OppositeDirectionMinConfidence
                : Settings.MinConfidence;
            if ((recommendation.Confidence ?? 0) < minConfForClose)
            {
                _logger.LogInformation(
                    "Gegenrichtungs-Positionen nicht geschlossen: Confidence {Conf:P0} < {Min:P0}",
                    recommendation.Confidence ?? 0, minConfForClose);
                return;
            }

            foreach (var pos in oppositePositions)
            {
                var posId = pos.BrokerPositionId ?? pos.Symbol;
                _logger.LogInformation(
                    "Schließe {Side}-Position {Symbol} ({Qty} Lots, ID: {PosId}) – LLM empfiehlt {Action}",
                    pos.Side, pos.Symbol, pos.Quantity, posId, recommendation.Action.ToUpper());

                var closeSuccess = await _broker.ClosePositionAsync(posId, null, ct);

                // Exit-Preis: Buy-Position schliessen = Verkauf an Bid; Sell-Position schliessen = Kauf an Ask
                var closeExitPrice = (bid > 0 && ask > 0)
                    ? (pos.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? bid : ask)
                    : currentPrice;

                db.Trades.Add(new Trade
                {
                    AccountId = AccountId,
                    Symbol = symbol,
                    Action = pos.Side == "buy" ? TradeAction.Sell : TradeAction.Buy,
                    Quantity = pos.Quantity,
                    Price = currentPrice,
                    ExecutedPrice = closeSuccess ? closeExitPrice : null,
                    ExecutedAt = closeSuccess ? DateTime.UtcNow : null,
                    Status = closeSuccess ? TradeStatus.Executed : TradeStatus.Failed,
                    ClaudeReasoning = $"Position geschlossen: LLM empfiehlt {recommendation.Action.ToUpper()} (Confidence: {recommendation.Confidence:P0}). {recommendation.Reasoning}",
                    ClaudeConfidence = recommendation.Confidence ?? 0.0,
                    BrokerPositionId = pos.BrokerPositionId,
                    ErrorMessage = closeSuccess ? null : "Close-Order fehlgeschlagen",
                    SetupType = recommendation.SetupType
                });

                db.TradingLogs.Add(new TradingLog
                {
                    AccountId = AccountId,
                    Level = closeSuccess ? "Info" : "Error",
                    Source = "TradingEngine",
                    Message = $"{(closeSuccess ? "✅" : "❌")} Close {pos.Side.ToUpper()} {pos.Quantity:F2} Lots {symbol} @ {closeExitPrice:F4} (LLM: {recommendation.Action.ToUpper()})"
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

        // Ausfuehrungspreis: Buy = Ask, Sell = Bid (Fallback: currentPrice wenn bid/ask = 0)
        var executionPrice = (bid > 0 && ask > 0)
            ? (action == TradeAction.Buy ? ask : bid)
            : currentPrice;

        // RequireSlTpFromLlm: Trade ablehnen wenn LLM keinen SL/TP liefert
        if (Settings.RequireSlTpFromLlm
            && !recommendation.Action.Equals("hold", StringComparison.OrdinalIgnoreCase)
            && (!recommendation.StopLossPrice.HasValue || recommendation.StopLossPrice.Value <= 0
                || !recommendation.TakeProfitPrice.HasValue || recommendation.TakeProfitPrice.Value <= 0))
        {
            _logger.LogInformation(
                "Trade rejected – RequireSlTpFromLlm aktiv, LLM hat keinen gueltigen SL/TP geliefert fuer {Symbol}",
                symbol);
            using var rejectScope = _scopeFactory.CreateScope();
            var rejectDb = rejectScope.ServiceProvider.GetRequiredService<TradingDbContext>();
            rejectDb.Trades.Add(new Trade
            {
                AccountId = AccountId,
                Symbol = symbol,
                Action = action,
                Quantity = recommendation.Quantity ?? 0.01m,
                Price = currentPrice,
                ClaudeReasoning = recommendation.Reasoning,
                ClaudeConfidence = recommendation.Confidence ?? 0.0,
                Status = TradeStatus.Rejected,
                ErrorMessage = "Trade rejected: SL/TP vom LLM erforderlich",
                SetupType = recommendation.SetupType
            });
            rejectDb.TradingLogs.Add(new TradingLog
            {
                AccountId = AccountId,
                Source = "TradingEngine",
                Message = $"{symbol}: Trade abgelehnt – LLM hat keinen SL/TP geliefert"
            });
            await rejectDb.SaveChangesAsync(ct);
            return;
        }

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
                    Quantity = recommendation.Quantity ?? 0.01m,
                    Price = currentPrice,
                    ClaudeReasoning = recommendation.Reasoning,
                    ClaudeConfidence = recommendation.Confidence ?? 0.0,
                    Status = TradeStatus.Rejected,
                    ErrorMessage = "Trade rejected by RiskManager",
                    SetupType = recommendation.SetupType
                });
                TradingMetrics.TradesTotal.WithLabels("rejected", AccountId).Inc();
                TradingMetrics.RejectedTrades.WithLabels("risk_manager").Inc();
            }
            TradingMetrics.TradesByAction.WithLabels(recommendation.Action.ToLower(), symbol, AccountId).Inc();
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

        // Risk-based Position Sizing: Lot-Größe aus SL-Distanz berechnen (Einstieg = executionPrice)
        var quantity = recommendation.Quantity ?? 0.01m;
        if (Settings.RiskPerTradePercent > 0 && recommendation.StopLossPrice.HasValue && recommendation.StopLossPrice.Value > 0)
        {
            var calculatedLots = CalculatePositionSize(
                symbol, executionPrice, recommendation.StopLossPrice.Value, portfolioValue);

            if (calculatedLots.HasValue)
            {
                _logger.LogInformation(
                    "Risk-based Sizing für {Symbol}: LLM={LlmQty:F2} → Berechnet={CalcQty:F2} Lots " +
                    "(Risk={Risk}%, SL-Distanz={SlDist:F1} Pips)",
                    symbol, recommendation.Quantity, calculatedLots.Value,
                    Settings.RiskPerTradePercent,
                    PipCalculator.PriceToPips(symbol, Math.Abs(executionPrice - recommendation.StopLossPrice.Value)));
                quantity = calculatedLots.Value;
            }
        }

        // SL/TP-Schutz: wenn LLM keinen SL/TP liefert, Default setzen
        var stopLoss = recommendation.StopLossPrice;
        var takeProfit = recommendation.TakeProfitPrice;

        if (!stopLoss.HasValue || stopLoss.Value <= 0)
        {
            var defaultSlPips = (decimal)Settings.DefaultStopLossPips;
            var slDistance = PipCalculator.PipsToPrice(symbol, defaultSlPips);
            var isBuyAction = action == TradeAction.Buy;
            stopLoss = isBuyAction ? executionPrice - slDistance : executionPrice + slDistance;

            _logger.LogWarning(
                "LLM hat keinen StopLoss geliefert fuer {Symbol}. Default-SL gesetzt: {SL:F5} ({Pips} Pips)",
                symbol, stopLoss.Value, defaultSlPips);
        }

        if (!takeProfit.HasValue || takeProfit.Value <= 0)
        {
            var slDist = Math.Abs(executionPrice - stopLoss.Value);
            var tpDist = slDist * (decimal)Settings.DefaultTakeProfitRatio;
            var isBuyAction = action == TradeAction.Buy;
            takeProfit = isBuyAction ? executionPrice + tpDist : executionPrice - tpDist;

            _logger.LogWarning(
                "LLM hat keinen TakeProfit geliefert fuer {Symbol}. Default-TP gesetzt: {TP:F5} ({Ratio}x SL-Distanz)",
                symbol, takeProfit.Value, Settings.DefaultTakeProfitRatio);
        }

        // MinRiskRewardRatio: Trade ablehnen wenn TP/SL < Ratio
        if (Settings.MinRiskRewardRatio > 0 && stopLoss.HasValue && takeProfit.HasValue)
        {
            var slDist = Math.Abs(executionPrice - stopLoss.Value);
            var tpDist = Math.Abs(takeProfit.Value - executionPrice);
            if (slDist > 0)
            {
                var rrRatio = (double)(tpDist / slDist);
                if (rrRatio < Settings.MinRiskRewardRatio)
                {
                    _logger.LogInformation(
                        "Trade rejected – Risk/Reward {RR:F2} < MinRiskRewardRatio {Min:F2} fuer {Symbol}",
                        rrRatio, Settings.MinRiskRewardRatio, symbol);
                    db.Trades.Add(new Trade
                    {
                        AccountId = AccountId,
                        Symbol = symbol,
                        Action = action,
                        Quantity = quantity,
                        Price = currentPrice,
                        ClaudeReasoning = recommendation.Reasoning,
                        ClaudeConfidence = recommendation.Confidence ?? 0.0,
                        Status = TradeStatus.Rejected,
                        ErrorMessage = $"Risk/Reward {rrRatio:F2} < {Settings.MinRiskRewardRatio}",
                        SetupType = recommendation.SetupType
                    });
                    db.TradingLogs.Add(new TradingLog
                    {
                        AccountId = AccountId,
                        Source = "TradingEngine",
                        Message = $"{symbol}: Trade abgelehnt – R/R {rrRatio:F2} < {Settings.MinRiskRewardRatio}"
                    });
                    await db.SaveChangesAsync(ct);
                    return;
                }
            }
        }

        // Neue Position eröffnen (Lots, StopLoss, TakeProfit)
        var trade = new Trade
        {
            AccountId = AccountId,
            Symbol = symbol,
            Action = action,
            OrderType = orderType,
            EntryPrice = orderType != OrderType.Market ? recommendation.EntryPrice : null,
            Quantity = quantity,
            Price = currentPrice,
            SpreadAtEntry = spreadPips > 0 ? spreadPips : null,
            ClaudeReasoning = recommendation.Reasoning,
            ClaudeConfidence = recommendation.Confidence ?? 0.0,
            Status = TradeStatus.Pending,
            SetupType = recommendation.SetupType
        };

        var execTimer = System.Diagnostics.Stopwatch.StartNew();
        var result = await _broker.PlaceOrderAsync(
            symbol, action, quantity,
            stopLoss, takeProfit,
            orderType, recommendation.EntryPrice, ct);
        execTimer.Stop();
        TradingMetrics.TradeExecutionDuration.Observe(execTimer.Elapsed.TotalSeconds);

        if (orderType == OrderType.Market)
        {
            trade.Status = result.Success ? TradeStatus.Executed : TradeStatus.Failed;
            trade.ExecutedPrice = result.Success ? executionPrice : null;
            trade.ExecutedAt = result.Success ? DateTime.UtcNow : null;
        }
        else
        {
            // Limit/Stop-Order: als PendingOrder markieren
            trade.Status = result.Success ? TradeStatus.PendingOrder : TradeStatus.Failed;
        }

        trade.BrokerOrderId = result.BrokerOrderId;
        trade.BrokerPositionId = result.BrokerPositionId;
        if (!result.Success)
            trade.ErrorMessage = "Order execution failed at broker";

        TradingMetrics.TradesTotal.WithLabels(trade.Status.ToString().ToLower(), AccountId).Inc();
        TradingMetrics.TradesByAction.WithLabels(recommendation.Action.ToLower(), symbol, AccountId).Inc();

        db.Trades.Add(trade);

        var orderLabel = orderType == OrderType.Market
            ? $"{action}"
            : $"{recommendation.Action.ToUpper()} @ {recommendation.EntryPrice:F5}";

        db.TradingLogs.Add(new TradingLog
        {
            AccountId = AccountId,
            Level = result.Success ? "Info" : "Error",
            Source = "TradingEngine",
            Message = $"{(result.Success ? "✅" : "❌")} {orderLabel} {quantity:F2} Lots {symbol} @ {executionPrice:F4}",
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

    /// <summary>Normalisiert eine Action (entfernt _limit/_stop Suffix) fuer Richtungsvergleiche.</summary>
    private static string NormalizeAction(string action)
        => action.Replace("_limit", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("_stop", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parst den LLM-Action-String in TradeAction + OrderType.</summary>
    private static (TradeAction Action, OrderType OrderType) ParseActionType(string action)
        => action.ToLower() switch
        {
            "buy" => (TradeAction.Buy, OrderType.Market),
            "sell" => (TradeAction.Sell, OrderType.Market),
            "buy_limit" => (TradeAction.Buy, OrderType.Limit),
            "sell_limit" => (TradeAction.Sell, OrderType.Limit),
            "buy_stop" => (TradeAction.Buy, OrderType.Stop),
            "sell_stop" => (TradeAction.Sell, OrderType.Stop),
            _ => (TradeAction.Buy, OrderType.Market)
        };

    /// <summary>Prüft ob Position-Side und LLM-Empfehlung in entgegengesetzte Richtungen zeigen.</summary>
    private static bool IsOppositeDirection(string positionSide, string recommendedAction)
    {
        var normalized = NormalizeAction(recommendedAction);
        var isBuyPosition = positionSide.Equals("buy", StringComparison.OrdinalIgnoreCase);
        var isSellRecommendation = normalized.Equals("sell", StringComparison.OrdinalIgnoreCase);
        var isSellPosition = positionSide.Equals("sell", StringComparison.OrdinalIgnoreCase);
        var isBuyRecommendation = normalized.Equals("buy", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Persistiert den aktuellen Engine-Zustand in die DB (Graceful Shutdown).
    /// Wird von AccountManager.StopAsync aufgerufen BEVOR CancellationTokens gefeuert werden.
    /// </summary>
    public async Task PersistStateAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            var positions = new List<Position>();
            try { positions = await _broker.GetPositionsAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Konnte Positionen beim Shutdown nicht laden"); }

            var snapshot = new EngineStateSnapshot
            {
                AccountId = AccountId,
                WasRunning = _isRunning,
                WasPaused = _pauseRequested,
                WasKillSwitchActive = _risk.IsKillSwitchActive,
                OpenPositionCount = positions.Count,
                OpenPositionsJson = JsonSerializer.Serialize(positions.Select(p => new
                {
                    p.Symbol, p.Side, p.Quantity, p.BrokerPositionId, p.AveragePrice
                })),
                ShutdownAt = DateTime.UtcNow,
                CleanShutdown = true
            };

            db.EngineStateSnapshots.Add(snapshot);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "[{AccountId}] State persisted: {Count} positions, Running={Running}, Paused={Paused}",
                AccountId, positions.Count, _isRunning, _pauseRequested);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{AccountId}] State-Persistierung beim Shutdown fehlgeschlagen", AccountId);
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
