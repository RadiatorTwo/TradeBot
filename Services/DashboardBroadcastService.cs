using ClaudeTradingBot.Data;
using ClaudeTradingBot.Hubs;
using ClaudeTradingBot.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClaudeTradingBot.Services;

/// <summary>Pusht Dashboard-Daten per SignalR an verbundene Clients (Multi-Account).</summary>
public class DashboardBroadcastService : BackgroundService
{
    private readonly IHubContext<TradingHub> _hubContext;
    private readonly IDbContextFactory<TradingDbContext> _dbFactory;
    private readonly AccountManager _accountMgr;
    private readonly MarketHoursService _marketHours;
    private readonly EconomicCalendarService _calendar;
    private readonly ILogger<DashboardBroadcastService> _logger;

    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, DashboardViewModel?> _lastViewModels = new();

    public DashboardBroadcastService(
        IHubContext<TradingHub> hubContext,
        IDbContextFactory<TradingDbContext> dbFactory,
        AccountManager accountMgr,
        MarketHoursService marketHours,
        EconomicCalendarService calendar,
        ILogger<DashboardBroadcastService> logger)
    {
        _hubContext = hubContext;
        _dbFactory = dbFactory;
        _accountMgr = accountMgr;
        _marketHours = marketHours;
        _calendar = calendar;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var ctx in _accountMgr.Accounts)
                {
                    var isIdle = ctx.Engine.IsPaused || ctx.Risk.IsKillSwitchActive;
                    _lastViewModels.TryGetValue(ctx.AccountId, out var lastVm);

                    if (isIdle && lastVm != null)
                    {
                        var idleUpdate = lastVm with
                        {
                            IsEngineRunning = ctx.Engine.IsRunning,
                            IsEnginePaused = ctx.Engine.IsPaused,
                            IsKillSwitchActive = ctx.Risk.IsKillSwitchActive,
                            IsMarketOpen = _marketHours.IsMarketOpen(),
                            MarketStatus = _marketHours.GetMarketStatus()
                        };
                        await _hubContext.Clients.All.SendAsync(
                            TradingHub.DashboardUpdate, idleUpdate, stoppingToken);
                    }
                    else
                    {
                        var viewModel = await BuildDashboardViewModelAsync(ctx, stoppingToken);
                        _lastViewModels[ctx.AccountId] = viewModel;
                        await _hubContext.Clients.All.SendAsync(
                            TradingHub.DashboardUpdate, viewModel, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DashboardBroadcast: Fehler beim Senden");
            }

            // Interval basierend auf einem beliebigen aktiven Account
            var anyActive = _accountMgr.Accounts.Any(a => !a.Engine.IsPaused && !a.Risk.IsKillSwitchActive);
            var interval = anyActive ? ActiveInterval : IdleInterval;
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task<DashboardViewModel> BuildDashboardViewModelAsync(AccountContext ctx, CancellationToken ct)
    {
        var positions = new List<Position>();
        decimal cash = 0, portfolioValue = 0;
        var account = new AccountDetails();

        if (ctx.EffectiveBroker.IsConnected)
        {
            try
            {
                positions = await ctx.EffectiveBroker.GetPositionsAsync(ct);
                account = await ctx.EffectiveBroker.GetAccountDetailsAsync(ct);
                cash = account.FreeMargin > 0 ? account.FreeMargin : account.Balance;
                portfolioValue = account.Equity > 0 ? account.Equity : account.Balance;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DashboardBroadcast: Broker-Daten nicht verfuegbar fuer {AccountId}", ctx.AccountId);
            }
        }

        // Prometheus-Gauges aktualisieren
        TradingMetrics.PortfolioEquity.WithLabels(ctx.AccountId).Set((double)portfolioValue);
        TradingMetrics.OpenPositionCount.WithLabels(ctx.AccountId).Set(positions.Count);
        TradingMetrics.KillSwitchActive.WithLabels(ctx.AccountId).Set(ctx.Risk.IsKillSwitchActive ? 1 : 0);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var aid = ctx.AccountId;

        var todayStart = DateTime.UtcNow.Date;
        var tradesToday = await db.Trades
            .Where(t => t.AccountId == aid && t.CreatedAt >= todayStart)
            .CountAsync(ct);

        var recentTrades = await db.Trades
            .Where(t => t.AccountId == aid)
            .OrderByDescending(t => t.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        var recentLogs = await db.TradingLogs
            .Where(l => l.AccountId == aid)
            .OrderByDescending(l => l.Timestamp)
            .Take(30)
            .ToListAsync(ct);

        var pnlHistory = await db.DailyPnLs
            .Where(d => d.AccountId == aid)
            .OrderByDescending(d => d.Date)
            .Take(30)
            .ToListAsync(ct);

        var stats = await TradingStatsService.CalculateFromDbAsync(db, pnlHistory, ct);

        return new DashboardViewModel
        {
            AccountId = aid,
            AccountDisplayName = ctx.DisplayName,
            PortfolioValue = portfolioValue,
            AvailableCash = cash,
            Account = account,
            OpenPositions = positions.Count,
            TradesToday = tradesToday,
            IsEngineRunning = ctx.Engine.IsRunning,
            IsEnginePaused = ctx.Engine.IsPaused,
            IsKillSwitchActive = ctx.Risk.IsKillSwitchActive,
            IsTradeLockerConnected = ctx.EffectiveBroker.IsConnected,
            IsMarketOpen = _marketHours.IsMarketOpen(),
            IsPaperTrading = ctx.PaperTrading.IsPaperTradingActive,
            MarketStatus = _marketHours.GetMarketStatus(),
            UpcomingEvents = _calendar.GetUpcomingHighImpactEvents(5)
                .Select(e => new UpcomingEventViewModel
                {
                    Title = e.Title,
                    EventTime = e.EventTime,
                    Impact = e.Impact.ToString(),
                    Currency = e.Currency
                }).ToList(),
            Positions = positions,
            RecentTrades = recentTrades,
            RecentLogs = recentLogs,
            PnLHistory = pnlHistory,
            Stats = stats
        };
    }
}
