namespace ClaudeTradingBot.Models;

/// <summary>Konfiguration fuer einen Backtest-Lauf.</summary>
public class BacktestConfig
{
    public string Symbol { get; set; } = "EURUSD";
    public string Timeframe { get; set; } = "1H";
    public DateTime StartDate { get; set; } = DateTime.UtcNow.AddDays(-30);
    public DateTime EndDate { get; set; } = DateTime.UtcNow;
    public decimal InitialBalance { get; set; } = 10000m;
    public double RiskPerTradePercent { get; set; } = 1.0;
    public double StopLossPips { get; set; } = 30;
    public double TakeProfitPips { get; set; } = 60;
    /// <summary>Strategie: "EMA_Cross", "RSI_Reversal" oder "LLM".</summary>
    public string Strategy { get; set; } = "EMA_Cross";

    /// <summary>LLM-Strategie: Max. Anzahl LLM-Aufrufe (Kostenkontrolle). 0 = unbegrenzt.</summary>
    public int MaxLlmCalls { get; set; } = 100;

    /// <summary>LLM-Strategie: Nur jede N-te Candle analysieren (Batch/Sampling). 1 = jede Candle.</summary>
    public int LlmSampleEveryN { get; set; } = 4;

    /// <summary>Simulierter Spread im Backtest (Pips).</summary>
    public double SpreadPips { get; set; } = 1.0;

    /// <summary>Slippage bei Market-Orders (Pips).</summary>
    public double SlippagePips { get; set; } = 0.5;
}

/// <summary>Ergebnis eines Backtest-Laufs.</summary>
public class BacktestResult
{
    public List<BacktestTrade> Trades { get; set; } = new();
    public List<BacktestEquityPoint> EquityCurve { get; set; } = new();
    public BacktestStats Stats { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>Ein simulierter Trade im Backtest.</summary>
public class BacktestTrade
{
    public DateTime EntryTime { get; set; }
    public DateTime ExitTime { get; set; }
    public string Side { get; set; } = "buy";
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal PnL { get; set; }
    public string Reason { get; set; } = string.Empty;
    /// <summary>LLM-Confidence bei Einstieg (nur LLM-Strategie).</summary>
    public double? Confidence { get; set; }
    /// <summary>LLM-Begruendung (nur LLM-Strategie).</summary>
    public string? LlmReasoning { get; set; }
}

/// <summary>Ein Punkt in der Equity-Kurve.</summary>
public class BacktestEquityPoint
{
    public DateTime Time { get; set; }
    public decimal Equity { get; set; }
}

/// <summary>Performance-Statistiken eines Backtests.</summary>
public class BacktestStats
{
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public double WinRate { get; set; }
    public decimal NetPnL { get; set; }
    public decimal MaxDrawdown { get; set; }
    public double MaxDrawdownPercent { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal AvgWin { get; set; }
    public decimal AvgLoss { get; set; }
    public double SharpeRatio { get; set; }
    public decimal FinalBalance { get; set; }
    public decimal PeakBalance { get; set; }
    /// <summary>Anzahl LLM-Aufrufe (nur LLM-Strategie).</summary>
    public int LlmCallCount { get; set; }
}
