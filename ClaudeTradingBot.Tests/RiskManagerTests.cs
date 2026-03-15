using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using ClaudeTradingBot.Services;
using ClaudeTradingBot.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClaudeTradingBot.Tests;

public class RiskManagerTests
{
    private readonly Mock<IBrokerService> _broker = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly ILogger<RiskManager> _logger = NullLogger<RiskManager>.Instance;

    private RiskManager CreateRiskManager(RiskSettings settings, string dbName)
    {
        var factory = new TestDbContextFactory(dbName);
        SetupScopeFactory(factory);

        var monitor = Mock.Of<IOptionsMonitor<RiskSettings>>(m => m.CurrentValue == settings);
        var notification = new NotificationService(
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<IOptionsMonitor<TelegramSettings>>(m => m.CurrentValue == new TelegramSettings()),
            NullLogger<NotificationService>.Instance);

        return new RiskManager(monitor, _broker.Object, notification, _scopeFactory.Object, _logger);
    }

    private void SetupScopeFactory(TestDbContextFactory factory)
    {
        _scopeFactory.Setup(x => x.CreateScope()).Returns(() =>
        {
            var scope = new Mock<IServiceScope>();
            var sp = new Mock<IServiceProvider>();
            sp.Setup(x => x.GetService(typeof(TradingDbContext))).Returns(factory.CreateDbContext());
            scope.Setup(x => x.ServiceProvider).Returns(sp.Object);
            return scope.Object;
        });
    }

    private static RiskSettings DefaultSettings() => new()
    {
        MinConfidence = 0.65,
        MaxPositionSizePercent = 10.0,
        MaxOpenPositions = 5,
        MaxDailyLossPercent = 3.0,
        MaxSpreadPips = 0,
        MaxCorrelatedExposurePercent = 0,
        MaxDrawdownPercent = 0,
        MaxWeeklyLossPercent = 0,
        MaxMonthlyLossPercent = 0,
        DynamicConfidenceEnabled = false
    };

    private static ClaudeTradeRecommendation BuyRec(string symbol = "EURUSD", double confidence = 0.8, decimal qty = 0.1m)
        => new() { Symbol = symbol, Action = "buy", Confidence = confidence, Quantity = qty };

    // ── Kill Switch ─────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateTrade_KillSwitchActive_ReturnsFalse()
    {
        var rm = CreateRiskManager(DefaultSettings(), nameof(ValidateTrade_KillSwitchActive_ReturnsFalse));
        rm.ActivateKillSwitch("test reason");

        var result = await rm.ValidateTradeAsync(BuyRec());

        result.Should().BeFalse();
        rm.IsKillSwitchActive.Should().BeTrue();
    }

    [Fact]
    public void ActivateKillSwitch_SetsActiveAndReason()
    {
        var rm = CreateRiskManager(DefaultSettings(), nameof(ActivateKillSwitch_SetsActiveAndReason));

        rm.ActivateKillSwitch("max loss exceeded");

        rm.IsKillSwitchActive.Should().BeTrue();
    }

    [Fact]
    public void ResetKillSwitch_ClearsActiveState()
    {
        var rm = CreateRiskManager(DefaultSettings(), nameof(ResetKillSwitch_ClearsActiveState));
        rm.ActivateKillSwitch("test");

        rm.ResetKillSwitch();

        rm.IsKillSwitchActive.Should().BeFalse();
    }

    // ── Hold ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateTrade_HoldAction_ReturnsTrue()
    {
        var rm = CreateRiskManager(DefaultSettings(), nameof(ValidateTrade_HoldAction_ReturnsTrue));

        var rec = new ClaudeTradeRecommendation { Action = "hold", Symbol = "EURUSD", Confidence = 0.3 };
        var result = await rm.ValidateTradeAsync(rec);

        result.Should().BeTrue();
    }

    // ── Confidence ──────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateTrade_ConfidenceBelowMin_ReturnsFalse()
    {
        var rm = CreateRiskManager(DefaultSettings(), nameof(ValidateTrade_ConfidenceBelowMin_ReturnsFalse));

        var result = await rm.ValidateTradeAsync(BuyRec(confidence: 0.50));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateTrade_ConfidenceAboveMin_Passes()
    {
        var settings = DefaultSettings();
        var rm = CreateRiskManager(settings, nameof(ValidateTrade_ConfidenceAboveMin_Passes));

        _broker.Setup(b => b.GetPortfolioValueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100000m);
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.1m);
        _broker.Setup(b => b.GetPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Position>());

        var result = await rm.ValidateTradeAsync(BuyRec(confidence: 0.70, qty: 0.01m));

        result.Should().BeTrue();
    }

    // ── Spread Filter ───────────────────────────────────────────────────

