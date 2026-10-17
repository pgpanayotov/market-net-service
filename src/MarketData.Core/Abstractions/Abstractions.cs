using MarketData.Core.Domain;

namespace MarketData.Core.Abstractions;

public sealed record SymbolInfo(int Id, SymbolName Name, bool IsActive);

/// <summary>A symbol with its admin metadata, as listed by the admin API.</summary>
public sealed record SymbolDetails(int Id, SymbolName Name, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastCandleAt);

public enum SymbolActivation
{
    /// <summary>The symbol did not exist and was inserted.</summary>
    Created,

    /// <summary>The symbol existed as inactive and was reactivated; its history is kept.</summary>
    Reactivated,

    /// <summary>The symbol was already active; nothing changed.</summary>
    AlreadyActive,
}

public enum SymbolDeactivation
{
    Deactivated,
    AlreadyInactive,
    NotFound,
}

/// <summary>Close of the last 1-minute candle in a bucket.</summary>
public readonly record struct BucketClose(DateTimeOffset BucketStart, decimal Close);

/// <summary>Aggregate of the 1-minute closes in a time range.</summary>
public readonly record struct CloseAverage(decimal Average, int Count, DateTimeOffset FirstOpenTime);

/// <summary>
/// Symbol storage. Writes join the caller's ambient transaction, so the API can commit a change
/// and its <c>symbol_changes</c> notification together (ADR-007).
/// </summary>
public interface ISymbolRepository
{
    /// <summary>Finds a symbol whether it is active or not (history of removed symbols stays queryable).</summary>
    Task<SymbolInfo?> FindAsync(SymbolName name, CancellationToken cancellationToken);

    /// <summary>All symbols, active and inactive, ordered by name.</summary>
    Task<IReadOnlyList<SymbolDetails>> ListAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SymbolInfo>> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Inserts the symbol, or reactivates it if it was removed.</summary>
    Task<(SymbolActivation Outcome, SymbolInfo Symbol)> ActivateAsync(SymbolName name, CancellationToken cancellationToken);

    /// <summary>Soft-deletes the symbol (<c>is_active = false</c>); candles are never purged.</summary>
    Task<SymbolDeactivation> DeactivateAsync(SymbolName name, CancellationToken cancellationToken);
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

    /// <summary>Open time of the newest candle for every symbol that has data.</summary>
    Task<IReadOnlyDictionary<int, DateTimeOffset>> GetLatestOpenTimesAsync(CancellationToken cancellationToken);
}

/// <summary>Candle writes and completeness checks used by the ingestion worker (ADR-005).</summary>
public interface ICandleIngestRepository
{
    /// <summary>
    /// Idempotent upsert on <c>(symbol_id, open_time)</c>. Rows whose values are unchanged are not
    /// rewritten. Returns the number of rows inserted or updated.
    /// </summary>
    Task<int> UpsertAsync(IReadOnlyCollection<Candle> candles, CancellationToken cancellationToken);

    /// <summary>
    /// Missing 1-minute candles in [<paramref name="from"/>, <paramref name="to"/>), merged into
    /// contiguous ranges, oldest first. Minutes before the symbol's first stored candle are not gaps
    /// (that history is the tail backfill's job), so a symbol with no candles returns no ranges.
    /// </summary>
    Task<IReadOnlyList<TimeRange>> FindMissingRangesAsync(int symbolId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
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
