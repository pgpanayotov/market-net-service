using MarketData.Core.Domain;

namespace MarketData.Core.Abstractions;

public interface ICandleQueryRepository
{
    /// <summary>Average close of candles with open time in [<paramref name="from"/>, <paramref name="to"/>); <c>null</c> when there are none.</summary>
    Task<CloseAverage?> GetAverageCloseAsync(int symbolId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>
    /// Close of each <paramref name="period"/> bucket that has data and starts in
    /// [<paramref name="from"/>, <paramref name="to"/>), newest <paramref name="limit"/> buckets, in any order.
    /// </summary>
    Task<IReadOnlyList<BucketClose>> GetBucketClosesAsync(
        int symbolId,
        TimePeriod period,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Open time of the newest stored candle; <c>null</c> when the symbol has no data. The analysis
    /// window is anchored here (ADR-006). It is a backward probe of the PK index, cheap enough to run
    /// on every request.
    /// </summary>
    Task<DateTimeOffset?> GetLatestOpenTimeAsync(int symbolId, CancellationToken cancellationToken);

    /// <summary>Open time of the oldest stored candle; <c>null</c> when the symbol has no data. Used by the worker's history-depth backfill (ADR-005).</summary>
    Task<DateTimeOffset?> GetFirstOpenTimeAsync(int symbolId, CancellationToken cancellationToken);

    /// <summary>Open time of the newest candle for every symbol that has data.</summary>
    Task<IReadOnlyDictionary<int, DateTimeOffset>> GetLatestOpenTimesAsync(CancellationToken cancellationToken);
}
