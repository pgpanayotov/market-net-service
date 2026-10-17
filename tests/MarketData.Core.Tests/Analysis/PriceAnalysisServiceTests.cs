using MarketData.Core.Abstractions;
using MarketData.Core.Analysis;
using MarketData.Core.Domain;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using static MarketData.Core.Tests.TestTime;

namespace MarketData.Core.Tests.Analysis;

public class PriceAnalysisServiceTests
{
    private const int BtcId = 7;

    private static readonly DateTimeOffset Now = At("2026-10-18T12:07:30Z");

    private readonly ISymbolRepository _symbols = Substitute.For<ISymbolRepository>();
    private readonly ICandleQueryRepository _candles = Substitute.For<ICandleQueryRepository>();
    private readonly PriceAnalysisService _service;

    public PriceAnalysisServiceTests()
    {
        SymbolName.TryCreate("BTCUSDT", out var btc);
        _symbols.FindAsync(btc, Arg.Any<CancellationToken>()).Returns(new SymbolInfo(BtcId, btc, IsActive: true));
        _candles
            .GetBucketClosesAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(Array.Empty<BucketClose>());
        _service = new PriceAnalysisService(_symbols, _candles, new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- 24h average ----

    [Fact]
    public async Task Average_WindowIsAnchoredAtNewestStoredCandle()
    {
        // Newest candle 12:05 → data ends 12:06, even though the clock says 12:07:30.
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns(At("2026-10-18T12:05:00Z"));
        _candles.GetAverageCloseAsync(BtcId, At("2026-10-17T12:06:00Z"), At("2026-10-18T12:06:00Z"), Ct)
            .Returns(new CloseAverage(100.5m, 1440, At("2026-10-17T12:06:00Z")));

        var result = await _service.Get24hAverageAsync("btcusdt", Ct);

        Assert.True(result.IsSuccess);
        var response = result.Value!;
        Assert.Equal("BTCUSDT", response.Symbol);
        Assert.Equal(100.5m, response.AveragePrice);
        Assert.Equal(At("2026-10-17T12:06:00Z").UtcDateTime, response.From);
        Assert.Equal(At("2026-10-18T12:06:00Z").UtcDateTime, response.To);
        Assert.Equal(DateTimeKind.Utc, response.To.Kind);
        Assert.Equal(1440, response.DataPoints);
        Assert.True(response.IsFullPeriod);
    }

    [Fact]
    public async Task Average_WithLessThan24hHistory_ReportsAvailablePeriod()
    {
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns(At("2026-10-18T12:05:00Z"));
        _candles.GetAverageCloseAsync(BtcId, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Ct)
            .Returns(new CloseAverage(42m, 60, At("2026-10-18T11:06:00Z")));

        var response = (await _service.Get24hAverageAsync("BTCUSDT", Ct)).Value!;

        Assert.False(response.IsFullPeriod);
        Assert.Equal(At("2026-10-18T11:06:00Z").UtcDateTime, response.From);
    }

    [Fact]
    public async Task Average_InvalidSymbol_IsInvalid()
    {
        var result = await _service.Get24hAverageAsync("BTC/USDT", Ct);

        var invalid = Assert.IsType<AnalysisError.Invalid>(result.Error);
        Assert.Contains("symbol", invalid.Errors.Keys);
        await _symbols.DidNotReceiveWithAnyArgs().FindAsync(default, Ct);
    }

    [Fact]
    public async Task Average_UnknownSymbol()
    {
        var result = await _service.Get24hAverageAsync("SOLUSDT", Ct);

        Assert.Equal(new AnalysisError.UnknownSymbol("SOLUSDT"), result.Error);
    }

    [Fact]
    public async Task Average_SymbolWithoutData_IsNoData()
    {
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns((DateTimeOffset?)null);

        var result = await _service.Get24hAverageAsync("BTCUSDT", Ct);

        Assert.Equal(new AnalysisError.NoData("BTCUSDT"), result.Error);
    }

    [Fact]
    public async Task Average_RepositoryReturnsNothing_IsNoData()
    {
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns(At("2026-10-18T12:05:00Z"));
        _candles.GetAverageCloseAsync(default, default, default, Ct).ReturnsForAnyArgs((CloseAverage?)null);

        var result = await _service.Get24hAverageAsync("BTCUSDT", Ct);

        Assert.Equal(new AnalysisError.NoData("BTCUSDT"), result.Error);
    }

    // ---- SMA ----

    [Fact]
    public async Task Sma_DefaultStart_IsAnchoredAtDataEndAndFloored()
    {
        // Newest candle 12:05 → data ends 12:06 → 5m reference 12:05 (the 12:05–12:10 bucket is incomplete).
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns(At("2026-10-18T12:05:00Z"));
        GivenBuckets(TimePeriod.FiveMinutes, At("2026-10-18T10:25:00Z"), At("2026-10-18T12:05:00Z"), limit: 20,
            new BucketClose(At("2026-10-18T12:00:00Z"), 30m),
            new BucketClose(At("2026-10-18T11:55:00Z"), 10m));

        var result = await _service.GetSmaAsync(new SmaQuery("BTCUSDT", 20, "5m"), Ct);

        var response = result.Value!;
        Assert.Equal(At("2026-10-18T12:05:00Z").UtcDateTime, response.ReferenceTime);
        Assert.Equal("5m", response.Period);
        Assert.Equal(20, response.N);
        Assert.Equal(20m, response.SimpleMovingAverage);
        Assert.Equal(2, response.DataPointsUsed);
        Assert.Null(response.Series);
        Assert.Equal([At("2026-10-18T11:55:00Z").UtcDateTime, At("2026-10-18T12:00:00Z").UtcDateTime], response.Points!.Select(p => p.Time));
    }

    [Fact]
    public async Task Sma_ExplicitStartBeforeDataEnd_IsUsed()
    {
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns(At("2026-10-18T12:05:00Z"));
        GivenBuckets(TimePeriod.OneDay, At("2026-10-15T00:00:00Z"), At("2026-10-17T00:00:00Z"), limit: 2,
            new BucketClose(At("2026-10-16T00:00:00Z"), 5m));

        var response = (await _service.GetSmaAsync(new SmaQuery("BTCUSDT", 2, "1d", At("2026-10-17T13:00:00Z")), Ct)).Value!;

        Assert.Equal(At("2026-10-17T00:00:00Z").UtcDateTime, response.ReferenceTime);
        Assert.Equal(1, response.DataPointsUsed);
    }

    [Fact]
    public async Task Sma_StartAfterDataEnd_IsClampedToDataEnd()
    {
        // Clock 12:07:30, s = 12:07 but newest candle 11:58 → data end 11:59 → 1m reference 11:59.
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns(At("2026-10-18T11:58:00Z"));
        GivenBuckets(TimePeriod.OneMinute, At("2026-10-18T11:56:00Z"), At("2026-10-18T11:59:00Z"), limit: 3,
            new BucketClose(At("2026-10-18T11:58:00Z"), 1m));

        var response = (await _service.GetSmaAsync(new SmaQuery("BTCUSDT", 3, "1m", At("2026-10-18T12:07:00Z")), Ct)).Value!;

        Assert.Equal(At("2026-10-18T11:59:00Z").UtcDateTime, response.ReferenceTime);
    }

    [Fact]
    public async Task Sma_IncludePointsFalse_OmitsPoints()
    {
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns(At("2026-10-18T12:05:00Z"));
        _candles.GetBucketClosesAsync(default, default, default, default, default, Ct)
            .ReturnsForAnyArgs([new BucketClose(At("2026-10-18T12:04:00Z"), 1m)]);

        var response = (await _service.GetSmaAsync(new SmaQuery("BTCUSDT", 1, "1m", IncludePoints: false), Ct)).Value!;

        Assert.Null(response.Points);
        Assert.Equal(1m, response.SimpleMovingAverage);
    }

    [Fact]
    public async Task Sma_Series_FetchesNPlusCountMinusOneBuckets()
    {
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns(At("2026-10-18T12:05:00Z"));
        // n=3, count=2 → limit 4 → from 12:05 - 20m.
        GivenBuckets(TimePeriod.FiveMinutes, At("2026-10-18T11:45:00Z"), At("2026-10-18T12:05:00Z"), limit: 4,
            new BucketClose(At("2026-10-18T11:45:00Z"), 1m),
            new BucketClose(At("2026-10-18T11:50:00Z"), 2m),
            new BucketClose(At("2026-10-18T11:55:00Z"), 3m),
            new BucketClose(At("2026-10-18T12:00:00Z"), 4m));

        var response = (await _service.GetSmaAsync(new SmaQuery("BTCUSDT", 3, "5m", Series: true, Count: 2), Ct)).Value!;

        Assert.Null(response.SimpleMovingAverage);
        Assert.Null(response.DataPointsUsed);
        Assert.Null(response.Points);
        Assert.Collection(
            response.Series!,
            first =>
            {
                Assert.Equal(At("2026-10-18T12:00:00Z").UtcDateTime, first.Time);
                Assert.Equal(2m, first.SimpleMovingAverage);
                Assert.Equal(3, first.DataPointsUsed);
            },
            last =>
            {
                Assert.Equal(At("2026-10-18T12:05:00Z").UtcDateTime, last.Time);
                Assert.Equal(3m, last.SimpleMovingAverage);
            });
    }

    [Fact]
    public async Task Sma_SeriesWithoutData_IsNoData()
    {
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns(At("2026-10-18T12:05:00Z"));

        var result = await _service.GetSmaAsync(new SmaQuery("BTCUSDT", 3, "5m", Series: true), Ct);

        Assert.Equal(new AnalysisError.NoData("BTCUSDT"), result.Error);
    }

    [Fact]
    public async Task Sma_SingleValueWithoutData_IsNoData()
    {
        _candles.GetLatestOpenTimeAsync(BtcId, Ct).Returns(At("2026-10-18T12:05:00Z"));

        var result = await _service.GetSmaAsync(new SmaQuery("BTCUSDT", 3, "5m"), Ct);

        Assert.Equal(new AnalysisError.NoData("BTCUSDT"), result.Error);
    }

    [Fact]
    public async Task Sma_InvalidQuery_DoesNotTouchRepositories()
    {
        var result = await _service.GetSmaAsync(new SmaQuery("BTCUSDT", 0, "5m"), Ct);

        Assert.IsType<AnalysisError.Invalid>(result.Error);
        await _symbols.DidNotReceiveWithAnyArgs().FindAsync(default, Ct);
    }

    [Fact]
    public async Task Sma_UnknownSymbol()
    {
        var result = await _service.GetSmaAsync(new SmaQuery("SOLUSDT", 3, "5m"), Ct);

        Assert.Equal(new AnalysisError.UnknownSymbol("SOLUSDT"), result.Error);
    }

    [Fact]
    public async Task Sma_SymbolWithoutData_IsNoData()
    {
        var result = await _service.GetSmaAsync(new SmaQuery("BTCUSDT", 3, "5m"), Ct);

        Assert.Equal(new AnalysisError.NoData("BTCUSDT"), result.Error);
    }

    private void GivenBuckets(TimePeriod period, DateTimeOffset from, DateTimeOffset to, int limit, params BucketClose[] buckets) =>
        _candles.GetBucketClosesAsync(BtcId, period, from, to, limit, Arg.Any<CancellationToken>()).Returns(buckets);
}
