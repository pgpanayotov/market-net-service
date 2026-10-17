using MarketData.Core.Domain;

namespace MarketData.Core.Tests.Domain;

public class SymbolNameTests
{
    [Theory]
    [InlineData("BTCUSDT", "BTCUSDT")]
    [InlineData("btcusdt", "BTCUSDT")]
    [InlineData("  EthUsdt ", "ETHUSDT")]
    [InlineData("1000SATSUSDT", "1000SATSUSDT")]
    public void TryCreate_NormalizesToUpperCase(string input, string expected)
    {
        Assert.True(SymbolName.TryCreate(input, out var symbol));
        Assert.Equal(expected, symbol.Value);
        Assert.Equal(expected, symbol.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BTC")]
    [InlineData("BTC-USDT")]
    [InlineData("BTC USDT")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
    public void TryCreate_RejectsInvalidNames(string? input)
    {
        Assert.False(SymbolName.TryCreate(input, out var symbol));
        Assert.Equal(string.Empty, symbol.ToString());
    }

    [Fact]
    public void Equality_IsByNormalizedValue()
    {
        SymbolName.TryCreate("adausdt", out var a);
        SymbolName.TryCreate("ADAUSDT", out var b);

        Assert.Equal(a, b);
    }
}
