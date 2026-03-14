using ClaudeTradingBot.Models;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Backtesting-Engine: Laedt historische Candles, wendet Strategien an,
/// und berechnet Performance-Statistiken.
/// </summary>
public class BacktestEngine
{
    private readonly IBrokerService _broker;
    private readonly TechnicalAnalysisService _ta;
    private readonly ILogger<BacktestEngine> _logger;

    public BacktestEngine(
        IBrokerService broker,
        TechnicalAnalysisService ta,
        ILogger<BacktestEngine> logger)
    {
        _broker = broker;
        _ta = ta;
        _logger = logger;
    }

    /// <summary>Fuehrt einen Backtest mit der angegebenen Konfiguration durch.</summary>
    public async Task<BacktestResult> RunAsync(BacktestConfig config, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var result = new BacktestResult();

        try
        {
            // 1. Historische Candles laden
            _logger.LogInformation(
                "Backtest Start: {Symbol} {Timeframe} von {From:dd.MM.yyyy} bis {To:dd.MM.yyyy}, Strategie={Strategy}",
                config.Symbol, config.Timeframe, config.StartDate, config.EndDate, config.Strategy);

            if (!_broker.IsConnected)
            {
                result.ErrorMessage = "Broker nicht verbunden. Bitte sicherstellen, dass TradeLocker verbunden ist.";
                return result;
            }

            var candles = await _broker.GetHistoricalCandlesAsync(
                config.Symbol, config.Timeframe, config.StartDate, config.EndDate, ct);

            if (candles.Count < 50)
            {
                var isWeekend = DateTime.UtcNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                var weekendHint = isWeekend
                    ? " Der Forex-Markt ist am Wochenende geschlossen — TradeLocker liefert dann moeglicherweise keine historischen Daten. Bitte unter der Woche erneut versuchen."
                    : "";
                result.ErrorMessage = $"Nicht genuegend Candles geladen ({candles.Count}). Mindestens 50 benoetigt.{weekendHint}" +
                    $" Moegliche Ursachen: Symbol '{config.Symbol}' nicht verfuegbar, Zeitraum zu kurz, Markt geschlossen, oder API-Limit erreicht.";
                return result;
            }

            _logger.LogInformation("Backtest: {Count} Candles geladen", candles.Count);

            // 2. Simulation durchfuehren
            var balance = config.InitialBalance;
            var peakBalance = balance;
            var maxDrawdown = 0m;
            BacktestPosition? openPosition = null;
            var trades = new List<BacktestTrade>();
            var equityCurve = new List<BacktestEquityPoint>();

            var pipSize = PipCalculator.GetPipSize(config.Symbol);
            var slDistance = (decimal)config.StopLossPips * pipSize;
            var tpDistance = (decimal)config.TakeProfitPips * pipSize;

            // Vorab-Listen fuer Performance (statt O(n^2) Kopieren)
            var allCloses = candles.Select(c => c.Close).ToList();
            var allHighs = candles.Select(c => c.High).ToList();
            var allLows = candles.Select(c => c.Low).ToList();

            // Startpunkt: genuegend Candles fuer Indikatoren (EMA200 braucht 200)
            var startIndex = 200;
            if (candles.Count <= startIndex)
            {
                result.ErrorMessage = $"Nicht genuegend Candles fuer Indikatoren ({candles.Count}, mindestens {startIndex + 1} benoetigt).";
                return result;
            }

            for (int i = startIndex; i < candles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var candle = candles[i];
                var candleTime = DateTimeOffset.FromUnixTimeMilliseconds(candle.Time).UtcDateTime;

                // Fortschritt melden
                if (progress != null && i % 10 == 0)
                {
                    var pct = (int)((double)(i - startIndex) / (candles.Count - startIndex) * 100);
                    progress.Report(pct);
                }

                // 2a. Offene Position pruefen (SL/TP)
                if (openPosition != null)
                {
                    var closed = CheckStopLossTakeProfit(openPosition, candle, candleTime);
                    if (closed != null)
                    {
                        balance += closed.PnL;
                        trades.Add(closed);
                        openPosition = null;
                    }
                }

                // 2b. Signal generieren (nur wenn keine offene Position)
                if (openPosition == null)
                {
                    // Verwende GetRange statt Take().ToList() fuer O(1) statt O(n)
                    var closesUpToNow = allCloses.GetRange(0, i + 1);
                    var highsUpToNow = allHighs.GetRange(0, i + 1);
                    var lowsUpToNow = allLows.GetRange(0, i + 1);

                    var signal = GenerateSignal(config.Strategy, closesUpToNow, highsUpToNow, lowsUpToNow);

                    if (signal != null)
                    {
                        // Position Sizing
                        var riskAmount = balance * (decimal)(config.RiskPerTradePercent / 100.0);
                        var pipValue = PipCalculator.GetPipValuePerLot(config.Symbol, candle.Close);
                        var lots = pipValue > 0 && config.StopLossPips > 0
                            ? riskAmount / ((decimal)config.StopLossPips * pipValue)
                            : 0.01m;
                        lots = Math.Max(Math.Round(lots, 2), 0.01m);

                        var isBuy = signal == "buy";
                        openPosition = new BacktestPosition
                        {
                            Side = signal,
                            EntryPrice = candle.Close,
                            EntryTime = candleTime,
                            Quantity = lots,
                            StopLoss = isBuy
                                ? candle.Close - slDistance
                                : candle.Close + slDistance,
                            TakeProfit = isBuy
                                ? candle.Close + tpDistance
                                : candle.Close - tpDistance
                        };
                    }
                }

                // 2c. Equity berechnen
                var unrealized = 0m;
                if (openPosition != null)
                {
                    var dir = openPosition.Side == "buy" ? 1 : -1;
                    unrealized = (candle.Close - openPosition.EntryPrice) * openPosition.Quantity * dir;
                }
                var equity = balance + unrealized;

                if (equity > peakBalance) peakBalance = equity;
                var drawdown = peakBalance - equity;
                if (drawdown > maxDrawdown) maxDrawdown = drawdown;

                equityCurve.Add(new BacktestEquityPoint
                {
                    Time = candleTime,
                    Equity = equity
                });
            }

            // Offene Position am Ende schliessen
            if (openPosition != null && candles.Count > 0)
            {
                var lastCandle = candles.Last();
                var lastTime = DateTimeOffset.FromUnixTimeMilliseconds(lastCandle.Time).UtcDateTime;
                var dir = openPosition.Side == "buy" ? 1 : -1;
                var pnl = (lastCandle.Close - openPosition.EntryPrice) * openPosition.Quantity * dir;
                balance += pnl;
                trades.Add(new BacktestTrade
                {
                    Side = openPosition.Side,
                    EntryPrice = openPosition.EntryPrice,
                    ExitPrice = lastCandle.Close,
                    EntryTime = openPosition.EntryTime,
                    ExitTime = lastTime,
                    Quantity = openPosition.Quantity,
                    PnL = pnl,
                    Reason = "Backtest-Ende"
                });
            }

            // 3. Statistiken berechnen
            result.Trades = trades;
            result.EquityCurve = equityCurve;
            result.Stats = CalculateStats(trades, config.InitialBalance, balance, peakBalance, maxDrawdown);

            progress?.Report(100);

            _logger.LogInformation(
                "Backtest fertig: {Trades} Trades, WinRate={WR:P0}, NetPnL={PnL:F2}, MaxDD={DD:P1}",
                result.Stats.TotalTrades, result.Stats.WinRate,
                result.Stats.NetPnL, result.Stats.MaxDrawdownPercent);
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Backtest abgebrochen.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backtest Fehler");
            result.ErrorMessage = $"Fehler: {ex.Message}";
        }

        return result;
    }

