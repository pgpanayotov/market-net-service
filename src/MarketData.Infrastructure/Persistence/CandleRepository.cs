using MarketData.Core.Abstractions;
using MarketData.Core.Domain;

namespace MarketData.Infrastructure.Persistence;

/// <summary>Hand-written SQL over <c>candles_1m</c>; every statement is a range scan on the PK <c>(symbol_id, open_time)</c>.</summary>
public sealed class CandleRepository(MarketDataDbContext db) : ICandleQueryRepository, ICandleIngestRepository
{
    public async Task<CloseAverage?> GetAverageCloseAsync(int symbolId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // Rounded in SQL: an unrounded numeric avg() can carry more digits than System.Decimal holds.
        const string sql = """
            SELECT round(avg(close), 10), count(*)::int, min(open_time)
            FROM candles_1m
            WHERE symbol_id = @symbol_id AND open_time >= @from AND open_time < @to
            """;

        await using var command = await db.CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false);
        command.With("symbol_id", symbolId).With("from", from.Utc()).With("to", to.Utc());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return reader.IsDBNull(0)
            ? null
            : new CloseAverage(reader.GetDecimal(0), reader.GetInt32(1), reader.GetUtcDateTimeOffset(2));
    }

    public async Task<IReadOnlyList<BucketClose>> GetBucketClosesAsync(
        int symbolId,
        TimePeriod period,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken)
    {
        // ADR-002: one round-trip. The date_bin origin must equal TimePeriod.BucketOrigin (a Monday),
        // so SQL and C# agree on bucket boundaries.
        const string sql = """
            SELECT bucket, close FROM (
              SELECT DISTINCT ON (bucket)
                     date_bin(@step, open_time, @origin) AS bucket,
                     close
              FROM candles_1m
              WHERE symbol_id = @symbol_id AND open_time >= @from AND open_time < @to
              ORDER BY bucket DESC, open_time DESC
            ) b
            ORDER BY bucket DESC
            LIMIT @limit
            """;

        await using var command = await db.CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false);
        command
            .With("step", period.Duration)
            .With("origin", TimePeriod.BucketOrigin.Utc())
            .With("symbol_id", symbolId)
            .With("from", from.Utc())
            .With("to", to.Utc())
            .With("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<BucketClose>(limit);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new BucketClose(reader.GetUtcDateTimeOffset(0), reader.GetDecimal(1)));
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<int, DateTimeOffset>> GetLatestOpenTimesAsync(CancellationToken cancellationToken)
    {
        // LATERAL max() per symbol uses the PK backwards; a GROUP BY would scan the whole table.
        const string sql = """
            SELECT s.id, l.open_time
            FROM symbols s
            CROSS JOIN LATERAL (SELECT max(c.open_time) AS open_time FROM candles_1m c WHERE c.symbol_id = s.id) l
            WHERE l.open_time IS NOT NULL
            """;

        await using var command = await db.CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<int, DateTimeOffset>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetInt32(0)] = reader.GetUtcDateTimeOffset(1);
        }

        return result;
    }

    public async Task<int> UpsertAsync(IReadOnlyCollection<Candle> candles, CancellationToken cancellationToken)
    {
        // A key may appear only once per statement (ON CONFLICT can't touch a row twice); the last occurrence wins.
        var batch = candles
            .GroupBy(c => (c.SymbolId, c.OpenTime.UtcTicks))
            .Select(g => g.Last())
            .ToArray();
        if (batch.Length == 0)
        {
            return 0;
        }

        // The WHERE skips no-op updates, so replays and backfills of identical data don't create dead tuples.
        const string sql = """
            INSERT INTO candles_1m AS c
                (symbol_id, open_time, open, high, low, close, volume, quote_volume, trade_count, received_at)
            SELECT * FROM unnest(@symbol_id, @open_time, @open, @high, @low, @close, @volume, @quote_volume, @trade_count, @received_at)
            ON CONFLICT (symbol_id, open_time) DO UPDATE SET
                open = EXCLUDED.open,
                high = EXCLUDED.high,
                low = EXCLUDED.low,
                close = EXCLUDED.close,
                volume = EXCLUDED.volume,
                quote_volume = EXCLUDED.quote_volume,
                trade_count = EXCLUDED.trade_count,
                received_at = EXCLUDED.received_at
            WHERE (c.open, c.high, c.low, c.close, c.volume, c.quote_volume, c.trade_count)
                  IS DISTINCT FROM
                  (EXCLUDED.open, EXCLUDED.high, EXCLUDED.low, EXCLUDED.close, EXCLUDED.volume, EXCLUDED.quote_volume, EXCLUDED.trade_count)
            """;

        await using var command = await db.CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false);
        command
            .With("symbol_id", batch.Select(c => c.SymbolId).ToArray())
            .With("open_time", batch.Select(c => c.OpenTime.Utc()).ToArray())
            .With("open", batch.Select(c => c.Open).ToArray())
            .With("high", batch.Select(c => c.High).ToArray())
            .With("low", batch.Select(c => c.Low).ToArray())
            .With("close", batch.Select(c => c.Close).ToArray())
            .With("volume", batch.Select(c => c.Volume).ToArray())
            .With("quote_volume", batch.Select(c => c.QuoteVolume).ToArray())
            .With("trade_count", batch.Select(c => c.TradeCount).ToArray())
            .With("received_at", batch.Select(c => c.ReceivedAt.Utc()).ToArray());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TimeRange>> FindMissingRangesAsync(int symbolId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // ADR-005 gap scan. The scan starts at max(from, first stored candle); HAVING removes the row
        // entirely for a symbol with no candles. Consecutive missing minutes share the same
        // (minute - row_number) value, which groups them into ranges.
        const string sql = """
            WITH first_candle AS (
              SELECT min(open_time) AS t FROM candles_1m WHERE symbol_id = @symbol_id
              HAVING min(open_time) IS NOT NULL
            ),
            missing AS (
              SELECT m
              FROM first_candle
              CROSS JOIN LATERAL generate_series(GREATEST(@from, first_candle.t), @to - interval '1 minute', interval '1 minute') AS m
              WHERE NOT EXISTS (SELECT 1 FROM candles_1m c WHERE c.symbol_id = @symbol_id AND c.open_time = m)
            ),
            grouped AS (
              SELECT m, m - (row_number() OVER (ORDER BY m)) * interval '1 minute' AS grp FROM missing
            )
            SELECT min(m), max(m) + interval '1 minute'
            FROM grouped
            GROUP BY grp
            ORDER BY 1
            """;

        await using var command = await db.CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false);
        command
            .With("symbol_id", symbolId)
            .With("from", TimePeriod.OneMinute.Floor(from).Utc())
            .With("to", TimePeriod.OneMinute.Floor(to).Utc());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<TimeRange>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TimeRange(reader.GetUtcDateTimeOffset(0), reader.GetUtcDateTimeOffset(1)));
        }

        return result;
    }
}
