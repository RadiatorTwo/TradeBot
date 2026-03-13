using ClaudeTradingBot.Data;
using ClaudeTradingBot.Hubs;
using ClaudeTradingBot.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClaudeTradingBot.Services;

/// <summary>Pusht Dashboard-Daten per SignalR an verbundene Clients.</summary>
public class DashboardBroadcastService : BackgroundService
{
    private readonly IHubContext<TradingHub> _hubContext;
    private readonly IDbContextFactory<TradingDbContext> _dbFactory;
    private readonly TradingEngine _engine;
    private readonly IBrokerService _broker;
    private readonly IRiskManager _risk;
    private readonly MarketHoursService _marketHours;
    private readonly EconomicCalendarService _calendar;
    private readonly ILogger<DashboardBroadcastService> _logger;

    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

    private DashboardViewModel? _lastViewModel;

    public DashboardBroadcastService(
        IHubContext<TradingHub> hubContext,
        IDbContextFactory<TradingDbContext> dbFactory,
        TradingEngine engine,
        IBrokerService broker,
        IRiskManager risk,
        MarketHoursService marketHours,
        EconomicCalendarService calendar,
        ILogger<DashboardBroadcastService> logger)
    {
        _hubContext = hubContext;
        _dbFactory = dbFactory;
        _engine = engine;
        _broker = broker;
        _risk = risk;
        _marketHours = marketHours;
        _calendar = calendar;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warten bis Broker-Verbindung steht
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var isIdle = _engine.IsPaused || _risk.IsKillSwitchActive;

                if (isIdle && _lastViewModel != null)
                {
                    // Im Idle-Modus: nur Status-Flags aktualisieren, keine Broker/DB-Calls
                    var idleUpdate = _lastViewModel with
                    {
                        IsEngineRunning = _engine.IsRunning,
                        IsEnginePaused = _engine.IsPaused,
                        IsKillSwitchActive = _risk.IsKillSwitchActive,
                        IsMarketOpen = _marketHours.IsMarketOpen(),
                        MarketStatus = _marketHours.GetMarketStatus()
                    };
                    await _hubContext.Clients.All.SendAsync(
                        TradingHub.DashboardUpdate, idleUpdate, stoppingToken);
                }
                else
                {
                    // Aktiv: volle Daten laden
                    var viewModel = await BuildDashboardViewModelAsync(stoppingToken);
                    _lastViewModel = viewModel;
                    await _hubContext.Clients.All.SendAsync(
                        TradingHub.DashboardUpdate, viewModel, stoppingToken);
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

            var interval = _engine.IsPaused || _risk.IsKillSwitchActive
                ? IdleInterval
                : ActiveInterval;
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task<DashboardViewModel> BuildDashboardViewModelAsync(CancellationToken ct)
    {
        var positions = new List<Position>();
        decimal cash = 0, portfolioValue = 0;
        var account = new AccountDetails();

        if (_broker.IsConnected)
        {
            try
            {
                positions = await _broker.GetPositionsAsync(ct);
                account = await _broker.GetAccountDetailsAsync(ct);
                cash = account.FreeMargin > 0 ? account.FreeMargin : account.Balance;
                portfolioValue = account.Equity > 0 ? account.Equity : account.Balance;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DashboardBroadcast: Broker-Daten nicht verfuegbar");
            }
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var todayStart = DateTime.UtcNow.Date;
        var tradesToday = await db.Trades
            .Where(t => t.CreatedAt >= todayStart)
            .CountAsync(ct);

        var recentTrades = await db.Trades
            .OrderByDescending(t => t.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        var recentLogs = await db.TradingLogs
            .OrderByDescending(l => l.Timestamp)
            .Take(30)
            .ToListAsync(ct);

        var pnlHistory = await db.DailyPnLs
            .OrderByDescending(d => d.Date)
            .Take(30)
            .ToListAsync(ct);

        // Trade-Statistiken berechnen
        var allTradesForStats = await db.Trades.ToListAsync(ct);
        var stats = TradingStatsService.Calculate(allTradesForStats, pnlHistory);

        return new DashboardViewModel
        {
            PortfolioValue = portfolioValue,
            AvailableCash = cash,
            Account = account,
            OpenPositions = positions.Count,
            TradesToday = tradesToday,
            IsEngineRunning = _engine.IsRunning,
            IsEnginePaused = _engine.IsPaused,
            IsKillSwitchActive = _risk.IsKillSwitchActive,
            IsTradeLockerConnected = _broker.IsConnected,
            IsMarketOpen = _marketHours.IsMarketOpen(),
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
