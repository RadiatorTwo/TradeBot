using System.Collections.Concurrent;
using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Verwaltet alle Account-Kontexte. Erstellt pro konfiguriertem Account einen Satz
/// von Services (Broker, RiskManager, TradingEngine). Implementiert IHostedService
/// um die Engines zu starten/stoppen. Liest Settings aus der DB via ISettingsRepository.
/// Kann ohne Accounts starten – Accounts werden ueber die UI angelegt.
/// </summary>
public class AccountManager : IHostedService
{
    private readonly List<AccountContext> _accounts = new();
    private readonly object _accountsLock = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _accountCts = new();
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _config;
    private readonly ISettingsRepository _settingsRepo;
    private readonly ILogger<AccountManager> _logger;

    public IReadOnlyList<AccountContext> Accounts { get { lock (_accountsLock) { return _accounts.ToList(); } } }

    /// <summary>Erster Account oder null wenn keine Accounts konfiguriert sind.</summary>
    public AccountContext? DefaultAccount { get { lock (_accountsLock) { return _accounts.Count > 0 ? _accounts[0] : null; } } }

    public bool HasAccounts { get { lock (_accountsLock) { return _accounts.Count > 0; } } }

    public AccountContext? GetAccount(string? accountId)
    {
        lock (_accountsLock)
        {
            if (_accounts.Count == 0) return null;
            if (string.IsNullOrEmpty(accountId))
                return _accounts[0];
            return _accounts.FirstOrDefault(a => a.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                ?? _accounts[0];
        }
    }

    public AccountManager(IServiceProvider sp, IConfiguration config, ISettingsRepository settingsRepo, ILogger<AccountManager> logger)
    {
        _sp = sp;
        _config = config;
        _settingsRepo = settingsRepo;
        _logger = logger;
    }

    private async Task BuildAccountsAsync()
    {
        var accountConfigs = await _settingsRepo.GetAllAccountsAsync();
        var globalWatchList = await _settingsRepo.GetGlobalWatchListAsync();

        if (accountConfigs.Count == 0)
        {
            _logger.LogInformation("Keine Accounts in der Datenbank. Bitte unter /accounts einen Account anlegen.");
            return;
        }

        foreach (var cfg in accountConfigs)
        {
            var ctx = CreateAccountContext(cfg, globalWatchList);
            lock (_accountsLock) { _accounts.Add(ctx); }
            _logger.LogInformation("Account registriert: {Id} ({Name})", ctx.AccountId, ctx.DisplayName);
        }
    }

    private AccountContext CreateAccountContext(AccountConfig cfg, List<string> globalWatchList)
    {
        var httpFactory = _sp.GetRequiredService<IHttpClientFactory>();
        var loggerFactory = _sp.GetRequiredService<ILoggerFactory>();

        // Per-Account Broker
        var broker = new TradeLockerService(
            httpFactory, cfg.TradeLocker,
            loggerFactory.CreateLogger<TradeLockerService>());

        // Per-Account PaperTrading Decorator
        var paperMonitor = new MutableOptionsMonitor<PaperTradingSettings>(cfg.PaperTrading);
        var paperTrading = new PaperTradingBrokerDecorator(
            broker, paperMonitor,
            loggerFactory.CreateLogger<PaperTradingBrokerDecorator>());

        // Per-Account RiskManager
        var riskMonitor = new MutableOptionsMonitor<RiskSettings>(cfg.RiskManagement);
        var riskManager = new RiskManager(
            riskMonitor,
            paperTrading,
            _sp.GetRequiredService<NotificationService>(),
            _sp.GetRequiredService<IServiceScopeFactory>(),
            loggerFactory.CreateLogger<RiskManager>())
        { AccountId = cfg.Id };

        // Watchlist: per-Account oder Fallback auf globale
        var watchList = cfg.WatchList.Count > 0
            ? cfg.WatchList.ToArray()
            : globalWatchList.ToArray();

        // Per-Account GridTradingService
        var gridTrading = new GridTradingService(
            paperTrading,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            loggerFactory.CreateLogger<GridTradingService>())
        { AccountId = cfg.Id };

        // Per-Account TradingEngine
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
            gridTrading,
            _sp.GetRequiredService<IServiceScopeFactory>(),
            riskMonitor,
            mtfSettings,
            _config,
            loggerFactory.CreateLogger<TradingEngine>())
        {
            AccountId = cfg.Id,
            AccountWatchList = watchList,
            StrategyPrompt = cfg.StrategyPrompt
        };

        return new AccountContext
        {
            AccountId = cfg.Id,
            DisplayName = string.IsNullOrEmpty(cfg.DisplayName) ? cfg.Id : cfg.DisplayName,
            Broker = broker,
            PaperTrading = paperTrading,
            Risk = riskManager,
            GridTrading = gridTrading,
            Engine = engine,
            RiskSettings = cfg.RiskManagement,
            WatchList = watchList,
            StrategyPrompt = cfg.StrategyPrompt,
            StrategyLabel = cfg.StrategyLabel,
            RiskMonitor = riskMonitor,
            PaperTradingMonitor = paperMonitor
        };
    }

