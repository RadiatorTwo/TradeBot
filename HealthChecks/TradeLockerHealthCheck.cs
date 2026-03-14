using ClaudeTradingBot.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClaudeTradingBot.HealthChecks;

/// <summary>Prueft ob mindestens ein TradeLocker-Account verbunden ist.</summary>
public class TradeLockerHealthCheck : IHealthCheck
{
    private readonly AccountManager _accountMgr;

    public TradeLockerHealthCheck(AccountManager accountMgr)
    {
        _accountMgr = accountMgr;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!_accountMgr.HasAccounts)
            return Task.FromResult(HealthCheckResult.Degraded("Keine Accounts konfiguriert"));

        var connected = _accountMgr.Accounts.Count(a => a.EffectiveBroker.IsConnected);
        var total = _accountMgr.Accounts.Count;

        if (connected == 0)
            return Task.FromResult(HealthCheckResult.Unhealthy($"Kein Account verbunden (0/{total})"));

        if (connected < total)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{connected}/{total} Accounts verbunden"));

        return Task.FromResult(HealthCheckResult.Healthy($"Alle {total} Accounts verbunden"));
    }
}
