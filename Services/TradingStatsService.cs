using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClaudeTradingBot.Services;

/// <summary>Berechnet Performance-Kennzahlen aus Trades und PnL-History.</summary>
public static class TradingStatsService
{
    /// <summary>Berechnet Stats direkt per DB-Aggregation (vermeidet ToListAsync auf alle Trades).</summary>
    public static async Task<TradingStatsViewModel> CalculateFromDbAsync(
        TradingDbContext db, List<DailyPnL> pnlHistory, CancellationToken ct = default)
    {
        var totalCount = await db.Trades.CountAsync(ct);
        if (totalCount == 0)
            return new TradingStatsViewModel();

        var executed = await db.Trades.CountAsync(t => t.Status == TradeStatus.Executed, ct);
        var rejected = await db.Trades.CountAsync(t => t.Status == TradeStatus.Rejected, ct);
        var failed = await db.Trades.CountAsync(t => t.Status == TradeStatus.Failed, ct);
        var avgConfidence = await db.Trades.AverageAsync(t => t.ClaudeConfidence, ct);

        // PnL-basierte Stats via DB
        var closedCount = await db.Trades
            .CountAsync(t => t.Status == TradeStatus.Executed && t.RealizedPnL != null, ct);

        var winCount = await db.Trades
            .CountAsync(t => t.Status == TradeStatus.Executed && t.RealizedPnL > 0, ct);

        var lossCount = await db.Trades
            .CountAsync(t => t.Status == TradeStatus.Executed && t.RealizedPnL < 0, ct);

        var totalProfit = closedCount > 0
            ? await db.Trades
                .Where(t => t.Status == TradeStatus.Executed && t.RealizedPnL > 0)
                .SumAsync(t => t.RealizedPnL!.Value, ct)
            : 0m;

        var totalLossRaw = closedCount > 0
            ? await db.Trades
                .Where(t => t.Status == TradeStatus.Executed && t.RealizedPnL < 0)
                .SumAsync(t => t.RealizedPnL!.Value, ct)
            : 0m;
        var totalLoss = Math.Abs(totalLossRaw);

        var avgWin = winCount > 0
            ? await db.Trades
                .Where(t => t.Status == TradeStatus.Executed && t.RealizedPnL > 0)
                .AverageAsync(t => t.RealizedPnL!.Value, ct)
            : 0m;

        var avgLoss = lossCount > 0
            ? Math.Abs(await db.Trades
                .Where(t => t.Status == TradeStatus.Executed && t.RealizedPnL < 0)
                .AverageAsync(t => t.RealizedPnL!.Value, ct))
            : 0m;

        var winRate = closedCount > 0 ? (double)winCount / closedCount * 100 : 0;
        var netPnL = totalProfit - totalLoss;
        var profitFactor = totalLoss > 0 ? totalProfit / totalLoss : totalProfit > 0 ? 999m : 0;

        // Trades pro Tag
        var tradesPerDay = 0.0;
        if (executed >= 2)
        {
            var firstDate = await db.Trades
                .Where(t => t.Status == TradeStatus.Executed)
                .MinAsync(t => t.CreatedAt, ct);
            var lastDate = await db.Trades
                .Where(t => t.Status == TradeStatus.Executed)
                .MaxAsync(t => t.CreatedAt, ct);
            var days = (lastDate - firstDate).TotalDays;
            tradesPerDay = days > 0 ? executed / days : executed;
        }

        var (maxDrawdown, maxDrawdownPercent) = CalculateMaxDrawdown(pnlHistory);
        var sharpeRatio = CalculateSharpeRatio(pnlHistory);

        return new TradingStatsViewModel
        {
            TotalExecuted = executed,
            TotalRejected = rejected,
            TotalFailed = failed,
            AvgConfidence = Math.Round(avgConfidence * 100, 1),
            HasPnLData = closedCount > 0,
            ClosedTrades = closedCount,
            WinningTrades = winCount,
            LosingTrades = lossCount,
            WinRate = Math.Round(winRate, 1),
            AvgWin = Math.Round(avgWin, 2),
            AvgLoss = Math.Round(avgLoss, 2),
            ProfitFactor = Math.Round(profitFactor, 2),
            TotalProfit = Math.Round(totalProfit, 2),
            TotalLoss = Math.Round(totalLoss, 2),
            NetPnL = Math.Round(netPnL, 2),
            MaxDrawdown = Math.Round(maxDrawdown, 2),
            MaxDrawdownPercent = Math.Round(maxDrawdownPercent, 2),
            SharpeRatio = Math.Round(sharpeRatio, 2),
            TradesPerDay = Math.Round(tradesPerDay, 1)
        };
    }

