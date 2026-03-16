using ClaudeTradingBot.Models;
using FluentAssertions;

namespace ClaudeTradingBot.Tests;

public class PipCalculatorTests
{
    // ── GetPipSize ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("EURUSD", 0.0001)]
    [InlineData("GBPUSD", 0.0001)]
    [InlineData("AUDUSD", 0.0001)]
    [InlineData("NZDUSD", 0.0001)]
    [InlineData("EURGBP", 0.0001)]
    public void GetPipSize_ForexStandard_Returns0001(string symbol, decimal expected)
    {
        PipCalculator.GetPipSize(symbol).Should().Be(expected);
    }

    [Theory]
    [InlineData("USDJPY")]
    [InlineData("EURJPY")]
    [InlineData("GBPJPY")]
    public void GetPipSize_JpyPairs_Returns001(string symbol)
    {
        PipCalculator.GetPipSize(symbol).Should().Be(0.01m);
    }

    [Fact]
    public void GetPipSize_Gold_Returns01()
    {
        PipCalculator.GetPipSize("XAUUSD").Should().Be(0.1m);
    }

    [Fact]
    public void GetPipSize_Silver_Returns001()
    {
        PipCalculator.GetPipSize("XAGUSD").Should().Be(0.01m);
    }

    [Theory]
    [InlineData("US100")]
    [InlineData("US500")]
    [InlineData("US30")]
    [InlineData("DE30")]
    public void GetPipSize_Indices_Returns1(string symbol)
    {
        PipCalculator.GetPipSize(symbol).Should().Be(1.0m);
    }

    [Theory]
    [InlineData("XTIUSD")]
    [InlineData("XBRUSD")]
    public void GetPipSize_Oil_Returns001(string symbol)
    {
        PipCalculator.GetPipSize(symbol).Should().Be(0.01m);
    }

    [Fact]
    public void GetPipSize_UnknownSymbol_ReturnsForexDefault()
    {
        PipCalculator.GetPipSize("ABCDEF").Should().Be(0.0001m);
    }

    // ── PriceToPips ─────────────────────────────────────────────────────

    [Fact]
    public void PriceToPips_EurUsd_10PipsMovement()
    {
        PipCalculator.PriceToPips("EURUSD", 0.0010m).Should().Be(10m);
    }

    [Fact]
    public void PriceToPips_UsdJpy_50PipsMovement()
    {
        PipCalculator.PriceToPips("USDJPY", 0.50m).Should().Be(50m);
    }

    [Fact]
    public void PriceToPips_Gold_100PipsMovement()
    {
        PipCalculator.PriceToPips("XAUUSD", 10.0m).Should().Be(100m);
    }

    [Fact]
    public void PriceToPips_NegativeDifference_ReturnsAbsoluteValue()
    {
        PipCalculator.PriceToPips("EURUSD", -0.0010m).Should().Be(10m);
    }

    // ── PipsToPrice ─────────────────────────────────────────────────────

    [Fact]
    public void PipsToPrice_EurUsd_10Pips()
    {
        PipCalculator.PipsToPrice("EURUSD", 10m).Should().Be(0.0010m);
    }

    [Fact]
    public void PipsToPrice_UsdJpy_50Pips()
    {
        PipCalculator.PipsToPrice("USDJPY", 50m).Should().Be(0.50m);
    }

    [Fact]
    public void PipsToPrice_Gold_100Pips()
    {
        PipCalculator.PipsToPrice("XAUUSD", 100m).Should().Be(10.0m);
    }

    // ── GetPipValuePerLot ───────────────────────────────────────────────

    [Fact]
    public void GetPipValuePerLot_XxxUsd_Returns10()
    {
        // EURUSD: 100.000 * 0.0001 = $10
        PipCalculator.GetPipValuePerLot("EURUSD", 1.1000m).Should().Be(10m);
    }

    [Fact]
    public void GetPipValuePerLot_UsdJpy_ReturnsCorrectValue()
    {
        // USDJPY @ 150: 100.000 * 0.01 / 150 = 1000/150 ≈ 6.6667
        var result = PipCalculator.GetPipValuePerLot("USDJPY", 150m);
        result.Should().BeApproximately(6.6667m, 0.001m);
    }

    [Fact]
    public void GetPipValuePerLot_UsdChf_KnownBug_MatchesIndexDueToUSPrefix()
    {
        // BUG: USDCHF matches s.StartsWith("US") in GetPipSize → treated as index (pipSize=1.0)
        // and also matches s.StartsWith("US") in GetPipValuePerLot → returns 1 * 1.0 = 1
        // This should actually be: 100_000 * 0.0001 / 0.9 ≈ 11.111 (USD/XXX pair)
        // Documenting current (buggy) behavior:
        var pipSize = PipCalculator.GetPipSize("USDCHF");
        pipSize.Should().Be(1.0m, "USDCHF incorrectly matches index rule via US prefix");
    }

    [Fact]
    public void GetPipValuePerLot_Gold_KnownBug_MatchesXxxUsdBeforeGold()
    {
        // BUG: XAUUSD[3..6] == "USD" matches the XXX/USD rule before the XAU rule
        // Returns 100_000 * 0.1 = 10_000 instead of correct 100 * 0.1 = $10
        PipCalculator.GetPipValuePerLot("XAUUSD", 2000m).Should().Be(10_000m,
            "XAUUSD incorrectly matches XXX/USD rule via s[3..6]==USD");
    }

    [Fact]
    public void GetPipValuePerLot_Index_Returns1()
    {
        // US100: 1 * 1.0 = $1
        PipCalculator.GetPipValuePerLot("US100", 18000m).Should().Be(1m);
    }

    [Fact]
    public void GetPipValuePerLot_CrossPairs_FallsBackToDefault()
    {
        // EURGBP: kein USD-Pattern -> Fallback: 100.000 * 0.0001 = $10
        PipCalculator.GetPipValuePerLot("EURGBP", 0.85m).Should().Be(10m);
    }

    // ── Roundtrip ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("EURUSD", 25)]
    [InlineData("USDJPY", 100)]
    [InlineData("XAUUSD", 50)]
    public void PriceToPips_PipsToPrice_Roundtrip(string symbol, int pips)
    {
        var price = PipCalculator.PipsToPrice(symbol, pips);
        var result = PipCalculator.PriceToPips(symbol, price);
        result.Should().Be(pips);
    }
}
