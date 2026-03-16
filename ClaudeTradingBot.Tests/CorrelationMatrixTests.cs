using ClaudeTradingBot.Models;
using FluentAssertions;

namespace ClaudeTradingBot.Tests;

public class CorrelationMatrixTests
{
    [Fact]
    public void GetCorrelation_SameSymbol_Returns1()
    {
        CorrelationMatrix.GetCorrelation("EURUSD", "EURUSD").Should().Be(1.0);
    }

    [Fact]
    public void GetCorrelation_KnownPair_ReturnsCorrectValue()
    {
        CorrelationMatrix.GetCorrelation("EURUSD", "GBPUSD").Should().Be(0.85);
    }

    [Fact]
    public void GetCorrelation_ReversedOrder_ReturnsSameValue()
    {
        CorrelationMatrix.GetCorrelation("GBPUSD", "EURUSD").Should().Be(0.85);
    }

    [Fact]
    public void GetCorrelation_NegativeCorrelation()
    {
        CorrelationMatrix.GetCorrelation("EURUSD", "USDCHF").Should().Be(-0.90);
    }

    [Fact]
    public void GetCorrelation_UnknownPair_Returns0()
    {
        CorrelationMatrix.GetCorrelation("ABCDEF", "GHIJKL").Should().Be(0.0);
    }

    [Fact]
    public void GetCorrelation_CaseInsensitive()
    {
        CorrelationMatrix.GetCorrelation("eurusd", "gbpusd").Should().Be(0.85);
    }

    [Fact]
    public void GetCorrelation_Indices()
    {
        CorrelationMatrix.GetCorrelation("US100", "US500").Should().Be(0.95);
    }

    [Fact]
    public void GetCorrelation_DynamicOverridesStatic()
    {
        // Save current state
        var dynamicData = new Dictionary<(string, string), double>
        {
            { ("EURUSD", "GBPUSD"), 0.75 }
        };

        CorrelationMatrix.UpdateDynamic(dynamicData);

        try
        {
            CorrelationMatrix.GetCorrelation("EURUSD", "GBPUSD").Should().Be(0.75);
        }
        finally
        {
            // Reset dynamic data
            CorrelationMatrix.UpdateDynamic(new Dictionary<(string, string), double>());
        }
    }
}
