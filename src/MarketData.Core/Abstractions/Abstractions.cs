using MarketData.Core.Domain;

namespace MarketData.Core.Abstractions;

public sealed record SymbolInfo(int Id, SymbolName Name, bool IsActive);

/// <summary>Close of the last 1-minute candle in a bucket.</summary>
public readonly record struct BucketClose(DateTimeOffset BucketStart, decimal Close);

/// <summary>Aggregate of the 1-minute closes in a time range.</summary>
public readonly record struct CloseAverage(decimal Average, int Count, DateTimeOffset FirstOpenTime);

public interface ISymbolRepository
{
    /// <summary>Finds a symbol whether it is active or not (history of removed symbols stays queryable).</summary>
    Task<SymbolInfo?> FindAsync(SymbolName name, CancellationToken cancellationToken);
}

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
}

/// <summary>
/// Newest stored candle per symbol. In the API it is kept current by <c>candles_written</c>
/// notifications (ADR-007).
/// </summary>
public interface ISymbolDataState
{
    /// <summary>Open time of the newest stored candle; <c>null</c> when the symbol has no data.</summary>
    DateTimeOffset? GetLatestOpenTime(int symbolId);
}
