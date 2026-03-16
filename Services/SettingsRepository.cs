using System.Text.Json;
using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClaudeTradingBot.Services;

public interface ISettingsRepository
{
    Task<List<AccountConfig>> GetAllAccountsAsync();
    Task<AccountConfig?> GetAccountAsync(string accountId);
    Task SaveAccountAsync(AccountConfig config);
    Task DeleteAccountAsync(string accountId);
    Task<List<string>> GetGlobalWatchListAsync();
    Task SaveGlobalWatchListAsync(List<string> symbols);
    Task<MultiTimeframeSettings> GetMultiTimeframeSettingsAsync();
    Task SaveMultiTimeframeSettingsAsync(MultiTimeframeSettings settings);
    Task<bool> HasAnySettingsAsync();
    Task SeedFromConfigurationAsync(IConfiguration configuration);
}

public class SettingsRepository : ISettingsRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static class Keys
    {
        public const string DefaultWatchList = "DefaultWatchList";
        public const string MultiTimeframe = "MultiTimeframe";
    }

    private readonly IDbContextFactory<TradingDbContext> _dbFactory;
    private readonly ILogger<SettingsRepository> _logger;

    public SettingsRepository(IDbContextFactory<TradingDbContext> dbFactory, ILogger<SettingsRepository> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<AccountConfig>> GetAllAccountsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entities = await db.AccountSettings.OrderBy(a => a.AccountId).ToListAsync();
        return entities.Select(e => MapToConfig(e)).ToList();
    }

    public async Task<AccountConfig?> GetAccountAsync(string accountId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.AccountSettings.FindAsync(accountId);
        return entity != null ? MapToConfig(entity) : null;
    }

    public async Task SaveAccountAsync(AccountConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Id))
            throw new ArgumentException("AccountId darf nicht leer sein.", nameof(config));

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var entity = await db.AccountSettings.FindAsync(config.Id);

            if (entity == null)
            {
                entity = new AccountSettingsEntity { AccountId = config.Id };
                db.AccountSettings.Add(entity);
            }

            entity.DisplayName = config.DisplayName;
            entity.TradeLockerJson = JsonSerializer.Serialize(config.TradeLocker, JsonOpts);
            entity.RiskSettingsJson = JsonSerializer.Serialize(config.RiskManagement, JsonOpts);
            entity.PaperTradingJson = JsonSerializer.Serialize(config.PaperTrading, JsonOpts);
            entity.WatchListJson = JsonSerializer.Serialize(config.WatchList, JsonOpts);
            entity.StrategyPrompt = config.StrategyPrompt;
            entity.StrategyLabel = config.StrategyLabel;
            entity.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Speichern von Account '{Id}'", config.Id);
            throw;
        }
    }

    public async Task DeleteAccountAsync(string accountId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.AccountSettings.FindAsync(accountId);
        if (entity != null)
        {
            db.AccountSettings.Remove(entity);
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<string>> GetGlobalWatchListAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.GlobalSettings.FindAsync(Keys.DefaultWatchList);
        if (entity == null)
            return new List<string>();
        return SafeDeserialize<List<string>>(entity.ValueJson) ?? new List<string>();
    }

    public async Task SaveGlobalWatchListAsync(List<string> symbols)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.GlobalSettings.FindAsync(Keys.DefaultWatchList);

        if (entity == null)
        {
            entity = new GlobalSettingsEntity { Key = Keys.DefaultWatchList };
            db.GlobalSettings.Add(entity);
        }

        entity.ValueJson = JsonSerializer.Serialize(symbols, JsonOpts);
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<MultiTimeframeSettings> GetMultiTimeframeSettingsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.GlobalSettings.FindAsync(Keys.MultiTimeframe);
        if (entity == null)
            return new MultiTimeframeSettings();
        return SafeDeserialize<MultiTimeframeSettings>(entity.ValueJson) ?? new MultiTimeframeSettings();
    }

    public async Task SaveMultiTimeframeSettingsAsync(MultiTimeframeSettings settings)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.GlobalSettings.FindAsync(Keys.MultiTimeframe);

        if (entity == null)
        {
            entity = new GlobalSettingsEntity { Key = Keys.MultiTimeframe };
            db.GlobalSettings.Add(entity);
        }

        entity.ValueJson = JsonSerializer.Serialize(settings, JsonOpts);
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<bool> HasAnySettingsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AccountSettings.AnyAsync();
    }

    public async Task SeedFromConfigurationAsync(IConfiguration configuration)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        if (await db.AccountSettings.AnyAsync())
        {
            _logger.LogDebug("Settings already in database, skipping seed");
            return;
        }

        _logger.LogInformation("Seeding settings from appsettings.json to database...");

        var accountConfigs = configuration.GetSection("Accounts").Get<List<AccountConfig>>();

        if (accountConfigs == null || accountConfigs.Count == 0)
        {
            var singleConfig = new AccountConfig
            {
                Id = "default",
                DisplayName = "Standard",
                TradeLocker = configuration.GetSection("TradeLocker").Get<TradeLockerSettings>() ?? new(),
                RiskManagement = configuration.GetSection("RiskManagement").Get<RiskSettings>() ?? new(),
                PaperTrading = configuration.GetSection("PaperTrading").Get<PaperTradingSettings>() ?? new()
            };
            accountConfigs = new List<AccountConfig> { singleConfig };
        }

        foreach (var cfg in accountConfigs)
        {
            db.AccountSettings.Add(new AccountSettingsEntity
            {
                AccountId = cfg.Id,
                DisplayName = cfg.DisplayName,
                TradeLockerJson = JsonSerializer.Serialize(cfg.TradeLocker, JsonOpts),
                RiskSettingsJson = JsonSerializer.Serialize(cfg.RiskManagement, JsonOpts),
                PaperTradingJson = JsonSerializer.Serialize(cfg.PaperTrading, JsonOpts),
                WatchListJson = JsonSerializer.Serialize(cfg.WatchList, JsonOpts),
                StrategyPrompt = cfg.StrategyPrompt,
                StrategyLabel = cfg.StrategyLabel,
                UpdatedAt = DateTime.UtcNow
            });
            _logger.LogInformation("Account '{Id}' ({Name}) seeded", cfg.Id, cfg.DisplayName);
        }

        // Global WatchList
        var watchList = configuration.GetSection("TradingStrategy:WatchList").Get<List<string>>();
        if (watchList != null && watchList.Count > 0)
        {
            db.GlobalSettings.Add(new GlobalSettingsEntity
            {
                Key = Keys.DefaultWatchList,
                ValueJson = JsonSerializer.Serialize(watchList, JsonOpts),
                UpdatedAt = DateTime.UtcNow
            });
            _logger.LogInformation("Global WatchList seeded: {Symbols}", string.Join(", ", watchList));
        }

        // MultiTimeframe
        var mtf = configuration.GetSection("MultiTimeframe").Get<MultiTimeframeSettings>();
        if (mtf != null)
        {
            db.GlobalSettings.Add(new GlobalSettingsEntity
            {
                Key = Keys.MultiTimeframe,
                ValueJson = JsonSerializer.Serialize(mtf, JsonOpts),
                UpdatedAt = DateTime.UtcNow
            });
            _logger.LogInformation("MultiTimeframe settings seeded");
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        _logger.LogInformation("Settings migrated from appsettings.json to database");
    }

    private AccountConfig MapToConfig(AccountSettingsEntity entity)
    {
        try
        {
            return new AccountConfig
            {
                Id = entity.AccountId,
                DisplayName = entity.DisplayName,
                TradeLocker = SafeDeserialize<TradeLockerSettings>(entity.TradeLockerJson) ?? new(),
                RiskManagement = SafeDeserialize<RiskSettings>(entity.RiskSettingsJson) ?? new(),
                PaperTrading = SafeDeserialize<PaperTradingSettings>(entity.PaperTradingJson) ?? new(),
                WatchList = SafeDeserialize<List<string>>(entity.WatchListJson) ?? new(),
                StrategyPrompt = entity.StrategyPrompt,
                StrategyLabel = entity.StrategyLabel
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Deserialisieren von Account '{Id}', verwende Defaults", entity.AccountId);
            return new AccountConfig { Id = entity.AccountId, DisplayName = entity.DisplayName };
        }
    }

    private T? SafeDeserialize<T>(string? json) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json))
            return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts) ?? new T();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON-Deserialisierung fehlgeschlagen fuer Typ {Type}", typeof(T).Name);
            return new T();
        }
    }
}
