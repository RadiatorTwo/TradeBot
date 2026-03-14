using ClaudeTradingBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClaudeTradingBot.HealthChecks;

/// <summary>Prueft ob die SQLite-Datenbank erreichbar und funktionsfaehig ist.</summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<TradingDbContext> _dbFactory;

    public DatabaseHealthCheck(IDbContextFactory<TradingDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var canConnect = await db.Database.CanConnectAsync(ct);

            if (!canConnect)
                return HealthCheckResult.Unhealthy("Datenbank nicht erreichbar");

            var tradeCount = await db.Trades.CountAsync(ct);
            return HealthCheckResult.Healthy($"Datenbank OK ({tradeCount} Trades)");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Datenbank-Fehler: {ex.Message}");
        }
    }
}
