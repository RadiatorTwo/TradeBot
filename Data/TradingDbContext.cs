using Microsoft.EntityFrameworkCore;
using ClaudeTradingBot.Models;

namespace ClaudeTradingBot.Data;

public class TradingDbContext : DbContext
{
    public TradingDbContext(DbContextOptions<TradingDbContext> options) : base(options) { }

    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<DailyPnL> DailyPnLs => Set<DailyPnL>();
    public DbSet<TradingLog> TradingLogs => Set<TradingLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Trade>(e =>
        {
            e.HasIndex(t => t.Symbol);
            e.HasIndex(t => t.CreatedAt);
            e.HasIndex(t => t.Status);
        });

        modelBuilder.Entity<Position>(e =>
        {
            e.HasIndex(p => p.Symbol).IsUnique();
        });

        modelBuilder.Entity<DailyPnL>(e =>
        {
            e.HasIndex(d => d.Date).IsUnique();
        });

        modelBuilder.Entity<TradingLog>(e =>
        {
            e.HasIndex(l => l.Timestamp);
        });
    }
}
