using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using ClaudeTradingBot.Services;
using ClaudeTradingBot.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeTradingBot.Tests;

public class GridTradingTests
{
    private readonly Mock<IBrokerService> _broker = new();
    private readonly ILogger<GridTradingService> _logger = NullLogger<GridTradingService>.Instance;

    private (GridTradingService Svc, TestDbContextFactory Factory) CreateService(string dbName)
    {
        var factory = new TestDbContextFactory(dbName);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(x => x.CreateScope()).Returns(() =>
        {
            var scope = new Mock<IServiceScope>();
            var sp = new Mock<IServiceProvider>();
            sp.Setup(x => x.GetService(typeof(TradingDbContext))).Returns(factory.CreateDbContext());
            scope.Setup(x => x.ServiceProvider).Returns(sp.Object);
            return scope.Object;
        });

        var svc = new GridTradingService(_broker.Object, scopeFactory.Object, _logger);
        return (svc, factory);
    }

    private static GridSettings DefaultGridSettings() => new()
    {
        Enabled = true,
        GridSpacingPips = 20,
        GridLevelsAbove = 5,
        GridLevelsBelow = 5,
        LotSizePerLevel = 0.01m,
        MaxActiveGrids = 3,
        MaxLevelsPerCycle = 2
    };

    // ── Grid Initialization ─────────────────────────────────────────────

    [Fact]
    public async Task InitializeGrid_CreatesCorrectLevelCount()
    {
        var (svc, factory) = CreateService(nameof(InitializeGrid_CreatesCorrectLevelCount));
        var settings = DefaultGridSettings();

        var grid = await svc.InitializeGridAsync("EURUSD", 1.1000m, settings, CancellationToken.None);

        var levels = grid.GetLevels();
        levels.Should().HaveCount(10); // 5 buy + 5 sell
    }

    [Fact]
    public async Task InitializeGrid_BuyLevelsBelowCenter()
    {
        var (svc, _) = CreateService(nameof(InitializeGrid_BuyLevelsBelowCenter));
        var settings = DefaultGridSettings();

        var grid = await svc.InitializeGridAsync("EURUSD", 1.1000m, settings, CancellationToken.None);

        var levels = grid.GetLevels();
        var buyLevels = levels.Where(l => l.Side == "buy").ToList();

        buyLevels.Should().HaveCount(5);
        buyLevels.Should().OnlyContain(l => l.Price < 1.1000m);
        buyLevels.Should().OnlyContain(l => l.Index < 0);
    }

    [Fact]
    public async Task InitializeGrid_SellLevelsAboveCenter()
    {
        var (svc, _) = CreateService(nameof(InitializeGrid_SellLevelsAboveCenter));
        var settings = DefaultGridSettings();

        var grid = await svc.InitializeGridAsync("EURUSD", 1.1000m, settings, CancellationToken.None);

        var levels = grid.GetLevels();
        var sellLevels = levels.Where(l => l.Side == "sell").ToList();

        sellLevels.Should().HaveCount(5);
        sellLevels.Should().OnlyContain(l => l.Price > 1.1000m);
        sellLevels.Should().OnlyContain(l => l.Index > 0);
    }

    [Fact]
    public async Task InitializeGrid_EurUsd_CorrectSpacing()
    {
        var (svc, _) = CreateService(nameof(InitializeGrid_EurUsd_CorrectSpacing));
        var settings = DefaultGridSettings();
        settings.GridSpacingPips = 20; // 20 pips = 0.0020 for EURUSD

        var grid = await svc.InitializeGridAsync("EURUSD", 1.1000m, settings, CancellationToken.None);

        var levels = grid.GetLevels();
        var buyLevels = levels.Where(l => l.Side == "buy").OrderByDescending(l => l.Price).ToList();

        // First buy level should be 20 pips below center
        buyLevels[0].Price.Should().Be(1.0980m);
        // Second buy level should be 40 pips below center
        buyLevels[1].Price.Should().Be(1.0960m);
    }

    [Fact]
    public async Task InitializeGrid_Gold_CorrectSpacing()
    {
        var (svc, _) = CreateService(nameof(InitializeGrid_Gold_CorrectSpacing));
        var settings = DefaultGridSettings();
        settings.GridSpacingPips = 20; // 20 pips = 2.0 for XAUUSD (pip = 0.1)

        var grid = await svc.InitializeGridAsync("XAUUSD", 2000.0m, settings, CancellationToken.None);

        var levels = grid.GetLevels();
        var sellLevels = levels.Where(l => l.Side == "sell").OrderBy(l => l.Price).ToList();

        sellLevels[0].Price.Should().Be(2002.0m);
        sellLevels[1].Price.Should().Be(2004.0m);
    }

    [Fact]
    public async Task InitializeGrid_PersistsToDatabase()
    {
        var (svc, factory) = CreateService(nameof(InitializeGrid_PersistsToDatabase));

        await svc.InitializeGridAsync("EURUSD", 1.1000m, DefaultGridSettings(), CancellationToken.None);

        using var db = factory.CreateDbContext();
        db.GridStates.Should().HaveCount(1);
        db.GridStates.First().Symbol.Should().Be("EURUSD");
    }

    // ── GridState JSON Roundtrip ────────────────────────────────────────

