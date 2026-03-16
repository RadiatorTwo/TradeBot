using System.Collections.Concurrent;
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

    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleBroadcastInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BrokerRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, DashboardViewModel> _lastViewModels = new();
    private readonly ConcurrentDictionary<string, BrokerDataSnapshot> _brokerData = new();

    private record BrokerDataSnapshot(
        List<Position> Positions,
        AccountDetails Account,
        decimal Cash,
        decimal PortfolioValue,
        DateTime FetchedAt);

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

    /// <summary>
    /// Gibt das zuletzt gebaute DashboardViewModel fuer einen Account zurueck.
    /// Wird von Dashboard.razor genutzt, damit beim Accountwechsel sofort Daten
    /// angezeigt werden (ohne auf Broker-Calls warten zu muessen).
    /// </summary>
    public DashboardViewModel? GetLastViewModel(string accountId)
    {
        _lastViewModels.TryGetValue(accountId, out var vm);
        return vm;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        _ = RefreshBrokerDataLoopAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var ctx in _accountMgr.Accounts)
                {
                    var viewModel = await BuildDashboardViewModelAsync(ctx, stoppingToken);
                    _lastViewModels[ctx.AccountId] = viewModel;
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

            var anyActive = _accountMgr.Accounts.Any(a => !a.Engine.IsPaused && !a.Risk.IsKillSwitchActive);
            var interval = anyActive ? BroadcastInterval : IdleBroadcastInterval;
            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>
    /// Separater Hintergrund-Loop der Broker-Daten (Positionen, Kontodetails) abruft.
    /// Laeuft unabhaengig vom Broadcast-Loop, damit der Broadcast nie auf den
    /// globalen Broker-Throttle warten muss.
    /// </summary>
    private async Task RefreshBrokerDataLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var ctx in _accountMgr.Accounts)
            {
                if (ct.IsCancellationRequested) break;
                if (!ctx.EffectiveBroker.IsConnected) continue;

                try
                {
                    var positions = await ctx.EffectiveBroker.GetPositionsAsync(ct);
                    var account = await ctx.EffectiveBroker.GetAccountDetailsAsync(ct);
                    var cash = account.FreeMargin > 0 ? account.FreeMargin : account.Balance;
                    var portfolioValue = account.Equity > 0 ? account.Equity : account.Balance;

                    _brokerData[ctx.AccountId] = new BrokerDataSnapshot(
                        positions, account, cash, portfolioValue, DateTime.UtcNow);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "DashboardBroadcast: Broker-Refresh fuer {AccountId} fehlgeschlagen", ctx.AccountId);
                }
            }

            try { await Task.Delay(BrokerRefreshInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Baut das ViewModel aus gecachten Broker-Daten + frischen DB-Daten.
    /// Blockiert NICHT auf den Broker-Throttle – nutzt immer nur vorhandene Daten.
    /// </summary>
    private async Task<DashboardViewModel> BuildDashboardViewModelAsync(AccountContext ctx, CancellationToken ct)
    {
        var positions = new List<Position>();
        decimal cash = 0, portfolioValue = 0;
        var account = new AccountDetails();

        if (_brokerData.TryGetValue(ctx.AccountId, out var snapshot))
        {
            positions = snapshot.Positions;
            account = snapshot.Account;
            cash = snapshot.Cash;
            portfolioValue = snapshot.PortfolioValue;
        }

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
