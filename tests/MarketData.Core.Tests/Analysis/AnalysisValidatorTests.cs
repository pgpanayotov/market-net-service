using MarketData.Core.Analysis;
using MarketData.Core.Domain;
using static MarketData.Core.Tests.TestTime;

namespace MarketData.Core.Tests.Analysis;

public class AnalysisValidatorTests
{
    private static readonly DateTimeOffset Now = At("2026-10-18T12:07:30Z");

    [Fact]
    public void Validate_ValidSingleValueQuery()
    {
        var errors = AnalysisValidator.Validate(new SmaQuery("btcusdt", 20, "5m", Now), Now, out var valid);

        Assert.Empty(errors);
        Assert.Equal(new ValidSmaQuery(Sym("BTCUSDT"), 20, TimePeriod.FiveMinutes, Now, null, true), valid);
    }

    [Theory]
    [InlineData(20, null, 20)]
    [InlineData(500, null, AnalysisLimits.DefaultCountCap)]
    [InlineData(500, 7, 7)]
    [InlineData(1, AnalysisLimits.MaxCount, AnalysisLimits.MaxCount)]
    public void Validate_Series_DefaultsCountToMinOfNAndCap(int n, int? count, int expected)
    {
        var errors = AnalysisValidator.Validate(new SmaQuery("ETHUSDT", n, "1d", Series: true, Count: count), Now, out var valid);

        Assert.Empty(errors);
        Assert.Equal(expected, valid!.SeriesCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(AnalysisLimits.MaxN + 1)]
    public void Validate_RejectsNOutOfRange(int n)
    {
        AssertSingleError(new SmaQuery("BTCUSDT", n, "1m"), "n");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1h")]
    public void Validate_RejectsUnknownPeriod(string? period)
    {
        AssertSingleError(new SmaQuery("BTCUSDT", 5, period), "p");
    }

    [Fact]
    public void Validate_RejectsFutureStart()
    {
        AssertSingleError(new SmaQuery("BTCUSDT", 5, "1m", Now.AddSeconds(1)), "s");
    }

    [Fact]
    public void Validate_RejectsCountWithoutSeries()
    {
        AssertSingleError(new SmaQuery("BTCUSDT", 5, "1m", Count: 3), "count");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AnalysisLimits.MaxCount + 1)]
    public void Validate_RejectsSeriesCountOutOfRange(int count)
    {
        AssertSingleError(new SmaQuery("BTCUSDT", 5, "1m", Series: true, Count: count), "count");
    }

    [Fact]
    public void Validate_RejectsInvalidSymbol()
    {
        AssertSingleError(new SmaQuery("BTC-USDT", 5, "1m"), "symbol");
    }

    [Fact]
    public void Validate_ReportsAllErrorsAtOnce()
    {
        var errors = AnalysisValidator.Validate(new SmaQuery("x", 0, "2h", Now.AddDays(1), Count: 5), Now, out var valid);

        Assert.Null(valid);
        Assert.Equal(["count", "n", "p", "s", "symbol"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ValidateSymbol_NormalizesOrReportsError()
    {
        Assert.Empty(AnalysisValidator.ValidateSymbol("adausdt", out var name));
        Assert.Equal("ADAUSDT", name.Value);
        Assert.Contains("symbol", AnalysisValidator.ValidateSymbol("??", out _).Keys);
    }

    private static void AssertSingleError(SmaQuery query, string key)
    {
        var errors = AnalysisValidator.Validate(query, Now, out var valid);

        Assert.Null(valid);
        Assert.Equal([key], errors.Keys);
    }

    private static SymbolName Sym(string value)
    {
        SymbolName.TryCreate(value, out var name);
        return name;
    }
}
