using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace ClaudeTradingBot.Pages;

public class TradesModel : PageModel
{
    private readonly TradingDbContext _db;

    public TradesModel(TradingDbContext db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public DateTime? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Symbol { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    public List<Trade> Trades { get; set; } = new();
    public List<string> AvailableSymbols { get; set; } = new();

    public async Task OnGetAsync()
    {
        AvailableSymbols = await _db.Trades
            .Select(t => t.Symbol)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync();

        var query = _db.Trades.AsQueryable();

        if (From.HasValue)
            query = query.Where(t => t.CreatedAt >= From.Value);

        if (To.HasValue)
            query = query.Where(t => t.CreatedAt <= To.Value.Date.AddDays(1));

        if (!string.IsNullOrEmpty(Symbol))
            query = query.Where(t => t.Symbol == Symbol);

        if (!string.IsNullOrEmpty(Status) && Enum.TryParse<TradeStatus>(Status, out var status))
            query = query.Where(t => t.Status == status);

        Trades = await query
            .OrderByDescending(t => t.CreatedAt)
            .Take(200)
            .ToListAsync();
    }
}