    [Fact]
    public void GridState_SetLevels_GetLevels_Roundtrip()
    {
        var grid = new GridState();
        var levels = new List<GridLevel>
        {
            new() { Index = -1, Price = 1.0980m, Side = "buy", Status = GridLevelStatus.Pending },
            new() { Index = 1, Price = 1.1020m, Side = "sell", Status = GridLevelStatus.Filled }
        };

        grid.SetLevels(levels);
        var result = grid.GetLevels();

        result.Should().HaveCount(2);
        result[0].Index.Should().Be(-1);
        result[0].Price.Should().Be(1.0980m);
        result[1].Status.Should().Be(GridLevelStatus.Filled);
    }

    [Fact]
    public void GridState_GetLevels_InvalidJson_ReturnsEmptyList()
    {
        var grid = new GridState { LevelsJson = "invalid json{{{" };

        var result = grid.GetLevels();

        result.Should().BeEmpty();
    }

    // ── HandleGridRecommendation ────────────────────────────────────────

    [Fact]
    public async Task HandleGridRecommendation_MaxGridsReached_DoesNotCreate()
    {
        var (svc, factory) = CreateService(nameof(HandleGridRecommendation_MaxGridsReached_DoesNotCreate));
        var settings = DefaultGridSettings();
        settings.MaxActiveGrids = 2;

        // Create 2 active grids
        await svc.InitializeGridAsync("EURUSD", 1.1m, settings, CancellationToken.None);
        await svc.InitializeGridAsync("GBPUSD", 1.3m, settings, CancellationToken.None);

        // Try to create a 3rd
        var rec = new ClaudeTradeRecommendation { Symbol = "AUDUSD", Action = "grid" };
        await svc.HandleGridRecommendationAsync("AUDUSD", 0.7m, rec, settings, CancellationToken.None);

        using var db = factory.CreateDbContext();
        db.GridStates.Count(g => g.Status == GridStatus.Active).Should().Be(2);
    }

    [Fact]
    public async Task HandleGridRecommendation_ExistingGrid_ManagesExisting()
    {
        var (svc, factory) = CreateService(nameof(HandleGridRecommendation_ExistingGrid_ManagesExisting));
        var settings = DefaultGridSettings();

        // Create initial grid
        await svc.InitializeGridAsync("EURUSD", 1.1m, settings, CancellationToken.None);

        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.1m);

        // Same symbol -> should manage, not create new
        var rec = new ClaudeTradeRecommendation { Symbol = "EURUSD", Action = "grid" };
        await svc.HandleGridRecommendationAsync("EURUSD", 1.1m, rec, settings, CancellationToken.None);

        using var db = factory.CreateDbContext();
        db.GridStates.Count(g => g.Symbol == "EURUSD" && g.Status == GridStatus.Active).Should().Be(1);
    }

    // ── ManageGrid – Level Triggering ───────────────────────────────────

    [Fact]
    public async Task ManageGrid_PriceBelowBuyLevel_TriggersOrder()
    {
        var (svc, factory) = CreateService(nameof(ManageGrid_PriceBelowBuyLevel_TriggersOrder));
        var settings = DefaultGridSettings();

        await svc.InitializeGridAsync("EURUSD", 1.1000m, settings, CancellationToken.None);

        // Price dropped below first buy level (1.0980)
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.0975m);
        _broker.Setup(b => b.PlaceOrderAsync(
            "EURUSD", TradeAction.Buy, 0.01m, null, null,
            It.IsAny<OrderType>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaceOrderResult { Success = true, BrokerPositionId = "pos1" });

        await svc.ManageGridAsync("EURUSD", settings, CancellationToken.None);

        _broker.Verify(b => b.PlaceOrderAsync(
            "EURUSD", TradeAction.Buy, 0.01m, null, null,
            It.IsAny<OrderType>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ManageGrid_PriceBetweenLevels_NoTrigger()
    {
        var (svc, _) = CreateService(nameof(ManageGrid_PriceBetweenLevels_NoTrigger));
        var settings = DefaultGridSettings();

        await svc.InitializeGridAsync("EURUSD", 1.1000m, settings, CancellationToken.None);

        // Price at center -> no levels triggered
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.1000m);

        await svc.ManageGridAsync("EURUSD", settings, CancellationToken.None);

        _broker.Verify(b => b.PlaceOrderAsync(
            It.IsAny<string>(), It.IsAny<TradeAction>(), It.IsAny<decimal>(),
            It.IsAny<decimal?>(), It.IsAny<decimal?>(),
            It.IsAny<OrderType>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ManageGrid_MaxLevelsPerCycle_Enforced()
    {
        var (svc, _) = CreateService(nameof(ManageGrid_MaxLevelsPerCycle_Enforced));
        var settings = DefaultGridSettings();
        settings.MaxLevelsPerCycle = 2;

        await svc.InitializeGridAsync("EURUSD", 1.1000m, settings, CancellationToken.None);

        // Price dropped below ALL buy levels
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.0800m);
        _broker.Setup(b => b.PlaceOrderAsync(
            It.IsAny<string>(), It.IsAny<TradeAction>(), It.IsAny<decimal>(),
            It.IsAny<decimal?>(), It.IsAny<decimal?>(),
            It.IsAny<OrderType>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaceOrderResult { Success = true, BrokerPositionId = "pos" });

        await svc.ManageGridAsync("EURUSD", settings, CancellationToken.None);

        // Only 2 orders placed despite 5 levels triggered
        _broker.Verify(b => b.PlaceOrderAsync(
            It.IsAny<string>(), It.IsAny<TradeAction>(), It.IsAny<decimal>(),
            It.IsAny<decimal?>(), It.IsAny<decimal?>(),
            It.IsAny<OrderType>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
