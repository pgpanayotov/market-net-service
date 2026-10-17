using MarketData.Api.Caching;
using MarketData.Core.Abstractions;
using MarketData.Core.Analysis;
using MarketData.Core.Domain;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MarketData.Api.IntegrationTests;

/// <summary>Database round-trips of the caching decorator, against substitutes (no container needed).</summary>
public class CachingPriceAnalysisServiceTests
{
    private static readonly DateTimeOffset Latest = new(2026, 10, 1, 0, 9, 0, TimeSpan.Zero);
    private static readonly SymbolInfo Btc = new(1, Name("BTCUSDT"), IsActive: true);

    private readonly ISymbolRepository _symbols = Substitute.For<ISymbolRepository>();
    private readonly ICandleQueryRepository _candles = Substitute.For<ICandleQueryRepository>();
    private readonly FakeTimeProvider _time = new(Latest.AddHours(1));
    private readonly AnalysisCacheOptions _options = new();
    private readonly AnalysisCache _cache;

    public CachingPriceAnalysisServiceTests()
    {
        _cache = new AnalysisCache(_options);
        _symbols.FindAsync(Name("BTCUSDT"), Arg.Any<CancellationToken>()).Returns(Btc);
        _candles.GetLatestOpenTimeAsync(Btc.Id, Arg.Any<CancellationToken>()).Returns(Latest);
        _candles.GetAverageCloseAsync(Btc.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new CloseAverage(10m, 10, Latest.AddMinutes(-9)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Miss_ProbesSymbolAndLatestOnce()
    {
        var result = await NewService().Get24hAverageAsync("btcusdt", Ct);

        Assert.True(result.IsSuccess);
        await _symbols.Received(1).FindAsync(Arg.Any<SymbolName>(), Ct);
        await _candles.Received(1).GetLatestOpenTimeAsync(Btc.Id, Ct);
        await _candles.Received(1).GetAverageCloseAsync(Btc.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Ct);
    }

    [Fact]
    public async Task Hit_OnlyProbes()
    {
        await NewService().Get24hAverageAsync("BTCUSDT", Ct);
        _symbols.ClearReceivedCalls();
        _candles.ClearReceivedCalls();

        var result = await NewService().Get24hAverageAsync("BTCUSDT", Ct);

        Assert.Equal(10m, result.Value!.AveragePrice);
        await _candles.Received(1).GetLatestOpenTimeAsync(Btc.Id, Ct);
        await _candles.DidNotReceiveWithAnyArgs().GetAverageCloseAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task Errors_AreNotCached()
    {
        _candles.GetAverageCloseAsync(Btc.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((CloseAverage?)null);
        Assert.IsType<AnalysisError.NoData>((await NewService().Get24hAverageAsync("BTCUSDT", Ct)).Error);

        _candles.GetAverageCloseAsync(Btc.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new CloseAverage(12m, 1, Latest));

        Assert.Equal(12m, (await NewService().Get24hAverageAsync("BTCUSDT", Ct)).Value!.AveragePrice);
    }

    [Fact]
    public async Task InvalidRequest_DoesNotTouchTheDatabase()
    {
        var result = await NewService().GetSmaAsync(new SmaQuery("BTCUSDT", 0, "1m"), Ct);

        Assert.IsType<AnalysisError.Invalid>(result.Error);
        await _symbols.DidNotReceiveWithAnyArgs().FindAsync(default, Ct);
    }

    [Fact]
    public async Task UnknownSymbol_LooksUpOnce()
    {
        var result = await NewService().Get24hAverageAsync("DOGEUSDT", Ct);

        Assert.IsType<AnalysisError.UnknownSymbol>(result.Error);
        await _symbols.Received(1).FindAsync(Arg.Any<SymbolName>(), Ct);
    }

    private CachingPriceAnalysisService NewService() => new(_symbols, _candles, _time, _cache, _options);

    private static SymbolName Name(string value) => SymbolName.TryCreate(value, out var name) ? name : throw new ArgumentException(value);
}
