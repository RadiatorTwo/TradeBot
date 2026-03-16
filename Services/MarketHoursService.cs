namespace ClaudeTradingBot.Services;

/// <summary>Prueft ob der Markt fuer ein bestimmtes Instrument geoeffnet ist.</summary>
public class MarketHoursService
{
    private readonly ILogger<MarketHoursService> _logger;

    // Forex: Sonntag 22:00 UTC bis Freitag 22:00 UTC
    // Feiertage an denen Forex geschlossen ist
    private static readonly HashSet<DateOnly> Holidays = new()
    {
        // 2025
        new DateOnly(2025, 12, 25), // Weihnachten
        new DateOnly(2026, 1, 1),   // Neujahr
        // 2026
        new DateOnly(2026, 12, 25),
        new DateOnly(2027, 1, 1),
    };

    /// <summary>Puffer vor Marktschluss – keine neuen Positionen in den letzten 30 Minuten.</summary>
    private static readonly TimeSpan CloseBuffer = TimeSpan.FromMinutes(30);

    public MarketHoursService(ILogger<MarketHoursService> logger)
    {
        _logger = logger;
    }

    /// <summary>Prueft ob der Forex-Markt geoeffnet ist.</summary>
    public bool IsMarketOpen()
    {
        return IsMarketOpen("EURUSD"); // Forex als Default
    }

    /// <summary>Prueft ob der Markt fuer ein bestimmtes Symbol geoeffnet ist.</summary>
    public bool IsMarketOpen(string symbol)
    {
        var now = DateTime.UtcNow;
        var s = symbol.ToUpperInvariant();

        // Feiertags-Check
        if (Holidays.Contains(DateOnly.FromDateTime(now)))
            return false;

        // Crypto: 24/7
        if (s.Contains("BTC") || s.Contains("ETH") || s.Contains("CRYPTO"))
            return true;

        // Forex (Waehrungspaare, Gold, Silber, Oel)
        if (IsForexSymbol(s))
            return IsForexMarketOpen(now);

        // Indizes: spezifische Boersenzeiten
        if (IsIndexSymbol(s))
            return IsIndexMarketOpen(s, now);

        // Unbekannt: Forex-Zeiten als Fallback
        return IsForexMarketOpen(now);
    }

    /// <summary>Prueft ob es sicher ist, neue Positionen zu oeffnen (nicht zu nah am Marktschluss).</summary>
    public bool IsSafeToOpenPosition(string symbol)
    {
        if (!IsMarketOpen(symbol))
            return false;

        var timeUntilClose = GetTimeUntilClose(symbol);
        return timeUntilClose == null || timeUntilClose.Value > CloseBuffer;
    }

    /// <summary>Wann schliesst der Markt? null wenn Markt geschlossen oder 24/7.</summary>
    public TimeSpan? GetTimeUntilClose(string symbol)
    {
        var now = DateTime.UtcNow;
        var s = symbol.ToUpperInvariant();

        if (s.Contains("BTC") || s.Contains("ETH"))
            return null; // 24/7

        if (IsForexSymbol(s) || !IsIndexSymbol(s))
        {
            // Forex schliesst Freitag 22:00 UTC
            if (now.DayOfWeek == DayOfWeek.Friday)
            {
                var close = now.Date.AddHours(22);
                if (now < close)
                    return close - now;
            }
            return null; // Nicht Freitag, kein nahes Schliessen
        }

        return null;
    }

    /// <summary>Wann oeffnet der Markt wieder? null wenn bereits offen.</summary>
    public DateTime? GetNextOpen(string symbol)
    {
        if (IsMarketOpen(symbol))
            return null;

        var now = DateTime.UtcNow;

        // Naechsten Sonntag 22:00 UTC finden
        var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilSunday == 0 && now.Hour >= 22)
            daysUntilSunday = 7; // Schon nach Oeffnung -> naechste Woche

        var nextOpen = now.Date.AddDays(daysUntilSunday).AddHours(22);

        // Feiertags-Check: wenn der naechste Oeffnungstag ein Feiertag ist, einen Tag weiter
        while (Holidays.Contains(DateOnly.FromDateTime(nextOpen)))
            nextOpen = nextOpen.AddDays(1);

