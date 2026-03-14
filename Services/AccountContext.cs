using ClaudeTradingBot.Models;

namespace ClaudeTradingBot.Services;

/// <summary>Haelt die per-Account Service-Instanzen zusammen.</summary>
public class AccountContext
{
    public string AccountId { get; init; } = "default";
    public string DisplayName { get; init; } = string.Empty;
    public TradeLockerService Broker { get; init; } = null!;
    public PaperTradingBrokerDecorator PaperTrading { get; init; } = null!;
    public IBrokerService EffectiveBroker => PaperTrading;
    public RiskManager Risk { get; init; } = null!;
    public TradingEngine Engine { get; init; } = null!;
    public RiskSettings RiskSettings { get; init; } = new();
}