    /// <summary>Legacy: Berechnet Stats aus einer vollstaendigen Trade-Liste (fuer Backtesting).</summary>
    public static TradingStatsViewModel Calculate(List<Trade> allTrades, List<DailyPnL> pnlHistory)
    {
        if (allTrades.Count == 0)
            return new TradingStatsViewModel();

        var executed = allTrades.Where(t => t.Status == TradeStatus.Executed).ToList();
        var rejected = allTrades.Count(t => t.Status == TradeStatus.Rejected);
        var failed = allTrades.Count(t => t.Status == TradeStatus.Failed);
        var avgConfidence = allTrades.Average(t => t.ClaudeConfidence);

        var closedWithPnl = executed
            .Where(t => t.RealizedPnL.HasValue)
            .ToList();

        var hasPnlData = closedWithPnl.Count > 0;

        var wins = closedWithPnl.Where(t => t.RealizedPnL!.Value > 0).ToList();
        var losses = closedWithPnl.Where(t => t.RealizedPnL!.Value < 0).ToList();

        var totalProfit = wins.Sum(t => t.RealizedPnL!.Value);
        var totalLoss = Math.Abs(losses.Sum(t => t.RealizedPnL!.Value));
        var netPnL = totalProfit - totalLoss;

        var winRate = closedWithPnl.Count > 0
            ? (double)wins.Count / closedWithPnl.Count * 100
            : 0;

        var avgWin = wins.Count > 0 ? wins.Average(t => t.RealizedPnL!.Value) : 0;
        var avgLoss = losses.Count > 0 ? Math.Abs(losses.Average(t => t.RealizedPnL!.Value)) : 0;

        var profitFactor = totalLoss > 0 ? totalProfit / totalLoss : totalProfit > 0 ? 999m : 0;

        var (maxDrawdown, maxDrawdownPercent) = CalculateMaxDrawdown(pnlHistory);
        var sharpeRatio = CalculateSharpeRatio(pnlHistory);

        var tradesPerDay = 0.0;
        if (executed.Count >= 2)
        {
            var firstDate = executed.Min(t => t.CreatedAt);
            var lastDate = executed.Max(t => t.CreatedAt);
            var days = (lastDate - firstDate).TotalDays;
            tradesPerDay = days > 0 ? executed.Count / days : executed.Count;
        }

        return new TradingStatsViewModel
        {
            TotalExecuted = executed.Count,
            TotalRejected = rejected,
            TotalFailed = failed,
            AvgConfidence = Math.Round(avgConfidence * 100, 1),
            HasPnLData = hasPnlData,
            ClosedTrades = closedWithPnl.Count,
            WinningTrades = wins.Count,
            LosingTrades = losses.Count,
            WinRate = Math.Round(winRate, 1),
            AvgWin = Math.Round(avgWin, 2),
            AvgLoss = Math.Round(avgLoss, 2),
            ProfitFactor = Math.Round(profitFactor, 2),
            TotalProfit = Math.Round(totalProfit, 2),
            TotalLoss = Math.Round(totalLoss, 2),
            NetPnL = Math.Round(netPnL, 2),
            MaxDrawdown = Math.Round(maxDrawdown, 2),
            MaxDrawdownPercent = Math.Round(maxDrawdownPercent, 2),
            SharpeRatio = Math.Round(sharpeRatio, 2),
            TradesPerDay = Math.Round(tradesPerDay, 1)
        };
    }

    /// <summary>Performance-Kennzahlen pro SetupType (Phase 8.2). Client-seitige Aggregation wegen SQLite decimal-Limitierung.</summary>
    public static async Task<List<SetupTypeStatsViewModel>> CalculatePerSetupTypeAsync(
        TradingDbContext db, CancellationToken ct = default)
    {
        var trades = await db.Trades
            .Where(t => t.SetupType != null && t.Status == TradeStatus.Executed && t.RealizedPnL != null)
            .Select(t => new { t.SetupType, PnL = t.RealizedPnL!.Value })
            .ToListAsync(ct);

        return trades
            .GroupBy(t => t.SetupType!)
            .Select(g =>
            {
                var totalPnL = g.Sum(t => t.PnL);
                var totalProfit = g.Where(t => t.PnL > 0).Sum(t => t.PnL);
                var absLoss = Math.Abs(g.Where(t => t.PnL < 0).Sum(t => t.PnL));
                var winCount = g.Count(t => t.PnL > 0);
                var count = g.Count();
                return new SetupTypeStatsViewModel
                {
                    SetupType = g.Key,
                    TradeCount = count,
                    WinCount = winCount,
                    WinRate = count > 0 ? Math.Round((double)winCount / count * 100, 1) : 0,
                    TotalPnL = Math.Round(totalPnL, 2),
                    AvgPnL = count > 0 ? Math.Round(totalPnL / count, 2) : 0,
                    ProfitFactor = absLoss > 0 ? Math.Round(totalProfit / absLoss, 2) : totalProfit > 0 ? 999m : 0m
                };
            })
            .OrderByDescending(s => s.TradeCount)
            .ToList();
    }

    private static (decimal MaxDrawdown, double MaxDrawdownPercent) CalculateMaxDrawdown(List<DailyPnL> pnlHistory)
    {
        if (pnlHistory.Count < 2)
            return (0, 0);

        var sorted = pnlHistory.OrderBy(p => p.Date).ToList();
        decimal peak = 0;
        decimal maxDrawdown = 0;
        double maxDrawdownPercent = 0;

        foreach (var day in sorted)
        {
            if (day.PortfolioValue > peak)
                peak = day.PortfolioValue;

            if (peak > 0)
            {
                var drawdown = peak - day.PortfolioValue;
                var drawdownPercent = (double)(drawdown / peak) * 100;

                if (drawdown > maxDrawdown)
                {
                    maxDrawdown = drawdown;
                    maxDrawdownPercent = drawdownPercent;
                }
            }
        }

        return (maxDrawdown, maxDrawdownPercent);
    }

    private static double CalculateSharpeRatio(List<DailyPnL> pnlHistory)
    {
        if (pnlHistory.Count < 3)
            return 0;

        var sorted = pnlHistory.OrderBy(p => p.Date).ToList();

        var returns = new List<double>();
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i - 1].PortfolioValue > 0)
            {
                var dailyReturn = (double)((sorted[i].PortfolioValue - sorted[i - 1].PortfolioValue)
                    / sorted[i - 1].PortfolioValue);
                returns.Add(dailyReturn);
            }
        }

        if (returns.Count < 2)
            return 0;

        var avgReturn = returns.Average();
        var variance = returns.Sum(r => (r - avgReturn) * (r - avgReturn)) / (returns.Count - 1);
        var stdDev = Math.Sqrt(variance);

        if (stdDev == 0)
            return 0;

        return (avgReturn / stdDev) * Math.Sqrt(252);
    }
}
