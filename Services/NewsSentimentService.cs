using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Laedt News-Headlines pro Waehrung/Symbol und stellt sie fuer den LLM-Prompt bereit.
/// Nutzt Finnhub (kostenloser Tier: 60 Calls/Min).
/// </summary>
public class NewsSentimentService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<NewsSettings> _settingsMonitor;
    private readonly IConfiguration _config;
    private readonly ISettingsRepository _settingsRepo;
    private readonly ILogger<NewsSentimentService> _logger;

    public const string HttpClientName = "Finnhub";

    private NewsSettings Settings => _settingsMonitor.CurrentValue;

    /// <summary>Symbol → Liste der aktuellsten Headlines.</summary>
    private readonly ConcurrentDictionary<string, List<string>> _headlinesCache = new(StringComparer.OrdinalIgnoreCase);

    public NewsSentimentService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<NewsSettings> settingsMonitor,
        IConfiguration config,
        ISettingsRepository settingsRepo,
        ILogger<NewsSentimentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settingsMonitor = settingsMonitor;
        _config = config;
        _settingsRepo = settingsRepo;
        _logger = logger;
    }

    /// <summary>Headlines fuer ein Symbol abrufen (aus Cache).</summary>
    public List<string> GetHeadlines(string symbol)
    {
        // Direktes Symbol-Match
        if (_headlinesCache.TryGetValue(symbol, out var headlines))
            return headlines;

        // Waehrungsbezogene Headlines: EURUSD → EUR + USD
        var combined = new List<string>();
        foreach (var currency in ExtractCurrencies(symbol))
        {
            if (_headlinesCache.TryGetValue(currency, out var currencyHeadlines))
                combined.AddRange(currencyHeadlines);
        }

        return combined.Count > 0
            ? combined.Take(Settings.MaxHeadlinesPerSymbol).ToList()
            : new List<string>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Kurz warten bis App gestartet ist
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (Settings.Enabled && !string.IsNullOrEmpty(Settings.FinnhubApiKey))
            {
                try
                {
                    await RefreshHeadlinesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "NewsSentiment: Fehler beim Aktualisieren der Headlines");
                }
            }

            var interval = Math.Max(Settings.RefreshIntervalMinutes, 15);
            await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken);
        }
    }

    private async Task RefreshHeadlinesAsync(CancellationToken ct)
    {
        var dbWatchList = await _settingsRepo.GetGlobalWatchListAsync();
        var watchList = dbWatchList.Count > 0
            ? dbWatchList.ToArray()
            : _config.GetSection("TradingStrategy:WatchList").Get<string[]>() ?? Array.Empty<string>();

        // Einzigartige Waehrungen/Symbole extrahieren
        var currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in watchList)
        {
            foreach (var c in ExtractCurrencies(symbol))
                currencies.Add(c);

            // Indizes direkt abfragen
            if (IsIndex(symbol))
                currencies.Add(symbol);
        }

        _logger.LogDebug("NewsSentiment: Lade Headlines fuer {Count} Kategorien: {Cats}",
            currencies.Count, string.Join(", ", currencies));

        foreach (var category in currencies)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var headlines = await FetchFinnhubNewsAsync(category, ct);
                if (headlines.Count > 0)
                {
                    _headlinesCache[category] = headlines.Take(Settings.MaxHeadlinesPerSymbol).ToList();
                    _logger.LogDebug("NewsSentiment: {Count} Headlines fuer {Cat}", headlines.Count, category);
                }

                // Rate Limiting: Finnhub Free = 60 Calls/Min
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NewsSentiment: Fehler beim Laden der Headlines fuer {Cat}", category);
            }
        }
    }

    private async Task<List<string>> FetchFinnhubNewsAsync(string category, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient(HttpClientName);

        // Finnhub General News: category = general, forex, crypto, merger
        // Fuer Forex nutzen wir "forex", fuer Indizes "general"
        var newsCategory = IsIndex(category) ? "general" : "forex";
        var url = $"api/v1/news?category={newsCategory}&token={Settings.FinnhubApiKey}";

        var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Finnhub API error {Status} fuer {Cat}", response.StatusCode, category);
            return new List<string>();
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var headlines = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return headlines;

            var categoryUpper = category.ToUpperInvariant();

            foreach (var item in root.EnumerateArray())
            {
                if (headlines.Count >= Settings.MaxHeadlinesPerSymbol * 2)
                    break;

                var headline = item.TryGetProperty("headline", out var h) ? h.GetString() : null;
                var summary = item.TryGetProperty("summary", out var s) ? s.GetString() : null;

                if (string.IsNullOrWhiteSpace(headline))
                    continue;

                // Relevanz-Filter: Headline oder Summary muss die Waehrung/den Index erwaehnen
                var text = $"{headline} {summary}".ToUpperInvariant();
                if (text.Contains(categoryUpper) || IsGeneralForexNews(text, categoryUpper))
                {
                    headlines.Add(headline!.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Finnhub: Fehler beim Parsen der News fuer {Cat}", category);
        }

        return headlines;
    }

    /// <summary>Waehrungen aus einem Symbol extrahieren (EURUSD → [EUR, USD]).</summary>
    private static List<string> ExtractCurrencies(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        var currencies = new List<string>();

        // Forex Pairs: EURUSD → EUR, USD
        if (s.Length == 6 && !s.Contains("100") && !s.Contains("500"))
        {
            currencies.Add(s[..3]);
            currencies.Add(s[3..]);
        }
        // Gold/Silber: XAUUSD → XAU, GOLD
        else if (s.StartsWith("XAU"))
        {
            currencies.Add("GOLD");
            currencies.Add("XAU");
        }
        else if (s.StartsWith("XAG"))
        {
            currencies.Add("SILVER");
            currencies.Add("XAG");
        }

        return currencies;
    }

    private static bool IsIndex(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        return s.Contains("100") || s.Contains("500") || s.Contains("30") ||
               s.StartsWith("US") || s.StartsWith("DE") || s.StartsWith("UK");
    }

    private static bool IsGeneralForexNews(string text, string currency)
    {
        // Breitere Forex-relevante Keywords
        var keywords = currency switch
        {
            "USD" => new[] { "DOLLAR", "FEDERAL RESERVE", "FED ", "FOMC", "US ECONOMY", "TREASURY" },
            "EUR" => new[] { "EURO", "ECB", "EUROPEAN CENTRAL", "EUROZONE" },
            "GBP" => new[] { "POUND", "STERLING", "BANK OF ENGLAND", "BOE " },
            "JPY" => new[] { "YEN", "BANK OF JAPAN", "BOJ " },
            "AUD" => new[] { "AUSSIE", "RBA ", "AUSTRALIAN" },
            "NZD" => new[] { "KIWI", "RBNZ", "NEW ZEALAND" },
            "CHF" => new[] { "FRANC", "SNB ", "SWISS" },
            "CAD" => new[] { "LOONIE", "BOC ", "CANADIAN" },
            "GOLD" or "XAU" => new[] { "GOLD", "PRECIOUS METAL", "SAFE HAVEN", "BULLION" },
            _ => Array.Empty<string>()
        };

        return keywords.Any(k => text.Contains(k));
    }
}
