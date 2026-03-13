using ClaudeTradingBot.Models;

namespace ClaudeTradingBot.Services;

/// <summary>Berechnet technische Indikatoren aus Preisdaten fuer die LLM-Analyse.</summary>
public class TechnicalAnalysisService
{
    /// <summary>Berechnet alle Indikatoren aus den verfuegbaren Candle-Daten.</summary>
    public TechnicalIndicators Calculate(List<decimal> closes, List<decimal> highs, List<decimal> lows)
    {
        var result = new TechnicalIndicators();

        if (closes.Count < 2)
            return result;

        // RSI(14)
        if (closes.Count >= 15)
            result.RSI14 = CalculateRSI(closes, 14);

        // EMA(20, 50, 200)
        if (closes.Count >= 20)
            result.EMA20 = CalculateEMA(closes, 20);
        if (closes.Count >= 50)
            result.EMA50 = CalculateEMA(closes, 50);
        if (closes.Count >= 200)
            result.EMA200 = CalculateEMA(closes, 200);

        // MACD(12, 26, 9)
        if (closes.Count >= 35)
        {
            var macd = CalculateMACD(closes, 12, 26, 9);
            result.MACDLine = macd.Line;
            result.MACDSignal = macd.Signal;
            result.MACDHistogram = macd.Histogram;
        }

        // ATR(14)
        if (highs.Count >= 15 && lows.Count >= 15 && closes.Count >= 15)
            result.ATR14 = CalculateATR(highs, lows, closes, 14);

        // Bollinger Bands(20, 2)
        if (closes.Count >= 20)
        {
            var bb = CalculateBollingerBands(closes, 20, 2.0m);
            result.BollingerUpper = bb.Upper;
            result.BollingerMiddle = bb.Middle;
            result.BollingerLower = bb.Lower;
        }

        return result;
    }

    /// <summary>RSI (Relative Strength Index) nach Wilder.</summary>
    private static decimal? CalculateRSI(List<decimal> closes, int period)
    {
        if (closes.Count < period + 1)
            return null;

        var gains = new List<decimal>();
        var losses = new List<decimal>();

        for (int i = 1; i < closes.Count; i++)
        {
            var change = closes[i] - closes[i - 1];
            gains.Add(change > 0 ? change : 0);
            losses.Add(change < 0 ? Math.Abs(change) : 0);
        }

        // Erster Durchschnitt: SMA
        var avgGain = gains.Take(period).Average();
        var avgLoss = losses.Take(period).Average();

        // Wilder's Smoothing fuer restliche Werte
        for (int i = period; i < gains.Count; i++)
        {
            avgGain = (avgGain * (period - 1) + gains[i]) / period;
            avgLoss = (avgLoss * (period - 1) + losses[i]) / period;
        }

        if (avgLoss == 0)
            return 100m;

        var rs = avgGain / avgLoss;
        return Math.Round(100m - (100m / (1m + rs)), 2);
    }

    /// <summary>EMA (Exponential Moving Average).</summary>
    private static decimal? CalculateEMA(List<decimal> closes, int period)
    {
        if (closes.Count < period)
            return null;

        var multiplier = 2.0m / (period + 1);

        // Start mit SMA der ersten 'period' Werte
        var ema = closes.Take(period).Average();

        for (int i = period; i < closes.Count; i++)
        {
            ema = (closes[i] - ema) * multiplier + ema;
        }

        return Math.Round(ema, 6);
    }

    /// <summary>MACD (Moving Average Convergence Divergence).</summary>
    private static (decimal Line, decimal Signal, decimal Histogram) CalculateMACD(
        List<decimal> closes, int fastPeriod, int slowPeriod, int signalPeriod)
    {
        // Berechne EMA-Serien fuer MACD-Linie
        var fastEma = CalculateEMASeries(closes, fastPeriod);
        var slowEma = CalculateEMASeries(closes, slowPeriod);

        if (fastEma.Count == 0 || slowEma.Count == 0)
            return (0, 0, 0);

        // MACD-Linie = Fast EMA - Slow EMA (nur wo beide existieren)
        var offset = slowPeriod - fastPeriod;
        var macdLine = new List<decimal>();

        for (int i = 0; i < slowEma.Count && (i + offset) < fastEma.Count; i++)
        {
            macdLine.Add(fastEma[i + offset] - slowEma[i]);
        }

        if (macdLine.Count < signalPeriod)
            return (macdLine.Last(), 0, macdLine.Last());

        // Signal-Linie = EMA der MACD-Linie
        var signal = CalculateEMASeries(macdLine, signalPeriod);

        if (signal.Count == 0)
            return (macdLine.Last(), 0, macdLine.Last());

        var currentMacd = macdLine.Last();
        var currentSignal = signal.Last();
        var histogram = currentMacd - currentSignal;

        return (Math.Round(currentMacd, 6), Math.Round(currentSignal, 6), Math.Round(histogram, 6));
    }

    /// <summary>Berechnet eine EMA-Serie (alle Werte ab dem Startpunkt).</summary>
    private static List<decimal> CalculateEMASeries(List<decimal> data, int period)
    {
        if (data.Count < period)
            return new List<decimal>();

        var multiplier = 2.0m / (period + 1);
        var result = new List<decimal>();

        // Start mit SMA
        var ema = data.Take(period).Average();
        result.Add(ema);

        for (int i = period; i < data.Count; i++)
        {
            ema = (data[i] - ema) * multiplier + ema;
            result.Add(ema);
        }

        return result;
    }

    /// <summary>ATR (Average True Range) nach Wilder.</summary>
    private static decimal? CalculateATR(List<decimal> highs, List<decimal> lows, List<decimal> closes, int period)
    {
        if (highs.Count < period + 1)
            return null;

        var trueRanges = new List<decimal>();

        for (int i = 1; i < highs.Count; i++)
        {
            var highLow = highs[i] - lows[i];
            var highClose = Math.Abs(highs[i] - closes[i - 1]);
            var lowClose = Math.Abs(lows[i] - closes[i - 1]);
            trueRanges.Add(Math.Max(highLow, Math.Max(highClose, lowClose)));
        }

        // Erster ATR = SMA der True Ranges
        var atr = trueRanges.Take(period).Average();

        // Wilder's Smoothing
        for (int i = period; i < trueRanges.Count; i++)
        {
            atr = (atr * (period - 1) + trueRanges[i]) / period;
        }

        return Math.Round(atr, 6);
    }

    /// <summary>Bollinger Bands (SMA +/- Standardabweichung).</summary>
    private static (decimal Upper, decimal Middle, decimal Lower) CalculateBollingerBands(
        List<decimal> closes, int period, decimal stdDevMultiplier)
    {
        if (closes.Count < period)
            return (0, 0, 0);

        // Letzten 'period' Werte fuer SMA
        var recentCloses = closes.TakeLast(period).ToList();
        var sma = recentCloses.Average();

        // Standardabweichung
        var sumSquares = recentCloses.Sum(c => (c - sma) * (c - sma));
        var stdDev = (decimal)Math.Sqrt((double)(sumSquares / period));

        var upper = Math.Round(sma + stdDevMultiplier * stdDev, 6);
        var middle = Math.Round(sma, 6);
        var lower = Math.Round(sma - stdDevMultiplier * stdDev, 6);

        return (upper, middle, lower);
    }
}
