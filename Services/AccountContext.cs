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
    public GridTradingService GridTrading { get; init; } = null!;
    public TradingEngine Engine { get; init; } = null!;
    public RiskSettings RiskSettings { get; init; } = new();

    // ── Strategie-Profil (Phase 7.3) ────────────────────────────────
    public string[] WatchList { get; set; } = Array.Empty<string>();
    public string StrategyPrompt { get; set; } = string.Empty;
    public string StrategyLabel { get; init; } = string.Empty;

    // ── Monitor-Referenzen fuer Hot-Reload ──────────────────────────
    public MutableOptionsMonitor<RiskSettings> RiskMonitor { get; init; } = null!;
    public MutableOptionsMonitor<PaperTradingSettings> PaperTradingMonitor { get; init; } = null!;
}