    [Fact]
    public async Task ValidateTrade_SpreadExceedsMax_ReturnsFalse()
    {
        var settings = DefaultSettings();
        settings.MaxSpreadPips = 3;
        var rm = CreateRiskManager(settings, nameof(ValidateTrade_SpreadExceedsMax_ReturnsFalse));

        // 5 pips spread for EURUSD: 0.0005
        var result = await rm.ValidateTradeAsync(BuyRec(), bid: 1.1000m, ask: 1.1005m);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateTrade_SpreadBelowMax_Passes()
    {
        var settings = DefaultSettings();
        settings.MaxSpreadPips = 3;
        var rm = CreateRiskManager(settings, nameof(ValidateTrade_SpreadBelowMax_Passes));

        _broker.Setup(b => b.GetPortfolioValueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100000m);
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.1m);
        _broker.Setup(b => b.GetPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Position>());

        // 1 pip spread: 0.0001
        var result = await rm.ValidateTradeAsync(BuyRec(qty: 0.01m), bid: 1.1000m, ask: 1.1001m);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateTrade_SpreadFilterDisabled_Passes()
    {
        var settings = DefaultSettings();
        settings.MaxSpreadPips = 0; // disabled
        var rm = CreateRiskManager(settings, nameof(ValidateTrade_SpreadFilterDisabled_Passes));

        _broker.Setup(b => b.GetPortfolioValueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100000m);
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.1m);
        _broker.Setup(b => b.GetPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Position>());

        // Even with huge spread, filter is disabled
        var result = await rm.ValidateTradeAsync(BuyRec(qty: 0.01m), bid: 1.1000m, ask: 1.1050m);

        result.Should().BeTrue();
    }

    // ── Position Size ───────────────────────────────────────────────────

    [Fact]
    public async Task ValidateTrade_PositionSizeExceedsMax_ReturnsFalse()
    {
        var settings = DefaultSettings();
        settings.MaxPositionSizePercent = 5.0;
        var rm = CreateRiskManager(settings, nameof(ValidateTrade_PositionSizeExceedsMax_ReturnsFalse));

        _broker.Setup(b => b.GetPortfolioValueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(10000m);
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.1m);
        _broker.Setup(b => b.GetPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Position>());

        // 1.0 Lot EURUSD = 100.000 * 1.1 = 110.000 → 1100% of 10k portfolio
        var result = await rm.ValidateTradeAsync(BuyRec(qty: 1.0m));

        result.Should().BeFalse();
    }

    // ── Max Open Positions ──────────────────────────────────────────────

    [Fact]
    public async Task ValidateTrade_MaxPositionsReached_ReturnsFalse()
    {
        var settings = DefaultSettings();
        settings.MaxOpenPositions = 2;
        var rm = CreateRiskManager(settings, nameof(ValidateTrade_MaxPositionsReached_ReturnsFalse));

        _broker.Setup(b => b.GetPortfolioValueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100000m);
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.1m);
        _broker.Setup(b => b.GetPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Position>
        {
            new() { Symbol = "GBPUSD", Quantity = 0.1m, Side = "buy" },
            new() { Symbol = "AUDUSD", Quantity = 0.1m, Side = "buy" }
        });

        // New symbol, max already reached
        var result = await rm.ValidateTradeAsync(BuyRec(symbol: "EURUSD"));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateTrade_MaxPositionsReached_SameSymbol_Passes()
    {
        var settings = DefaultSettings();
        settings.MaxOpenPositions = 2;
        var rm = CreateRiskManager(settings, nameof(ValidateTrade_MaxPositionsReached_SameSymbol_Passes));

        _broker.Setup(b => b.GetPortfolioValueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100000m);
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.1m);
        _broker.Setup(b => b.GetPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Position>
        {
            new() { Symbol = "EURUSD", Quantity = 0.1m, Side = "buy" },
            new() { Symbol = "GBPUSD", Quantity = 0.1m, Side = "buy" }
        });

        // Same symbol already open -> allowed (small qty to pass position size check)
        var result = await rm.ValidateTradeAsync(BuyRec(symbol: "EURUSD", qty: 0.01m));

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateTrade_BuyLimit_CorrectlyNormalized()
    {
        var settings = DefaultSettings();
        settings.MaxOpenPositions = 1;
        var rm = CreateRiskManager(settings, nameof(ValidateTrade_BuyLimit_CorrectlyNormalized));

        _broker.Setup(b => b.GetPortfolioValueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100000m);
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.1m);
        _broker.Setup(b => b.GetPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Position>
        {
            new() { Symbol = "GBPUSD", Quantity = 0.1m, Side = "buy" }
        });

        // buy_limit should be normalized to "buy" for position check
        var rec = new ClaudeTradeRecommendation
        {
            Symbol = "EURUSD", Action = "buy_limit", Confidence = 0.8, Quantity = 0.01m
        };
        var result = await rm.ValidateTradeAsync(rec);

        result.Should().BeFalse();
    }

    // ── Sell bypasses max positions ─────────────────────────────────────

    [Fact]
    public async Task ValidateTrade_SellAction_BypassesMaxPositions()
    {
        var settings = DefaultSettings();
        settings.MaxOpenPositions = 1;
        var rm = CreateRiskManager(settings, nameof(ValidateTrade_SellAction_BypassesMaxPositions));

        _broker.Setup(b => b.GetPortfolioValueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100000m);
        _broker.Setup(b => b.GetCurrentPriceAsync("EURUSD", It.IsAny<CancellationToken>())).ReturnsAsync(1.1m);
        _broker.Setup(b => b.GetPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Position>
        {
            new() { Symbol = "GBPUSD", Quantity = 0.1m, Side = "buy" }
        });

        // Sell should not be blocked by max positions
        var rec = new ClaudeTradeRecommendation
        {
            Symbol = "EURUSD", Action = "sell", Confidence = 0.8, Quantity = 0.01m
        };
        var result = await rm.ValidateTradeAsync(rec);

        result.Should().BeTrue();
    }
}
