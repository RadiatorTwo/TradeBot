namespace ClaudeTradingBot.Models;

public record DashboardViewModel
{
    public string AccountId { get; set; } = "default";
    public string AccountDisplayName { get; set; } = string.Empty;
    public decimal PortfolioValue { get; set; }
    public decimal DailyPnL { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal AvailableCash { get; set; }
    public AccountDetails Account { get; set; } = new();
    public int OpenPositions { get; set; }
    public int TradesToday { get; set; }
    public bool IsEngineRunning { get; set; }
    public bool IsEnginePaused { get; set; }
    public bool IsKillSwitchActive { get; set; }
    public bool IsTradeLockerConnected { get; set; }
    public bool IsMarketOpen { get; set; }
    public bool IsPaperTrading { get; set; }
    public string MarketStatus { get; set; } = string.Empty;
    public List<Position> Positions { get; set; } = new();
    public List<Trade> RecentTrades { get; set; } = new();
    public List<TradingLog> RecentLogs { get; set; } = new();
    public List<DailyPnL> PnLHistory { get; set; } = new();
    public List<UpcomingEventViewModel> UpcomingEvents { get; set; } = new();
    public TradingStatsViewModel Stats { get; set; } = new();
}

/// <summary>Performance-Kennzahlen fuer das Dashboard.</summary>
public record TradingStatsViewModel
{
    public int TotalExecuted { get; init; }
    public int TotalRejected { get; init; }
    public int TotalFailed { get; init; }
    public double AvgConfidence { get; init; }
    public bool HasPnLData { get; init; }
    public int ClosedTrades { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public double WinRate { get; init; }
    public decimal AvgWin { get; init; }
    public decimal AvgLoss { get; init; }
    public decimal ProfitFactor { get; init; }
    public decimal TotalProfit { get; init; }
    public decimal TotalLoss { get; init; }
    public decimal NetPnL { get; init; }
    public decimal MaxDrawdown { get; init; }
    public double MaxDrawdownPercent { get; init; }
    public double SharpeRatio { get; init; }
    public double TradesPerDay { get; init; }
}

/// <summary>Performance-Kennzahlen pro Setup-Typ.</summary>
public record SetupTypeStatsViewModel
{
    public string SetupType { get; init; } = string.Empty;
    public int TradeCount { get; init; }
    public int WinCount { get; init; }
    public double WinRate { get; init; }
    public decimal TotalPnL { get; init; }
    public decimal AvgPnL { get; init; }
    public decimal ProfitFactor { get; init; }
}

public class UpcomingEventViewModel
{
    public string Title { get; set; } = string.Empty;
    public DateTime EventTime { get; set; }
    public string Impact { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
}
