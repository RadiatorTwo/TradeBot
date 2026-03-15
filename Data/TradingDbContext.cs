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
    public DbSet<AccountSettingsEntity> AccountSettings => Set<AccountSettingsEntity>();
    public DbSet<GlobalSettingsEntity> GlobalSettings => Set<GlobalSettingsEntity>();
    public DbSet<EngineStateSnapshot> EngineStateSnapshots => Set<EngineStateSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Trade>(e =>
        {
            e.HasIndex(t => t.Symbol);
            e.HasIndex(t => t.CreatedAt);
            e.HasIndex(t => t.Status);
            e.HasIndex(t => t.AccountId);
            e.HasIndex(t => t.SetupType);
        });

        modelBuilder.Entity<Position>(e =>
        {
            e.HasIndex(p => p.Symbol);
            e.HasIndex(p => p.BrokerPositionId).IsUnique();
        });

        modelBuilder.Entity<DailyPnL>(e =>
        {
            e.HasIndex(d => new { d.Date, d.AccountId }).IsUnique();
        });

        modelBuilder.Entity<TradingLog>(e =>
        {
            e.HasIndex(l => l.Timestamp);
            e.HasIndex(l => l.AccountId);
        });

        modelBuilder.Entity<EngineStateSnapshot>(e =>
        {
            e.HasIndex(s => s.AccountId);
            e.HasIndex(s => s.ShutdownAt);
        });
    }
}
