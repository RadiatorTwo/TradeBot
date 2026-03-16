using ClaudeTradingBot.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeTradingBot.Tests.Helpers;

public class TestDbContextFactory : IDbContextFactory<TradingDbContext>
{
    private readonly DbContextOptions<TradingDbContext> _options;

    public TestDbContextFactory(string dbName)
    {
        _options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        // Ensure schema is created
        using var ctx = new TradingDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public TradingDbContext CreateDbContext()
        => new(_options);
}
