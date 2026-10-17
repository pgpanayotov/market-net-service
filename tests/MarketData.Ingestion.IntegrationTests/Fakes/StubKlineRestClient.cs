using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using MarketData.Core.Domain;
using MarketData.Infrastructure.Binance;

namespace MarketData.Ingestion.IntegrationTests.Fakes;

/// <summary>
/// A fake exchange: every closed minute has a deterministic <see cref="Synthetic"/> candle, so tests can
/// compare what the worker stored with what "Binance" holds. Records every requested range.
/// </summary>
public sealed class StubKlineRestClient(TimeProvider timeProvider) : IKlineRestClient
{
    public ConcurrentQueue<(string Symbol, TimeRange Range)> Requests { get; } = new();

    /// <summary>When <c>false</c> the exchange has no history at all (only the WebSocket delivers).</summary>
    public bool HasHistory { get; set; } = true;

    public static Candle Synthetic(int symbolId, DateTimeOffset openTime)
    {
        var close = 100m + symbolId + ((openTime.ToUnixTimeSeconds() / 60 % 1000) / 100m);
        return new Candle(symbolId, openTime, close - 0.5m, close + 1m, close - 1m, close, 10.123456m, close * 10.123456m, 42, openTime.AddMinutes(1));
    }

    public async IAsyncEnumerable<IReadOnlyList<Candle>> GetClosedCandlesAsync(
        SymbolInfo symbol,
        TimeRange range,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Enqueue((symbol.Name.Value, range));
        await Task.Yield();
        if (!HasHistory)
        {
            yield break;
        }

        var now = timeProvider.GetUtcNow();
        var page = new List<Candle>();
        for (var t = Ceiling(range.From); t < range.To && t.AddMinutes(1) <= now; t = t.AddMinutes(1))
        {
            page.Add(Synthetic(symbol.Id, t) with { ReceivedAt = now });
            if (page.Count == BinanceKlineRestClient.MaxKlinesPerRequest)
            {
                yield return page;
                page = [];
            }
        }

        if (page.Count > 0)
        {
            yield return page;
        }
    }

    private static DateTimeOffset Ceiling(DateTimeOffset t)
    {
        var floor = new DateTimeOffset(t.UtcTicks - (t.UtcTicks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
        return floor == t ? floor : floor.AddMinutes(1);
    }
}
