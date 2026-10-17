using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Infrastructure.Binance;
using MarketData.Ingestion.IntegrationTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MarketData.Ingestion.IntegrationTests.Unit;

/// <summary>
/// ADR-005: the WebSocket and REST paths upsert the same rows, so the same kline recorded from both
/// endpoints must map to identical candles.
/// </summary>
public sealed class KlineMapperGoldenTests
{
    private static readonly DateTimeOffset Now = new(2025, 10, 18, 12, 5, 0, TimeSpan.Zero);
    private static readonly SymbolInfo Btc = new(1, Symbol("BTCUSDT"), IsActive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ws_and_rest_payloads_of_the_same_kline_map_to_identical_candles()
    {
        var fromSocket = MapStreamed("ws-kline-closed.json");

        var handler = new StubHttpHandler();
        handler.EnqueueJson(BinanceFixtures.Golden("rest-klines.json"));
        var rest = new BinanceKlineRestClient(BinanceFixtures.RestClient(handler), new FakeTimeProvider(Now), NullLogger<BinanceKlineRestClient>.Instance);
        var pages = await rest.GetClosedCandlesAsync(Btc, new TimeRange(Now.AddMinutes(-5), Now.AddMinutes(-3)), Ct).ToListAsync(Ct);
        var fromRest = pages.SelectMany(p => p).First();

        Assert.NotNull(fromSocket);
        Assert.Equal(fromSocket, fromRest);
    }

    [Fact]
    public void Ws_kline_maps_every_column_at_full_precision()
    {
        var candle = MapStreamed("ws-kline-closed.json");

        Assert.Equal(
            new Candle(
                1,
                new DateTimeOffset(2025, 10, 18, 12, 0, 0, TimeSpan.Zero),
                Open: 106950.01m,
                High: 106970m,
                Low: 106940m,
                Close: 106960.5m,
                Volume: 12.34567m,
                QuoteVolume: 1320456.78901234m,
                TradeCount: 1243,
                ReceivedAt: Now),
            candle);
        Assert.Equal(TimeSpan.Zero, candle!.OpenTime.Offset);
    }

    [Fact]
    public void Open_kline_updates_are_not_mapped()
    {
        Assert.Null(MapStreamed("ws-kline-open.json"));
    }

    private static Candle? MapStreamed(string golden)
    {
        var message = BinanceFixtures.ParseStreamKline(BinanceFixtures.Golden(golden));
        return KlineMapper.TryMapClosed(1, message.Data, Now, out var candle) ? candle : null;
    }

    private static SymbolName Symbol(string name) => SymbolName.TryCreate(name, out var s) ? s : throw new ArgumentException(name);
}
