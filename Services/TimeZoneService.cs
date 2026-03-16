namespace ClaudeTradingBot.Services;

/// <summary>
/// Konvertiert UTC-Zeiten in die lokale Zeitzone des Servers.
/// Wird in Razor-Komponenten verwendet um Zeiten konsistent anzuzeigen.
/// </summary>
public static class LocalTime
{
    private static readonly TimeZoneInfo _tz = TimeZoneInfo.Local;

    /// <summary>Konvertiert UTC DateTime in lokale Zeit.</summary>
    public static DateTime FromUtc(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc), _tz);

    /// <summary>Konvertiert nullable UTC DateTime in lokale Zeit.</summary>
    public static DateTime? FromUtc(DateTime? utc)
        => utc.HasValue ? FromUtc(utc.Value) : null;

    /// <summary>Formatiert UTC DateTime als lokale Zeit.</summary>
    public static string Format(DateTime utc, string format = "dd.MM.yyyy HH:mm")
        => FromUtc(utc).ToString(format);

    /// <summary>Formatiert nullable UTC DateTime als lokale Zeit.</summary>
    public static string Format(DateTime? utc, string format = "dd.MM.yyyy HH:mm", string fallback = "–")
        => utc.HasValue ? Format(utc.Value, format) : fallback;

    /// <summary>Kurzformat fuer Dashboard/Tabellen.</summary>
    public static string Short(DateTime utc) => Format(utc, "dd.MM HH:mm");

    /// <summary>Zeitzone-Abkuerzung (z.B. "CET", "CEST").</summary>
    public static string ZoneName => _tz.IsDaylightSavingTime(DateTime.UtcNow)
        ? _tz.DaylightName : _tz.StandardName;
}