    /// <summary>
    /// Fuegt zur Laufzeit einen neuen Account hinzu und startet seine TradingEngine.
    /// Kein Neustart des Systems noetig.
    /// </summary>
    public async Task AddAccountAsync(AccountConfig cfg)
    {
        // Pruefen ob Account bereits existiert
        lock (_accountsLock)
        {
            if (_accounts.Any(a => a.AccountId.Equals(cfg.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Account {Id} existiert bereits, ueberspringe Hinzufuegen", cfg.Id);
                return;
            }
        }

        var globalWatchList = await _settingsRepo.GetGlobalWatchListAsync();
        var ctx = CreateAccountContext(cfg, globalWatchList);
        lock (_accountsLock) { _accounts.Add(ctx); }

        _logger.LogInformation("Account {Id} ({Name}) zur Laufzeit hinzugefuegt, starte Engine...", ctx.AccountId, ctx.DisplayName);

        var cts = new CancellationTokenSource();
        _accountCts[ctx.AccountId] = cts;
        await ctx.Engine.StartAsync(cts.Token);
    }

    /// <summary>
    /// Entfernt einen Account zur Laufzeit und stoppt seine TradingEngine.
    /// Kein Neustart des Systems noetig.
    /// </summary>
    public async Task RemoveAccountAsync(string accountId)
    {
        AccountContext? ctx;
        lock (_accountsLock)
        {
            ctx = _accounts.FirstOrDefault(a => a.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase));
        }
        if (ctx == null)
        {
            _logger.LogWarning("Account {Id} nicht gefunden, ueberspringe Entfernen", accountId);
            return;
        }

        _logger.LogInformation("Stoppe und entferne Account {Id} zur Laufzeit...", accountId);

        if (_accountCts.TryRemove(accountId, out var cts))
        {
            cts.Cancel();
        }

        try
        {
            await ctx.Engine.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fehler beim Stoppen der Engine fuer Account {Id}", accountId);
        }

        lock (_accountsLock) { _accounts.Remove(ctx); }
        _logger.LogInformation("Account {Id} entfernt", accountId);
    }

    /// <summary>
    /// Hot-Reload: Liest Account-Settings aus der DB und aktualisiert die laufenden Services.
    /// Wirksam fuer: RiskSettings, Watchlist, StrategyPrompt, PaperTrading.
    /// </summary>
    public async Task ReloadAccountSettingsAsync(string accountId)
    {
        var cfg = await _settingsRepo.GetAccountAsync(accountId);
        if (cfg == null) return;

        AccountContext? ctx;
        lock (_accountsLock) { ctx = _accounts.FirstOrDefault(a => a.AccountId == accountId); }
        if (ctx == null) return;

        var globalWatchList = await _settingsRepo.GetGlobalWatchListAsync();

        // RiskSettings hot-reload
        ctx.RiskMonitor.Update(cfg.RiskManagement);

        // PaperTrading hot-reload
        ctx.PaperTradingMonitor.Update(cfg.PaperTrading);

        // Watchlist hot-reload (atomarer Array-Swap)
        var watchList = cfg.WatchList.Count > 0
            ? cfg.WatchList.ToArray()
            : globalWatchList.ToArray();
        ctx.WatchList = watchList;
        ctx.Engine.AccountWatchList = watchList;

        // StrategyPrompt hot-reload
        ctx.StrategyPrompt = cfg.StrategyPrompt;
        ctx.Engine.StrategyPrompt = cfg.StrategyPrompt;

        _logger.LogInformation("Account {Id} Settings aus Datenbank neu geladen", accountId);
    }

    /// <summary>Hot-Reload fuer alle Accounts.</summary>
    public async Task ReloadAllAccountSettingsAsync()
    {
        List<AccountContext> snapshot;
        lock (_accountsLock) { snapshot = _accounts.ToList(); }
        await Task.WhenAll(snapshot.Select(ctx => ReloadAccountSettingsAsync(ctx.AccountId)));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await BuildAccountsAsync();

        List<AccountContext> snapshot;
        lock (_accountsLock) { snapshot = _accounts.ToList(); }
        foreach (var ctx in snapshot)
        {
            _logger.LogInformation("Starte TradingEngine fuer Account {Id}...", ctx.AccountId);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _accountCts[ctx.AccountId] = cts;
            await ctx.Engine.StartAsync(cts.Token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<AccountContext> snapshot;
        lock (_accountsLock) { snapshot = _accounts.ToList(); }
        _logger.LogInformation("AccountManager: Graceful shutdown fuer {Count} Accounts...", snapshot.Count);

        // Phase 1: State persistieren BEVOR CancellationTokens gefeuert werden
        foreach (var ctx in snapshot)
        {
            try
            {
                await ctx.Engine.PersistStateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "State-Persistierung fehlgeschlagen fuer Account {Id}", ctx.AccountId);
            }
        }

        // Phase 2: Engines stoppen
        foreach (var ctx in snapshot)
        {
            _logger.LogInformation("Stoppe TradingEngine fuer Account {Id}...", ctx.AccountId);

            if (_accountCts.TryRemove(ctx.AccountId, out var cts))
            {
                cts.Cancel();
            }

            await ctx.Engine.StopAsync(cancellationToken);
        }

        _accountCts.Clear();
    }
}

/// <summary>Thread-sicherer IOptionsMonitor-Wrapper mit Update()-Methode fuer Hot-Reload.</summary>
public class MutableOptionsMonitor<T> : IOptionsMonitor<T> where T : class
{
    private volatile T _value;
    private readonly List<Action<T, string?>> _listeners = new();
    private readonly object _lock = new();

    public MutableOptionsMonitor(T initial) => _value = initial;

    public T CurrentValue => _value;
    public T Get(string? name) => _value;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        lock (_lock) { _listeners.Add(listener); }
        return new ChangeRegistration(() =>
        {
            lock (_lock) { _listeners.Remove(listener); }
        });
    }

    public void Update(T newValue)
    {
        _value = newValue;
        List<Action<T, string?>> snapshot;
        lock (_lock) { snapshot = _listeners.ToList(); }
        foreach (var listener in snapshot)
        {
            listener(newValue, null);
        }
    }

    private sealed class ChangeRegistration(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
