using Microsoft.AspNetCore.SignalR;

namespace ClaudeTradingBot.Hubs;

/// <summary>SignalR Hub fuer Echtzeit-Dashboard-Updates.</summary>
public class TradingHub : Hub
{
    public const string DashboardUpdate = "ReceiveDashboardUpdate";
    public const string StatusChange = "ReceiveStatusChange";
}
