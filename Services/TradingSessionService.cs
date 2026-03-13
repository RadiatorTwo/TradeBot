using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>Prueft ob die aktuelle Zeit in einer erlaubten Trading-Session liegt.</summary>
public class TradingSessionService
{
    private readonly IOptionsMonitor<RiskSettings> _settingsMonitor;
    private readonly ILogger<TradingSessionService> _logger;

    private RiskSettings Settings => _settingsMonitor.CurrentValue;

    // Vordefinierte Sessions (UTC)
    private static readonly Dictionary<string, (TimeOnly Start, TimeOnly End)> Sessions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["London"] = (new TimeOnly(8, 0), new TimeOnly(17, 0)),
        ["NewYork"] = (new TimeOnly(13, 0), new TimeOnly(22, 0)),
        ["Overlap"] = (new TimeOnly(13, 0), new TimeOnly(17, 0)),
        ["Tokyo"] = (new TimeOnly(0, 0), new TimeOnly(9, 0)),
        ["Sydney"] = (new TimeOnly(21, 0), new TimeOnly(6, 0)), // Ueber Mitternacht
    };

    public TradingSessionService(IOptionsMonitor<RiskSettings> settingsMonitor, ILogger<TradingSessionService> logger)
    {
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    /// <summary>Prueft ob fuer das gegebene Symbol gerade eine erlaubte Session aktiv ist.</summary>
    public bool IsSessionActive(string symbol)
    {
        var allowed = Settings.AllowedSessions;

        // Keine Sessions konfiguriert = immer aktiv
        if (allowed.Count == 0)
            return true;

        var now = TimeOnly.FromDateTime(DateTime.UtcNow);

        // JPY-Pairs: Tokyo-Session ist zusaetzlich erlaubt
        if (symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase) && IsInSession("Tokyo", now))
            return true;

        foreach (var sessionName in allowed)
        {
            if (IsInSession(sessionName, now))
                return true;
        }

        return false;
    }

    /// <summary>Gibt die aktuelle(n) aktive(n) Session(s) zurueck.</summary>
    public List<string> GetActiveSessions()
    {
        var now = TimeOnly.FromDateTime(DateTime.UtcNow);
        return Sessions
            .Where(s => IsTimeInRange(now, s.Value.Start, s.Value.End))
            .Select(s => s.Key)
            .ToList();
    }

    private bool IsInSession(string sessionName, TimeOnly now)
    {
        if (!Sessions.TryGetValue(sessionName, out var range))
        {
            _logger.LogWarning("Unbekannte Trading-Session: {Session}. Verfuegbar: {Available}",
                sessionName, string.Join(", ", Sessions.Keys));
            return false;
        }

        return IsTimeInRange(now, range.Start, range.End);
    }

    /// <summary>Prueft ob eine Zeit in einem Bereich liegt (unterstuetzt Ueber-Mitternacht-Bereiche).</summary>
    private static bool IsTimeInRange(TimeOnly time, TimeOnly start, TimeOnly end)
    {
        if (start <= end)
            return time >= start && time <= end;

        // Ueber Mitternacht (z.B. Sydney: 21:00 - 06:00)
        return time >= start || time <= end;
    }
}