    /// <summary>Generiert ein Trading-Signal basierend auf der gewaehlten Strategie.</summary>
    private string? GenerateSignal(string strategy, List<decimal> closes, List<decimal> highs, List<decimal> lows)
    {
        if (closes.Count < 51)
            return null;

        return strategy switch
        {
            "RSI_Reversal" => GenerateRsiReversalSignal(closes),
            _ => GenerateEmaCrossSignal(closes)
        };
    }

    /// <summary>EMA-Cross: Buy wenn EMA20 > EMA50 kreuzt und RSI < 70. Sell umgekehrt.</summary>
    private string? GenerateEmaCrossSignal(List<decimal> closes)
    {
        if (closes.Count < 51)
            return null;

        var ema20Now = TechnicalAnalysisService.CalculateEMA(closes, 20);
        var ema50Now = TechnicalAnalysisService.CalculateEMA(closes, 50);
        var ema20Prev = TechnicalAnalysisService.CalculateEMA(closes.SkipLast(1).ToList(), 20);
        var ema50Prev = TechnicalAnalysisService.CalculateEMA(closes.SkipLast(1).ToList(), 50);

        if (!ema20Now.HasValue || !ema50Now.HasValue || !ema20Prev.HasValue || !ema50Prev.HasValue)
            return null;

        // Berechne RSI fuer Filter
        var indicators = _ta.Calculate(closes, closes, closes); // highs/lows approximieren
        var rsi = indicators.RSI14;

        // Bullish Cross: EMA20 kreuzt von unten nach oben ueber EMA50
        if (ema20Prev.Value <= ema50Prev.Value && ema20Now.Value > ema50Now.Value)
        {
            if (!rsi.HasValue || rsi.Value < 70)
                return "buy";
        }

        // Bearish Cross: EMA20 kreuzt von oben nach unten unter EMA50
        if (ema20Prev.Value >= ema50Prev.Value && ema20Now.Value < ema50Now.Value)
        {
            if (!rsi.HasValue || rsi.Value > 30)
                return "sell";
        }

        return null;
    }