        return nextOpen;
    }

    /// <summary>Gibt den aktuellen Marktstatus als Text zurueck (fuer Dashboard).</summary>
    public string GetMarketStatus(string symbol = "EURUSD")
    {
        if (IsMarketOpen(symbol))
        {
            var untilClose = GetTimeUntilClose(symbol);
            if (untilClose.HasValue && untilClose.Value.TotalHours < 2)
            {
                var closeTime = LocalTime.FromUtc(DateTime.UtcNow.Add(untilClose.Value));
                return $"Offen (schliesst {closeTime:HH:mm}, in {untilClose.Value.Hours}h {untilClose.Value.Minutes}m)";
            }
            return "Offen";
        }

        var nextOpen = GetNextOpen(symbol);
        if (nextOpen.HasValue)
        {
            var until = nextOpen.Value - DateTime.UtcNow;
            var localOpen = LocalTime.FromUtc(nextOpen.Value);
            return $"Geschlossen (oeffnet {localOpen:ddd HH:mm}, in {(int)until.TotalHours}h {until.Minutes}m)";
        }

        return "Geschlossen";
    }

    // ── Private Hilfsmethoden ────────────────────────────────────────────

    private static readonly string[] ForexCurrencies = { "EUR", "USD", "GBP", "JPY", "CHF", "AUD", "NZD", "CAD" };

    private static bool IsForexSymbol(string s)
    {
        // Waehrungspaare (6 Buchstaben wie EURUSD, GBPJPY)
        var currencies = ForexCurrencies;
        if (s.Length == 6 && currencies.Any(c => s.StartsWith(c)) && currencies.Any(c => s.EndsWith(c)))
            return true;

        // Edelmetalle
        if (s.StartsWith("XAU") || s.StartsWith("XAG"))
            return true;

        // Oel
        if (s.StartsWith("XTI") || s.StartsWith("XBR") || s.Contains("OIL"))
            return true;

        return false;
    }

    private static bool IsIndexSymbol(string s)
    {
        return s.StartsWith("US") || s.StartsWith("UK") || s.StartsWith("DE") || s.StartsWith("JP")
            || s.Contains("100") || s.Contains("500") || s.Contains("DAX") || s.Contains("NASDAQ");
    }

    /// <summary>Forex: So 22:00 UTC bis Fr 22:00 UTC</summary>
    private static bool IsForexMarketOpen(DateTime utcNow)
    {
        var day = utcNow.DayOfWeek;
        var hour = utcNow.Hour;

        return day switch
        {
            DayOfWeek.Saturday => false,
            DayOfWeek.Sunday => hour >= 22, // Oeffnet Sonntag 22:00 UTC
            DayOfWeek.Friday => hour < 22,  // Schliesst Freitag 22:00 UTC
            _ => true                       // Mo-Do: immer offen
        };
    }

    /// <summary>Indizes: USA 14:30-21:00 UTC, Europa 08:00-16:30 UTC, Japan 00:00-06:00 UTC</summary>
    private static bool IsIndexMarketOpen(string symbol, DateTime utcNow)
    {
        var hour = utcNow.Hour;
        var minute = utcNow.Minute;
        var timeMinutes = hour * 60 + minute;
        var day = utcNow.DayOfWeek;

        // Wochenende: geschlossen
        if (day is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        // US-Indizes (US100, US500, US30): erweiterte Handelszeiten ca. 23:00-22:00 UTC (fast 24h Mo-Fr)
        if (symbol.StartsWith("US"))
            return timeMinutes is >= 0 and < 22 * 60 || timeMinutes >= 23 * 60;

        // Europaeische Indizes (DE40, UK100): 08:00-22:00 UTC
        if (symbol.StartsWith("DE") || symbol.StartsWith("UK"))
            return timeMinutes >= 8 * 60 && timeMinutes < 22 * 60;

        // Japanische Indizes: 00:00-06:30 UTC
        if (symbol.StartsWith("JP"))
            return timeMinutes < 6 * 60 + 30;

        // Fallback: Forex-Zeiten
        return IsForexMarketOpen(utcNow);
    }
}
