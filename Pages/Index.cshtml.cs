using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using ClaudeTradingBot.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace ClaudeTradingBot.Pages;

public class IndexModel : PageModel
{
    private readonly TradingDbContext _db;
    private readonly TradingEngine _engine;
    private readonly IBrokerService _broker;
    private readonly IRiskManager _risk;
    private readonly MarketHoursService _marketHours;
    private readonly EconomicCalendarService _calendar;

    public DashboardViewModel Dashboard { get; set; } = new();

    public IndexModel(
        TradingDbContext db,
        TradingEngine engine,
        IBrokerService broker,
        IRiskManager risk,
        MarketHoursService marketHours,
        EconomicCalendarService calendar)
    {
        _db = db;
        _engine = engine;
        _broker = broker;
        _risk = risk;
        _marketHours = marketHours;
        _calendar = calendar;
    }

    public async Task OnGetAsync()
    {
        var positions = await _broker.GetPositionsAsync();
        var cash = await _broker.GetAccountCashAsync();
        var portfolioValue = await _broker.GetPortfolioValueAsync();

        var todayStart = DateTime.UtcNow.Date;
        var tradesToday = await _db.Trades
            .Where(t => t.CreatedAt >= todayStart)
            .CountAsync();

        var recentTrades = await _db.Trades
            .OrderByDescending(t => t.CreatedAt)
            .Take(20)
            .ToListAsync();

        var recentLogs = await _db.TradingLogs
            .OrderByDescending(l => l.Timestamp)
            .Take(30)
            .ToListAsync();

        var pnlHistory = await _db.DailyPnLs
            .OrderByDescending(d => d.Date)
            .Take(30)
            .ToListAsync();

        Dashboard = new DashboardViewModel
        {
            PortfolioValue = portfolioValue,
            AvailableCash = cash,
            OpenPositions = positions.Count,
            TradesToday = tradesToday,
            IsEngineRunning = _engine.IsRunning,
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
            PnLHistory = pnlHistory
        };
    }
}
