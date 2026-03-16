using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using ClaudeTradingBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClaudeTradingBot.HealthChecks;

/// <summary>Warnt wenn laenger als 2h kein Trade-Versuch stattgefunden hat (bei laufender Engine).</summary>
public class TradingActivityHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<TradingDbContext> _dbFactory;
    private readonly AccountManager _accountMgr;
    private readonly MarketHoursService _marketHours;

    private static readonly TimeSpan InactivityThreshold = TimeSpan.FromHours(2);

    public TradingActivityHealthCheck(
        IDbContextFactory<TradingDbContext> dbFactory,
        AccountManager accountMgr,
        MarketHoursService marketHours)
    {
        _dbFactory = dbFactory;
        _accountMgr = accountMgr;
        _marketHours = marketHours;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!_accountMgr.HasAccounts)
            return HealthCheckResult.Healthy("Keine Accounts konfiguriert – Aktivitaetspruefung uebersprungen");

        if (!_marketHours.IsMarketOpen())
            return HealthCheckResult.Healthy("Markt geschlossen – Aktivitaetspruefung uebersprungen");

        var anyRunning = _accountMgr.Accounts.Any(a => a.Engine.IsRunning && !a.Engine.IsPaused);
        if (!anyRunning)
            return HealthCheckResult.Healthy("Keine Engine aktiv – Aktivitaetspruefung uebersprungen");

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var latestTrade = await db.Trades
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => t.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (latestTrade == default)
                return HealthCheckResult.Degraded("Noch keine Trades in der Datenbank");

            var timeSinceLast = DateTime.UtcNow - latestTrade;

            if (timeSinceLast > InactivityThreshold)
                return HealthCheckResult.Degraded(
                    $"Letzter Trade vor {timeSinceLast.TotalMinutes:F0} Minuten (Schwelle: {InactivityThreshold.TotalMinutes:F0} Min)");

            return HealthCheckResult.Healthy(
                $"Letzter Trade vor {timeSinceLast.TotalMinutes:F0} Minuten");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Aktivitaetspruefung fehlgeschlagen: {ex.Message}");
        }
    }
}