    /// <summary>RSI-Reversal: Buy wenn RSI unter 30 und zurueck kreuzt. Sell bei RSI > 70.</summary>
    private string? GenerateRsiReversalSignal(List<decimal> closes)
    {
        if (closes.Count < 16)
            return null;

        var indicators = _ta.Calculate(closes, closes, closes);
        var indicatorsPrev = _ta.Calculate(closes.SkipLast(1).ToList(), closes.SkipLast(1).ToList(), closes.SkipLast(1).ToList());

        if (!indicators.RSI14.HasValue || !indicatorsPrev.RSI14.HasValue)
            return null;

        // RSI war unter 30 und kreuzt zurueck
        if (indicatorsPrev.RSI14.Value < 30 && indicators.RSI14.Value >= 30)
            return "buy";

        // RSI war ueber 70 und kreuzt zurueck
        if (indicatorsPrev.RSI14.Value > 70 && indicators.RSI14.Value <= 70)
            return "sell";

        return null;
    }

    /// <summary>Prueft ob SL oder TP innerhalb der Candle getroffen wurde.</summary>
    private static BacktestTrade? CheckStopLossTakeProfit(BacktestPosition pos, OhlcCandle candle, DateTime candleTime)
    {
        var isBuy = pos.Side == "buy";

        // SL pruefen
        if (isBuy && candle.Low <= pos.StopLoss)
        {
            var pnl = (pos.StopLoss - pos.EntryPrice) * pos.Quantity;
            return new BacktestTrade
            {
                Side = pos.Side, EntryPrice = pos.EntryPrice, ExitPrice = pos.StopLoss,
                EntryTime = pos.EntryTime, ExitTime = candleTime,
                Quantity = pos.Quantity, PnL = pnl, Reason = "Stop-Loss"
            };
        }
        if (!isBuy && candle.High >= pos.StopLoss)
        {
            var pnl = (pos.EntryPrice - pos.StopLoss) * pos.Quantity;
            return new BacktestTrade
            {
                Side = pos.Side, EntryPrice = pos.EntryPrice, ExitPrice = pos.StopLoss,
                EntryTime = pos.EntryTime, ExitTime = candleTime,
                Quantity = pos.Quantity, PnL = pnl, Reason = "Stop-Loss"
            };
        }

        // TP pruefen
        if (isBuy && candle.High >= pos.TakeProfit)
        {
            var pnl = (pos.TakeProfit - pos.EntryPrice) * pos.Quantity;
            return new BacktestTrade
            {
                Side = pos.Side, EntryPrice = pos.EntryPrice, ExitPrice = pos.TakeProfit,
                EntryTime = pos.EntryTime, ExitTime = candleTime,
                Quantity = pos.Quantity, PnL = pnl, Reason = "Take-Profit"
            };
        }
        if (!isBuy && candle.Low <= pos.TakeProfit)
        {
            var pnl = (pos.EntryPrice - pos.TakeProfit) * pos.Quantity;
            return new BacktestTrade
            {
                Side = pos.Side, EntryPrice = pos.EntryPrice, ExitPrice = pos.TakeProfit,
                EntryTime = pos.EntryTime, ExitTime = candleTime,
                Quantity = pos.Quantity, PnL = pnl, Reason = "Take-Profit"
            };
        }

        return null;
    }

