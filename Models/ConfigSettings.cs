namespace ClaudeTradingBot.Models;

/// <summary>Wahl des LLM-Providers: Anthropic (Claude), Gemini (kostenloser Cloud-Free-Tier), OpenAICompatible (z. B. Ollama).</summary>
public class LlmSettings
{
    public string Provider { get; set; } = "Gemini";
}

/// <summary>Claude von Anthropic. API-Key von https://console.anthropic.com/ (Format sk-ant-...).</summary>
public class AnthropicSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-sonnet-4-20250514";
    public int MaxTokens { get; set; } = 2048;
}

/// <summary>Google Gemini API. Key: https://aistudio.google.com/apikey</summary>
public class GeminiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-2.0-flash-lite";
    public int MaxTokens { get; set; } = 2048;
}

/// <summary>Fuer OpenAI-kompatible Endpoints (Ollama, LM Studio, OpenRouter).</summary>
public class OpenAICompatibleSettings
{
    public string BaseUrl { get; set; } = "http://localhost:11434/v1/";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "qwen2.5:7b";
    public int MaxTokens { get; set; } = 2048;
    public int TimeoutSeconds { get; set; } = 180;
}

/// <summary>Konfiguration fuer einen einzelnen Account (Multi-Account-Support).</summary>
public class AccountConfig
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public TradeLockerSettings TradeLocker { get; set; } = new();
    public RiskSettings RiskManagement { get; set; } = new();
    public PaperTradingSettings PaperTrading { get; set; } = new();
    public List<string> WatchList { get; set; } = new();
    public string StrategyPrompt { get; set; } = string.Empty;
    public string StrategyLabel { get; set; } = string.Empty;
}

public class TradeLockerSettings
{
    public string BaseUrl { get; set; } = "https://demo.tradelocker.com/backend-api";
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string? AccountId { get; set; }
}

public class RiskSettings
{
    public double MinConfidence { get; set; } = 0.65;
    public double MaxPositionSizePercent { get; set; } = 10.0;
    public double MaxDailyLossPercent { get; set; } = 3.0;
    public double StopLossPercent { get; set; } = 5.0;
    public int MaxOpenPositions { get; set; } = 10;
    public int TradingIntervalMinutes { get; set; } = 15;
    public bool KillSwitchEnabled { get; set; } = true;
    public decimal MaxDailyLossAbsolute { get; set; } = 500m;
    public List<string> AllowedSessions { get; set; } = new();
    public double TrailingStopPips { get; set; } = 0;
    public double BreakevenTriggerPips { get; set; } = 0;
    public double RiskPerTradePercent { get; set; } = 0;
    public double MaxDrawdownPercent { get; set; } = 0;
    public double MaxWeeklyLossPercent { get; set; } = 0;
    public double MaxMonthlyLossPercent { get; set; } = 0;
    public double MaxCorrelatedExposurePercent { get; set; } = 0;
    public double MaxSpreadPips { get; set; } = 0;
    public bool DynamicConfidenceEnabled { get; set; }
    public double ConfidenceAtrFactor { get; set; } = 0.05;
    public double ConfidenceDrawdownFactor { get; set; } = 0.02;
    public double ConfidenceLossStreakFactor { get; set; } = 0.05;
    public double MaxDynamicConfidence { get; set; } = 0.85;
    public PortfolioAllocationSettings Allocation { get; set; } = new();
    public int PendingOrderMaxAgeMinutes { get; set; } = 240;
    public GridSettings Grid { get; set; } = new();
    public double PartialClosePercent { get; set; } = 0;
    public double PartialCloseTriggerPips { get; set; } = 30;
    public int MaxPyramidLevels { get; set; } = 0;
    public double PyramidMinConfidence { get; set; } = 0.75;
    /// <summary>Min. Confidence um Gegenrichtungs-Positionen zu schließen. 0 = MinConfidence nutzen.</summary>
    public double OppositeDirectionMinConfidence { get; set; }
    /// <summary>Fallback-SL in Pips wenn LLM keinen liefert.</summary>
    public double DefaultStopLossPips { get; set; } = 50;
    /// <summary>TP = Ratio × SL-Distanz.</summary>
    public double DefaultTakeProfitRatio { get; set; } = 1.5;
    /// <summary>Pause in Sekunden zwischen Symbol-Analysen.</summary>
    public int AnalysisDelaySeconds { get; set; } = 2;
    /// <summary>Candles für Indikatoren (z. B. EMA200).</summary>
    public int IndicatorCandleCount { get; set; } = 210;
    /// <summary>Anzahl Recent Prices für die Analyse.</summary>
    public int RecentPricesCount { get; set; } = 20;
    /// <summary>Anzahl Trades im Feedback-Loop.</summary>
    public int FeedbackLoopTradeCount { get; set; } = 10;
    /// <summary>Min. Confidence um Grid bei buy/sell zu deaktivieren.</summary>
    public double GridDeactivationMinConfidence { get; set; } = 0.8;
    /// <summary>Min. Korrelation für Exposure-Check.</summary>
    public double CorrelationThreshold { get; set; } = 0.3;
    /// <summary>Win-Rate-Schwelle für dynamische Confidence.</summary>
    public double ConfidenceWinRateThreshold { get; set; } = 0.5;
    /// <summary>Min. Risk/Reward – Trade ablehnen wenn TP/SL &lt; Ratio.</summary>
    public double MinRiskRewardRatio { get; set; } = 1.0;
    /// <summary>Trade ablehnen wenn LLM keinen SL/TP liefert.</summary>
    public bool RequireSlTpFromLlm { get; set; }
    /// <summary>Retries bei null-Response (0 = kein Retry).</summary>
    public int LlmRetryCount { get; set; } = 1;
}

/// <summary>Portfolio-Allokation: max. Gewichtung pro Symbol oder Asset-Klasse.</summary>
public class PortfolioAllocationSettings
{
    public bool Enabled { get; set; }
    public double DefaultMaxPercent { get; set; } = 20.0;
    public Dictionary<string, double> SymbolLimits { get; set; } = new();
    public double RebalanceTriggerOverPercent { get; set; } = 2.0;
}

/// <summary>Konfiguration fuer den News-Sentiment-Service.</summary>
public class NewsSettings
{
    public string FinnhubApiKey { get; set; } = string.Empty;
    public int MaxHeadlinesPerSymbol { get; set; } = 5;
    public int RefreshIntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; }
}

/// <summary>Konfiguration fuer den Paper-Trading-Modus.</summary>
public class PaperTradingSettings
{
    public bool Enabled { get; set; }
    public decimal InitialBalance { get; set; } = 10000m;
}

/// <summary>Multi-Timeframe-Bestaetigung: EMA-Trend auf hoeherem Timeframe als Filter.</summary>
public class MultiTimeframeSettings
{
    public bool Enabled { get; set; }
    public string HigherTimeframe { get; set; } = "4H";
    public int EmaPeriod { get; set; } = 200;
}

