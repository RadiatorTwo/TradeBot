using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeTradingBot.Models;

public class ClaudeAnalysisRequest
{
    public string Symbol { get; set; } = string.Empty;
    public decimal CurrentPrice { get; set; }
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
    public decimal DayChange { get; set; }
    public decimal Volume { get; set; }
    public List<decimal> RecentPrices { get; set; } = new();
    public List<decimal> Candles1D { get; set; } = new();
    public List<decimal> Candles4H { get; set; } = new();
    public List<decimal> Candles1H { get; set; } = new();
    public Position? CurrentPosition { get; set; }
    public decimal AvailableCash { get; set; }
    public decimal PortfolioValue { get; set; }
    public TechnicalIndicators? Indicators { get; set; }
    public List<RecentTradeResult> RecentTradeResults { get; set; } = new();
    public List<string> NewsHeadlines { get; set; } = new();
    public List<EconomicEventSummary> UpcomingEvents { get; set; } = new();
    public string StrategyPrompt { get; set; } = string.Empty;
    public List<SymbolAllocation> PortfolioAllocations { get; set; } = new();
}

/// <summary>Zusammenfassung eines Wirtschaftskalender-Events fuer den LLM-Prompt.</summary>
public record EconomicEventSummary(string Title, DateTime EventTime, string Impact, string Currency);

/// <summary>Zusammenfassung eines geschlossenen Trades fuer den LLM-Feedback-Loop.</summary>
public record RecentTradeResult
{
    public string Symbol { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public decimal RealizedPnL { get; init; }
    public double Confidence { get; init; }
    public DateTime ClosedAt { get; init; }
}

/// <summary>Berechnete technische Indikatoren fuer ein Symbol.</summary>
public class TechnicalIndicators
{
    public decimal? RSI14 { get; set; }
    public decimal? EMA20 { get; set; }
    public decimal? EMA50 { get; set; }
    public decimal? EMA200 { get; set; }
    public decimal? MACDLine { get; set; }
    public decimal? MACDSignal { get; set; }
    public decimal? MACDHistogram { get; set; }
    public decimal? ATR14 { get; set; }
    public decimal? BollingerUpper { get; set; }
    public decimal? BollingerMiddle { get; set; }
    public decimal? BollingerLower { get; set; }
}

public class ClaudeTradeRecommendation
{
    public string Symbol { get; set; } = string.Empty;
    public string Action { get; set; } = "hold";
    public decimal? Quantity { get; set; }
    public double? Confidence { get; set; }
    public string Reasoning { get; set; } = string.Empty;
    public decimal? StopLossPrice { get; set; }
    public decimal? TakeProfitPrice { get; set; }
    public string? SetupType { get; set; }
    public decimal? GridCenterPrice { get; set; }
    public decimal? EntryPrice { get; set; }
}

/// <summary>Konto-Details vom Broker (Balance, Equity, Margin).</summary>
public class AccountDetails
{
    public decimal Balance { get; set; }
    public decimal Equity { get; set; }
    public decimal Margin { get; set; }
    public decimal FreeMargin { get; set; }
    public decimal UnrealizedPnL => Equity - Balance;
    public double MarginUsagePercent => Equity > 0 ? (double)(Margin / Equity) * 100 : 0;
}

/// <summary>OHLC-Candle fuer technische Indikatoren.</summary>
public class OhlcCandle
{
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Time { get; set; }
}

/// <summary>Ergebnis von IBrokerService.PlaceOrderAsync.</summary>
public class PlaceOrderResult
{
    public bool Success { get; set; }
    public string? BrokerOrderId { get; set; }
    public string? BrokerPositionId { get; set; }
}

/// <summary>Aktuelle Allokation eines Symbols im Portfolio.</summary>
public class SymbolAllocation
{
    public string Symbol { get; set; } = string.Empty;
    public decimal NotionalValue { get; set; }
    public double PercentOfPortfolio { get; set; }
    public double MaxAllowedPercent { get; set; }
    public bool IsOverweight { get; set; }
}

/// <summary>Pending Order vom Broker (Limit/Stop, nicht SL/TP einer Position).</summary>
public class BrokerPendingOrder
{
    public string OrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public decimal Price { get; set; }
    public string Type { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Geschlossene Position vom Broker (fuer Sync).</summary>
public class BrokerClosedPosition
{
    public string PositionId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal PnL { get; set; }
    public DateTime ClosedAt { get; set; }
}