    /// <summary>Berechnet Performance-Statistiken aus den Backtest-Trades.</summary>
    private static BacktestStats CalculateStats(
        List<BacktestTrade> trades, decimal initialBalance, decimal finalBalance, decimal peakBalance, decimal maxDrawdown)
    {
        var stats = new BacktestStats
        {
            TotalTrades = trades.Count,
            FinalBalance = finalBalance,
            PeakBalance = peakBalance,
            MaxDrawdown = maxDrawdown,
            NetPnL = finalBalance - initialBalance
        };

        if (trades.Count == 0)
            return stats;

        var winners = trades.Where(t => t.PnL > 0).ToList();
        var losers = trades.Where(t => t.PnL < 0).ToList();

        stats.WinningTrades = winners.Count;
        stats.LosingTrades = losers.Count;
        stats.WinRate = (double)winners.Count / trades.Count;
        stats.AvgWin = winners.Count > 0 ? winners.Average(t => t.PnL) : 0;
        stats.AvgLoss = losers.Count > 0 ? losers.Average(t => t.PnL) : 0;
        stats.MaxDrawdownPercent = peakBalance > 0
            ? (double)(maxDrawdown / peakBalance) * 100
            : 0;

        var totalProfit = winners.Sum(t => t.PnL);
        var totalLoss = Math.Abs(losers.Sum(t => t.PnL));
        stats.ProfitFactor = totalLoss > 0 ? totalProfit / totalLoss : totalProfit > 0 ? 999m : 0m;

        // Sharpe Ratio (vereinfacht: Durchschnitts-Return / StdDev)
        if (trades.Count > 1)
        {
            var returns = trades.Select(t => (double)t.PnL).ToList();
            var avgReturn = returns.Average();
            var stdDev = Math.Sqrt(returns.Sum(r => (r - avgReturn) * (r - avgReturn)) / (returns.Count - 1));
            stats.SharpeRatio = stdDev > 0 ? Math.Round(avgReturn / stdDev * Math.Sqrt(252), 2) : 0;
        }

        return stats;
    }

    /// <summary>Interne Klasse fuer eine offene Backtest-Position.</summary>
    private class BacktestPosition
    {
        public string Side { get; set; } = "buy";
        public decimal EntryPrice { get; set; }
        public DateTime EntryTime { get; set; }
        public decimal Quantity { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
    }
}
