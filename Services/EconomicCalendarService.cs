using System.Text.Json;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Ruft Wirtschaftskalender-Daten ab und prueft ob High-Impact-Events bevorstehen.
/// Nutzt die kostenlose Nager.Date API als Feiertags-Quelle und eine konfigurierbare
/// Kalender-URL fuer Wirtschaftsevents.
/// </summary>
public class EconomicCalendarService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<EconomicCalendarService> _logger;

    private volatile List<EconomicEvent> _cachedEvents = new();
    private DateTime _lastFetch = DateTime.MinValue;
    private readonly TimeSpan _cacheInterval = TimeSpan.FromHours(4);
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    public EconomicCalendarService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<EconomicCalendarService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial laden
        await RefreshEventsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cacheInterval, stoppingToken);
                await RefreshEventsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EconomicCalendar: Fehler beim Aktualisieren");
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
        }
    }

    /// <summary>Prueft ob ein High-Impact-Event fuer die gegebene Waehrung in der Naehe ist.</summary>
    public bool IsHighImpactEventNear(string currency, int minutesBefore = 30, int minutesAfter = 15)
    {
        var now = DateTime.UtcNow;

        return _cachedEvents.Any(e =>
            e.Impact == EventImpact.High &&
            e.AffectsCurrency(currency) &&
            e.EventTime >= now.AddMinutes(-minutesAfter) &&
            e.EventTime <= now.AddMinutes(minutesBefore));
    }

    /// <summary>Prueft ob ein Symbol von einem bevorstehenden High-Impact-Event betroffen ist.</summary>
    public bool IsSymbolAffectedByEvent(string symbol, int minutesBefore = 30, int minutesAfter = 15)
    {
        var currencies = ExtractCurrencies(symbol);
        return currencies.Any(c => IsHighImpactEventNear(c, minutesBefore, minutesAfter));
    }

    /// <summary>Gibt die naechsten Events zurueck (fuer Dashboard).</summary>
    public List<EconomicEvent> GetUpcomingEvents(int count = 10)
    {
        var now = DateTime.UtcNow;
        return _cachedEvents
            .Where(e => e.EventTime >= now.AddHours(-1))
            .OrderBy(e => e.EventTime)
            .Take(count)
            .ToList();
    }

    /// <summary>Gibt die naechsten High-Impact-Events zurueck.</summary>
    public List<EconomicEvent> GetUpcomingHighImpactEvents(int count = 5)
    {
        var now = DateTime.UtcNow;
        return _cachedEvents
            .Where(e => e.Impact == EventImpact.High && e.EventTime >= now)
            .OrderBy(e => e.EventTime)
            .Take(count)
            .ToList();
    }

    /// <summary>Gibt die naechsten Events fuer ein Symbol zurueck (gefiltert nach Waehrungen des Symbols).</summary>
    public List<EconomicEvent> GetUpcomingEventsForSymbol(string symbol, int count = 10)
    {
        var currencies = ExtractCurrencies(symbol);
        if (currencies.Count == 0)
            return new List<EconomicEvent>();

        var now = DateTime.UtcNow;
        return _cachedEvents
            .Where(e => e.EventTime >= now.AddHours(-1) && currencies.Any(c => e.AffectsCurrency(c)))
            .OrderBy(e => e.EventTime)
            .Take(count)
            .ToList();
    }

    private async Task RefreshEventsAsync(CancellationToken ct)
    {
        if (!await _fetchLock.WaitAsync(0, ct))
            return; // Bereits am Laden

        try
        {
            var calendarUrl = _config["EconomicCalendar:ApiUrl"] ?? "";

            if (string.IsNullOrEmpty(calendarUrl))
            {
                // Kein externer Kalender konfiguriert: statische bekannte Events nutzen
                _cachedEvents = GetStaticHighImpactEvents();
                _lastFetch = DateTime.UtcNow;
                _logger.LogDebug("EconomicCalendar: Nutze statische Events ({Count} Events)", _cachedEvents.Count);
                return;
            }

            var events = await FetchFromApiAsync(calendarUrl, ct);
            if (events.Count > 0)
            {
                _cachedEvents = events;
                _lastFetch = DateTime.UtcNow;
                _logger.LogInformation("EconomicCalendar: {Count} Events geladen", events.Count);
            }
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private async Task<List<EconomicEvent>> FetchFromApiAsync(string url, CancellationToken ct)
    {
        var events = new List<EconomicEvent>();
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("EconomicCalendar API returned {Status}", (int)response.StatusCode);
                return events;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);

            // Generisches JSON-Parsing: erwartet Array mit Objekten die title/date/impact/currency enthalten
            var root = doc.RootElement;
            var array = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("data", out var data) ? data
                : root.TryGetProperty("events", out var ev) ? ev
                : root;

            if (array.ValueKind != JsonValueKind.Array)
                return events;

            foreach (var item in array.EnumerateArray())
            {
                var eventObj = ParseEventFromJson(item);
                if (eventObj != null)
                    events.Add(eventObj);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EconomicCalendar: Fehler beim Abrufen von {Url}", url);
        }

        return events;
    }

    private static EconomicEvent? ParseEventFromJson(JsonElement item)
    {
        try
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString()
                : item.TryGetProperty("name", out var n) ? n.GetString()
                : item.TryGetProperty("event", out var e) ? e.GetString()
                : null;

            if (string.IsNullOrEmpty(title))
                return null;

            DateTime eventTime = DateTime.UtcNow;
            if (item.TryGetProperty("date", out var d))
            {
                if (DateTime.TryParse(d.GetString(), out var parsed))
                    eventTime = parsed.ToUniversalTime();
            }
            else if (item.TryGetProperty("datetime", out var dt))
            {
                if (DateTime.TryParse(dt.GetString(), out var parsed))
                    eventTime = parsed.ToUniversalTime();
            }

            var impactStr = item.TryGetProperty("impact", out var imp) ? imp.GetString()
                : item.TryGetProperty("importance", out var imprt) ? imprt.GetString()
                : "low";

            var impact = impactStr?.ToLowerInvariant() switch
            {
                "high" or "3" or "red" => EventImpact.High,
                "medium" or "2" or "orange" => EventImpact.Medium,
                _ => EventImpact.Low
            };

            var currency = item.TryGetProperty("currency", out var c) ? c.GetString()
                : item.TryGetProperty("country", out var co) ? co.GetString()
                : "";

            return new EconomicEvent
            {
                Title = title,
                EventTime = eventTime,
                Impact = impact,
                Currency = currency?.ToUpperInvariant() ?? ""
            };
        }
        catch { /* Einzelnes Event mit ungueltigem Format – ueberspringen */ return null; }
    }

    /// <summary>Statische Liste bekannter regelmaessiger High-Impact-Events.</summary>
    private static List<EconomicEvent> GetStaticHighImpactEvents()
    {
        var events = new List<EconomicEvent>();
        var now = DateTime.UtcNow;

        // NFP (Non-Farm Payrolls): erster Freitag jeden Monats, 13:30 UTC
        var nfpDate = GetFirstFridayOfMonth(now.Year, now.Month);
        if (nfpDate < DateOnly.FromDateTime(now))
            nfpDate = GetFirstFridayOfMonth(now.Month == 12 ? now.Year + 1 : now.Year, now.Month == 12 ? 1 : now.Month + 1);

        events.Add(new EconomicEvent
        {
            Title = "Non-Farm Payrolls (NFP)",
            EventTime = nfpDate.ToDateTime(new TimeOnly(13, 30)),
            Impact = EventImpact.High,
            Currency = "USD"
        });

        // FOMC: 8 Sitzungen pro Jahr (ungefaehre Termine: jeden 6. Mittwoch ab Januar)
        // Vereinfacht: Mittwochs, 19:00 UTC
        var fomcDates = GetFomcDates(now.Year);
        foreach (var fomcDate in fomcDates.Where(d => d >= now.AddDays(-1)))
        {
            events.Add(new EconomicEvent
            {
                Title = "FOMC Zinsentscheid",
                EventTime = fomcDate,
                Impact = EventImpact.High,
                Currency = "USD"
            });
        }

        // EZB Zinsentscheid: ca. alle 6 Wochen, Donnerstag 13:15 UTC
        var ecbDates = GetEcbDates(now.Year);
        foreach (var ecbDate in ecbDates.Where(d => d >= now.AddDays(-1)))
        {
            events.Add(new EconomicEvent
            {
                Title = "EZB Zinsentscheid",
                EventTime = ecbDate,
                Impact = EventImpact.High,
                Currency = "EUR"
            });
        }

        // CPI (Verbraucherpreisindex USA): ca. 10.-15. jeden Monats, 13:30 UTC
        var cpiDate = new DateTime(now.Year, now.Month, 13, 13, 30, 0, DateTimeKind.Utc);
        if (cpiDate < now)
            cpiDate = cpiDate.AddMonths(1);
        events.Add(new EconomicEvent
        {
            Title = "US CPI (Verbraucherpreisindex)",
            EventTime = cpiDate,
            Impact = EventImpact.High,
            Currency = "USD"
        });

        return events;
    }

    private static DateOnly GetFirstFridayOfMonth(int year, int month)
    {
        var date = new DateOnly(year, month, 1);
        while (date.DayOfWeek != DayOfWeek.Friday)
            date = date.AddDays(1);
        return date;
    }

    /// <summary>Ungefaehre FOMC-Termine (8 pro Jahr, Mittwochs 19:00 UTC).</summary>
    private static List<DateTime> GetFomcDates(int year)
    {
        // Typische FOMC-Monate: Jan, Marz, Mai, Jun, Jul, Sep, Nov, Dez
        var months = new[] { 1, 3, 5, 6, 7, 9, 11, 12 };
        return months.Select(m =>
        {
            // Dritter Mittwoch des Monats als Annaeherung
            var date = new DateOnly(year, m, 1);
            var wednesdayCount = 0;
            while (wednesdayCount < 3)
            {
                if (date.DayOfWeek == DayOfWeek.Wednesday)
                    wednesdayCount++;
                if (wednesdayCount < 3)
                    date = date.AddDays(1);
            }
            return date.ToDateTime(new TimeOnly(19, 0));
        }).ToList();
    }

    /// <summary>Ungefaehre EZB-Termine (8 pro Jahr, Donnerstags 13:15 UTC).</summary>
    private static List<DateTime> GetEcbDates(int year)
    {
        var months = new[] { 1, 3, 4, 6, 7, 9, 10, 12 };
        return months.Select(m =>
        {
            // Zweiter Donnerstag des Monats als Annaeherung
            var date = new DateOnly(year, m, 1);
            var thursdayCount = 0;
            while (thursdayCount < 2)
            {
                if (date.DayOfWeek == DayOfWeek.Thursday)
                    thursdayCount++;
                if (thursdayCount < 2)
                    date = date.AddDays(1);
            }
            return date.ToDateTime(new TimeOnly(13, 15));
        }).ToList();
    }

    /// <summary>Extrahiert Waehrungen aus einem Forex-Symbol (z.B. EURUSD -> EUR, USD).</summary>
    private static List<string> ExtractCurrencies(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        var currencies = new List<string>();

        if (s.StartsWith("XAU") || s.StartsWith("XAG"))
        {
            currencies.Add("USD"); // Gold/Silber in USD
            return currencies;
        }

        if (s.Length >= 6)
        {
            currencies.Add(s[..3]);
            currencies.Add(s[3..6]);
        }

        // Indizes
        if (s.StartsWith("US"))
            currencies.Add("USD");
        if (s.StartsWith("DE"))
            currencies.Add("EUR");
        if (s.StartsWith("UK"))
            currencies.Add("GBP");
        if (s.StartsWith("JP"))
            currencies.Add("JPY");

        return currencies.Distinct().ToList();
    }
}

public enum EventImpact
{
    Low,
    Medium,
    High
}

public class EconomicEvent
{
    public string Title { get; set; } = string.Empty;
    public DateTime EventTime { get; set; }
    public EventImpact Impact { get; set; }
    public string Currency { get; set; } = string.Empty;

    public bool AffectsCurrency(string currency)
    {
        return Currency.Equals(currency, StringComparison.OrdinalIgnoreCase);
    }
}
