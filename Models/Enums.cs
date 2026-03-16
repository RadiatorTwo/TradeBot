namespace ClaudeTradingBot.Models;

public enum TradeAction
{
    Buy,
    Sell,
    Hold
}

public enum TradeStatus
{
    Pending,
    Executed,
    Failed,
    Cancelled,
    Rejected,
    PendingOrder
}

public enum OrderType
{
    Market,
    Limit,
    Stop
}
