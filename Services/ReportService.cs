using System.Text;
using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Generiert und versendet taegliche/woechentliche Performance-Reports per Telegram.
/// Laeuft als BackgroundService, sendet taeglich um ReportTimeUtc und woechentlich am Montag.
/// </summary>
public class ReportService : BackgroundService
{
    private readonly IDbContextFactory<TradingDbContext> _dbFactory;
    private readonly AccountManager _accountMgr;
    private readonly NotificationService _notification;
    private readonly IOptionsMonitor<ReportSettings> _settingsMonitor;
    private readonly ILogger<ReportService> _logger;

    private ReportSettings Settings => _settingsMonitor.CurrentValue;

    public ReportService(
        IDbContextFactory<TradingDbContext> dbFactory,
        AccountManager accountMgr,
        NotificationService notification,
        IOptionsMonitor<ReportSettings> settingsMonitor,
        ILogger<ReportService> logger)
    {
        _dbFactory = dbFactory;
        _accountMgr = accountMgr;
        _notification = notification;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        DateTime? lastDailyReport = null;
        DateTime? lastWeeklyReport = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_notification.IsConfigured || !Settings.Enabled)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue;
                }

                var now = DateTime.UtcNow;
                var reportTime = Settings.ReportTimeUtc;

                // Taeglicher Report
                if (Settings.DailyEnabled
                    && now.TimeOfDay >= reportTime
                    && (lastDailyReport == null || lastDailyReport.Value.Date < now.Date))
                {
                    await SendDailyReportAsync(now.Date, stoppingToken);
                    lastDailyReport = now;
                }

                // Woechentlicher Report (Montag)
                if (Settings.WeeklyEnabled
                    && now.DayOfWeek == DayOfWeek.Monday
                    && now.TimeOfDay >= reportTime
                    && (lastWeeklyReport == null || lastWeeklyReport.Value.Date < now.Date))
                {
                    await SendWeeklyReportAsync(now.Date, stoppingToken);
                    lastWeeklyReport = now;
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReportService: Fehler");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task SendDailyReportAsync(DateTime date, CancellationToken ct)
    {
        _logger.LogInformation("Generiere taeglichen Report fuer {Date:dd.MM.yyyy}", date);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var dayStart = date.Date;
        var dayEnd = dayStart.AddDays(1);

        var trades = await db.Trades
            .Where(t => t.CreatedAt >= dayStart && t.CreatedAt < dayEnd && t.Status == TradeStatus.Executed)
            .ToListAsync(ct);

        var closedTrades = await db.Trades
            .Where(t => t.ClosedAt >= dayStart && t.ClosedAt < dayEnd && t.RealizedPnL != null)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"📊 *Tages\\-Report {date:dd\\.MM\\.yyyy}*");
        sb.AppendLine();

        // Account-Infos
        foreach (var ctx in _accountMgr.Accounts)
        {
            if (!ctx.EffectiveBroker.IsConnected) continue;
            try
            {
                var details = await ctx.EffectiveBroker.GetAccountDetailsAsync(ct);
                sb.AppendLine($"*{EscapeMarkdownV2(ctx.DisplayName)}:* Equity ${details.Equity:N2}, Balance ${details.Balance:N2}");
            }
            catch
            {
                // Broker nicht erreichbar
            }
        }
        sb.AppendLine();

        // Trade-Zusammenfassung
        sb.AppendLine($"*Trades heute:* {trades.Count}");

        if (closedTrades.Count > 0)
        {
            var totalPnL = closedTrades.Sum(t => t.RealizedPnL ?? 0);
            var wins = closedTrades.Count(t => t.RealizedPnL > 0);
            var winRate = (double)wins / closedTrades.Count * 100;

            sb.AppendLine($"*Geschlossen:* {closedTrades.Count} \\(Win\\-Rate: {winRate:F0}%\\)");
            sb.AppendLine($"*Tages\\-PnL:* {(totalPnL >= 0 ? "\\+" : "")}${totalPnL:F2}");

            // Top/Flop
            var best = closedTrades.MaxBy(t => t.RealizedPnL ?? 0);
            var worst = closedTrades.MinBy(t => t.RealizedPnL ?? 0);

            if (best != null && best.RealizedPnL > 0)
                sb.AppendLine($"*Bester Trade:* {best.Symbol} \\+${best.RealizedPnL:F2}");
            if (worst != null && worst.RealizedPnL < 0)
                sb.AppendLine($"*Schlechtester:* {worst.Symbol} ${worst.RealizedPnL:F2}");
        }
        else
        {
            sb.AppendLine("*Geschlossen:* 0");
        }

        // Offene Positionen
        var openPositions = await db.Positions.CountAsync(ct);
        sb.AppendLine($"*Offene Positionen:* {openPositions}");

        // Setup-Typen heute
        var setupGroups = closedTrades
            .Where(t => t.SetupType != null)
            .GroupBy(t => t.SetupType!)
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();
        if (setupGroups.Count > 0)
            sb.AppendLine($"*Setups:* {EscapeMarkdownV2(string.Join(", ", setupGroups))}");

        await _notification.SendRawMessageAsync(sb.ToString(), "MarkdownV2");
        _logger.LogInformation("Taeglicher Report gesendet");
    }

    private async Task SendWeeklyReportAsync(DateTime date, CancellationToken ct)
    {
        _logger.LogInformation("Generiere woechentlichen Report fuer Woche bis {Date:dd.MM.yyyy}", date);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var weekStart = date.AddDays(-7).Date;
        var weekEnd = date.Date;

        var trades = await db.Trades
            .Where(t => t.CreatedAt >= weekStart && t.CreatedAt < weekEnd && t.Status == TradeStatus.Executed)
            .ToListAsync(ct);

        var closedTrades = await db.Trades
            .Where(t => t.ClosedAt >= weekStart && t.ClosedAt < weekEnd && t.RealizedPnL != null)
            .ToListAsync(ct);

        // Vorwoche zum Vergleich
        var prevWeekStart = weekStart.AddDays(-7);
        var prevClosedTrades = await db.Trades
            .Where(t => t.ClosedAt >= prevWeekStart && t.ClosedAt < weekStart && t.RealizedPnL != null)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"📈 *Wochen\\-Report {weekStart:dd\\.MM} – {weekEnd:dd\\.MM\\.yyyy}*");
        sb.AppendLine();

        sb.AppendLine($"*Trades:* {trades.Count}");

        if (closedTrades.Count > 0)
        {
            var totalPnL = closedTrades.Sum(t => t.RealizedPnL ?? 0);
            var wins = closedTrades.Count(t => t.RealizedPnL > 0);
            var winRate = (double)wins / closedTrades.Count * 100;

            sb.AppendLine($"*Geschlossen:* {closedTrades.Count} \\(Win\\-Rate: {winRate:F0}%\\)");
            sb.AppendLine($"*Wochen\\-PnL:* {(totalPnL >= 0 ? "\\+" : "")}${totalPnL:F2}");

            // Vergleich zur Vorwoche
            if (prevClosedTrades.Count > 0)
            {
                var prevPnL = prevClosedTrades.Sum(t => t.RealizedPnL ?? 0);
                var diff = totalPnL - prevPnL;
                sb.AppendLine($"*vs\\. Vorwoche:* {(diff >= 0 ? "\\+" : "")}${diff:F2}");
            }

            // Top-Symbole
            var symbolStats = closedTrades
                .GroupBy(t => t.Symbol)
                .Select(g => new { Symbol = g.Key, PnL = g.Sum(t => t.RealizedPnL ?? 0) })
                .OrderByDescending(s => s.PnL)
                .Take(3)
                .ToList();

            if (symbolStats.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("*Top Symbole:*");
                foreach (var s in symbolStats)
                {
                    sb.AppendLine($"  {EscapeMarkdownV2(s.Symbol)}: {(s.PnL >= 0 ? "\\+" : "")}${s.PnL:F2}");
                }
            }

            // Setup-Typen
            var setupStats = closedTrades
                .Where(t => t.SetupType != null)
                .GroupBy(t => t.SetupType!)
                .Select(g => new { Setup = g.Key, Count = g.Count(), PnL = g.Sum(t => t.RealizedPnL ?? 0) })
                .OrderByDescending(s => s.Count)
                .Take(5)
                .ToList();

            if (setupStats.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("*Setup\\-Typen:*");
                foreach (var s in setupStats)
                {
                    sb.AppendLine($"  {EscapeMarkdownV2(s.Setup)}: {s.Count}x, {(s.PnL >= 0 ? "\\+" : "")}${s.PnL:F2}");
                }
            }
        }
        else
        {
            sb.AppendLine("*Geschlossen:* 0");
        }

        // Drawdown-Status
        var pnlHistory = await db.DailyPnLs
            .Where(d => d.Date >= DateOnly.FromDateTime(weekStart))
            .OrderBy(d => d.Date)
            .ToListAsync(ct);

        if (pnlHistory.Count >= 2)
        {
            var peak = pnlHistory.Max(d => d.PortfolioValue);
            var current = pnlHistory.Last().PortfolioValue;
            if (peak > 0)
            {
                var ddPercent = (double)((peak - current) / peak) * 100;
                if (ddPercent > 1)
                    sb.AppendLine($"\n*Drawdown vom Peak:* {ddPercent:F1}%");
            }
        }

        await _notification.SendRawMessageAsync(sb.ToString(), "MarkdownV2");
        _logger.LogInformation("Woechentlicher Report gesendet");
    }

    private static string EscapeMarkdownV2(string text)
    {
        var chars = new[] { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (Array.IndexOf(chars, c) >= 0)
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>Report-Einstellungen.</summary>
public class ReportSettings
{
    public bool Enabled { get; set; }
    public bool DailyEnabled { get; set; } = true;
    public bool WeeklyEnabled { get; set; } = true;
    /// <summary>Uhrzeit (UTC) fuer Report-Versand, z.B. "22:00".</summary>
    public TimeSpan ReportTimeUtc { get; set; } = TimeSpan.FromHours(22);
}
