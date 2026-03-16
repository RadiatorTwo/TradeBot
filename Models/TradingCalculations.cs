namespace ClaudeTradingBot.Models;

/// <summary>Korrelationsmatrix: dynamische Werte (aus historischen Preisen) mit statischem Fallback.</summary>
public static class CorrelationMatrix
{
    /// <summary>Dynamisch berechnete Korrelationen. Werden taeglich vom CorrelationService aktualisiert.</summary>
    private static volatile Dictionary<(string, string), double> _dynamic = new();

    /// <summary>Statische Fallback-Korrelationen fuer gaengige Forex/CFD-Pairs.</summary>
    private static readonly Dictionary<(string, string), double> _static = new()
    {
        { ("EURUSD", "GBPUSD"), 0.85 },
        { ("EURUSD", "AUDUSD"), 0.70 },
        { ("EURUSD", "NZDUSD"), 0.65 },
        { ("GBPUSD", "AUDUSD"), 0.60 },
        { ("GBPUSD", "NZDUSD"), 0.55 },
        { ("AUDUSD", "NZDUSD"), 0.90 },
        { ("EURUSD", "USDCHF"), -0.90 },
        { ("EURUSD", "USDJPY"), -0.50 },
        { ("GBPUSD", "USDCHF"), -0.80 },
        { ("GBPUSD", "USDJPY"), -0.40 },
        { ("XAUUSD", "EURUSD"), 0.40 },
        { ("XAUUSD", "USDJPY"), -0.30 },
        { ("XAUUSD", "USDCHF"), -0.35 },
        { ("US100", "US500"), 0.95 },
        { ("US100", "US30"), 0.85 },
        { ("US500", "US30"), 0.90 },
    };

    /// <summary>Dynamische Korrelationen setzen (vom CorrelationService aufgerufen).</summary>
    public static void UpdateDynamic(Dictionary<(string, string), double> correlations)
        => _dynamic = correlations;

    /// <summary>Korrelation zwischen zwei Symbolen. Dynamisch bevorzugt, statisch als Fallback, 0 wenn unbekannt.</summary>
    public static double GetCorrelation(string symbol1, string symbol2)
    {
        var s1 = symbol1.ToUpperInvariant();
        var s2 = symbol2.ToUpperInvariant();

        if (s1 == s2) return 1.0;

        var dyn = _dynamic;
        if (dyn.TryGetValue((s1, s2), out var dynCorr))
            return dynCorr;
        if (dyn.TryGetValue((s2, s1), out var dynCorrReverse))
            return dynCorrReverse;

        if (_static.TryGetValue((s1, s2), out var corr))
            return corr;
        if (_static.TryGetValue((s2, s1), out var corrReverse))
            return corrReverse;

        return 0.0;
    }

    /// <summary>True wenn dynamische Korrelationen verfuegbar sind.</summary>
    public static bool HasDynamicData => _dynamic.Count > 0;
}

/// <summary>Pip-Berechnungen fuer Forex/CFD-Instrumente.</summary>
public static class PipCalculator
{
    /// <summary>Pip-Groesse (kleinste Preiseinheit) je Instrument.</summary>
    public static decimal GetPipSize(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        if (s.Contains("JPY"))
            return 0.01m;
        if (s.StartsWith("XAU"))
            return 0.1m;
        if (s.StartsWith("XAG"))
            return 0.01m;
        if (s.Contains("100") || s.Contains("500") || s.Contains("30") || s.Contains("50") ||
            s.StartsWith("US") || s.StartsWith("UK") || s.StartsWith("DE") || s.StartsWith("JP"))
            return 1.0m;
        if (s.StartsWith("XTI") || s.StartsWith("XBR") || s.Contains("OIL"))
            return 0.01m;
        return 0.0001m;
    }

    /// <summary>Preisdifferenz in Pips umrechnen.</summary>
    public static decimal PriceToPips(string symbol, decimal priceDiff)
        => Math.Abs(priceDiff) / GetPipSize(symbol);

    /// <summary>Pips in Preisdifferenz umrechnen.</summary>
    public static decimal PipsToPrice(string symbol, decimal pips)
        => pips * GetPipSize(symbol);

    /// <summary>Pip-Wert in USD pro Standard-Lot (1.0 Lot).</summary>
    public static decimal GetPipValuePerLot(string symbol, decimal currentPrice)
    {
        var s = symbol.ToUpperInvariant();
        var pipSize = GetPipSize(symbol);

        if (s.Length >= 6 && s[3..6] == "JPY")
            return 100_000m * pipSize / currentPrice;
        if (s.Length >= 6 && s[3..6] == "USD")
            return 100_000m * pipSize;
        if (s.Length >= 3 && s[..3] == "USD")
            return 100_000m * pipSize / currentPrice;
        if (s.StartsWith("XAU"))
            return 100m * pipSize;
        if (s.Contains("100") || s.StartsWith("US") || s.StartsWith("DE"))
            return 1m * pipSize;
        return 100_000m * pipSize;
    }
}
