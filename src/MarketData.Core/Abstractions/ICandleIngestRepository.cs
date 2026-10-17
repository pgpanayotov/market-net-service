using MarketData.Core.Domain;

namespace MarketData.Core.Abstractions;

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
