using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Verwaltet alle Account-Kontexte. Erstellt pro konfiguriertem Account einen Satz
/// von Services (Broker, RiskManager, TradingEngine). Implementiert IHostedService
/// um die Engines zu starten/stoppen.
/// </summary>
public class AccountManager : IHostedService
{
    private readonly List<AccountContext> _accounts = new();
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _config;
    private readonly ILogger<AccountManager> _logger;

    public IReadOnlyList<AccountContext> Accounts => _accounts;
    public AccountContext DefaultAccount => _accounts[0];

    public AccountContext GetAccount(string? accountId)
    {
        if (string.IsNullOrEmpty(accountId))
            return DefaultAccount;
        return _accounts.FirstOrDefault(a => a.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))
            ?? DefaultAccount;
    }

    public AccountManager(IServiceProvider sp, IConfiguration config, ILogger<AccountManager> logger)
    {
        _sp = sp;
        _config = config;
        _logger = logger;

        BuildAccounts();
    }

    private void BuildAccounts()
    {
        var accountConfigs = _config.GetSection("Accounts").Get<List<AccountConfig>>();

        if (accountConfigs == null || accountConfigs.Count == 0)
        {
            // Backwards-Kompatibilitaet: einzelner Account aus Top-Level-Konfiguration
            var singleConfig = new AccountConfig
            {
                Id = "default",
                DisplayName = "Standard",
                TradeLocker = _config.GetSection("TradeLocker").Get<TradeLockerSettings>() ?? new(),
                RiskManagement = _config.GetSection("RiskManagement").Get<RiskSettings>() ?? new(),
                PaperTrading = _config.GetSection("PaperTrading").Get<PaperTradingSettings>() ?? new()
            };
            accountConfigs = new List<AccountConfig> { singleConfig };
        }

        foreach (var cfg in accountConfigs)
        {
            var ctx = CreateAccountContext(cfg);
            _accounts.Add(ctx);
            _logger.LogInformation("Account registriert: {Id} ({Name})", ctx.AccountId, ctx.DisplayName);
        }
    }

    private AccountContext CreateAccountContext(AccountConfig cfg)
    {
        var httpFactory = _sp.GetRequiredService<IHttpClientFactory>();
        var loggerFactory = _sp.GetRequiredService<ILoggerFactory>();

        // Per-Account Broker
        var broker = new TradeLockerService(
            httpFactory, cfg.TradeLocker,
            loggerFactory.CreateLogger<TradeLockerService>());

        // Per-Account PaperTrading Decorator
        var paperSettings = new OptionsWrapper<PaperTradingSettings>(cfg.PaperTrading);
        var paperTrading = new PaperTradingBrokerDecorator(
            broker, new OptionsMonitorWrapper<PaperTradingSettings>(paperSettings),
            loggerFactory.CreateLogger<PaperTradingBrokerDecorator>());

        // Per-Account RiskManager
        var riskSettings = new OptionsWrapper<RiskSettings>(cfg.RiskManagement);
        var riskManager = new RiskManager(
            new OptionsMonitorWrapper<RiskSettings>(riskSettings),
            paperTrading,
            _sp.GetRequiredService<NotificationService>(),
            _sp.GetRequiredService<IServiceScopeFactory>(),
            loggerFactory.CreateLogger<RiskManager>())
        { AccountId = cfg.Id };

        // Per-Account TradingEngine
        var riskMonitor = new OptionsMonitorWrapper<RiskSettings>(riskSettings);
        var mtfSettings = _sp.GetRequiredService<IOptionsMonitor<MultiTimeframeSettings>>();
        var engine = new TradingEngine(
            _sp.GetRequiredService<IClaudeService>(),
            paperTrading,
            riskManager,
            _sp.GetRequiredService<TechnicalAnalysisService>(),
            _sp.GetRequiredService<TradingSessionService>(),
            _sp.GetRequiredService<MarketHoursService>(),
            _sp.GetRequiredService<EconomicCalendarService>(),
            _sp.GetRequiredService<NewsSentimentService>(),
            paperTrading,
            _sp.GetRequiredService<NotificationService>(),
            _sp.GetRequiredService<IServiceScopeFactory>(),
            riskMonitor,
            mtfSettings,
            _config,
            loggerFactory.CreateLogger<TradingEngine>())
        { AccountId = cfg.Id };

        return new AccountContext
        {
            AccountId = cfg.Id,
            DisplayName = string.IsNullOrEmpty(cfg.DisplayName) ? cfg.Id : cfg.DisplayName,
            Broker = broker,
            PaperTrading = paperTrading,
            Risk = riskManager,
            Engine = engine,
            RiskSettings = cfg.RiskManagement
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var ctx in _accounts)
        {
            _logger.LogInformation("Starte TradingEngine fuer Account {Id}...", ctx.AccountId);
            await ctx.Engine.StartAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var ctx in _accounts)
        {
            _logger.LogInformation("Stoppe TradingEngine fuer Account {Id}...", ctx.AccountId);
            await ctx.Engine.StopAsync(cancellationToken);
        }
    }
}

/// <summary>Einfacher IOptionsMonitor-Wrapper fuer statische Settings (kein Hot-Reload).</summary>
internal class OptionsMonitorWrapper<T> : IOptionsMonitor<T> where T : class
{
    private readonly T _value;
    public OptionsMonitorWrapper(IOptions<T> options) => _value = options.Value;
    public T CurrentValue => _value;
    public T Get(string? name) => _value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
