using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketData.Infrastructure.IntegrationTests;

public class CandleRepositoryTests(PostgresFixture postgres) : PostgresTest(postgres)
{
    private static readonly DateTimeOffset T0 = At("2026-10-18T12:00:00Z");

    // ---- Upsert ----

    [Fact]
    public async Task Upsert_InsertsThenIgnoresIdenticalReplays()
    {
        await using var db = Postgres.CreateContext();
        var repository = new CandleRepository(db);
        var candles = Minutes(Btc, T0, 1m, 2m, 3m).ToArray();

        Assert.Equal(3, await repository.UpsertAsync(candles, Ct));
        Assert.Equal(0, await repository.UpsertAsync(candles, Ct));
        Assert.Equal(3, await db.Candles.CountAsync(Ct));
    }

    [Fact]
    public async Task Upsert_UpdatesChangedValues()
    {
        await SeedAsync(Minutes(Btc, T0, 1m));
        await using var db = Postgres.CreateContext();

        var affected = await new CandleRepository(db).UpsertAsync([C(Btc, T0, 9m)], Ct);

        Assert.Equal(1, affected);
        Assert.Equal(9m, (await db.Candles.SingleAsync(Ct)).Close);
    }

    [Fact]
    public async Task Upsert_DuplicateKeysInOneBatch_LastWins()
    {
        await using var db = Postgres.CreateContext();

        await new CandleRepository(db).UpsertAsync([C(Btc, T0, 1m), C(Btc, T0, 2m)], Ct);

        Assert.Equal(2m, (await db.Candles.SingleAsync(Ct)).Close);
    }

    [Fact]
    public async Task Upsert_EmptyBatch_IsNoOp()
    {
        await using var db = Postgres.CreateContext();

        Assert.Equal(0, await new CandleRepository(db).UpsertAsync([], Ct));
    }

    [Fact]
    public async Task Upsert_StoresAllColumnsWithFullPrecision()
    {
        var candle = new Candle(Ada, T0, 0.1234567891m, 0.2m, 0.05m, 0.3519876543m, 123456789.0123456789m, 98765.4321m, 42, T0.AddSeconds(60.5));
        await SeedAsync([candle]);
        await using var db = Postgres.CreateContext();

        var row = await db.Candles.SingleAsync(Ct);

        Assert.Equal(
            (candle.SymbolId, candle.OpenTime, candle.Open, candle.High, candle.Low, candle.Close, candle.Volume, candle.QuoteVolume, candle.TradeCount, candle.ReceivedAt),
            (row.SymbolId, row.OpenTime, row.Open, row.High, row.Low, row.Close, row.Volume, row.QuoteVolume, row.TradeCount, row.ReceivedAt));
    }

    // ---- 24h average ----

    [Fact]
    public async Task GetAverageClose_IsHalfOpenAndRoundedToTenDecimals()
    {
        await SeedAsync(Minutes(Btc, T0, 1m, 0m, 0m, 1000m));
        await SeedAsync(Minutes(Eth, T0, 555m));
        await using var db = Postgres.CreateContext();

        var average = await new CandleRepository(db).GetAverageCloseAsync(Btc, T0, T0.AddMinutes(3), Ct);

        Assert.Equal(new CloseAverage(0.3333333333m, 3, T0), average);
    }

    [Fact]
    public async Task GetAverageClose_NoCandles_IsNull()
    {
        await SeedAsync(Minutes(Btc, T0, 1m));
        await using var db = Postgres.CreateContext();

        Assert.Null(await new CandleRepository(db).GetAverageCloseAsync(Btc, T0.AddMinutes(1), T0.AddMinutes(5), Ct));
    }

    // ---- SMA buckets ----

    [Fact]
    public async Task GetBucketCloses_FiveMinutes_UsesLastCandleOfEachBucket()
    {
        // 12:00..12:09 → buckets 12:00 (close of 12:04) and 12:05 (close of 12:09).
        await SeedAsync(Minutes(Btc, T0, 0m, 1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 9m));
        await using var db = Postgres.CreateContext();

        var buckets = await new CandleRepository(db).GetBucketClosesAsync(Btc, TimePeriod.FiveMinutes, T0, T0.AddMinutes(10), 10, Ct);

        Assert.Equal([new BucketClose(T0.AddMinutes(5), 9m), new BucketClose(T0, 4m)], buckets);
    }

    [Fact]
    public async Task GetBucketCloses_RespectsRangeAndLimitNewestFirst()
    {
        await SeedAsync(Minutes(Btc, T0, 0m, 1m, 2m, 3m, 4m, 5m));
        await using var db = Postgres.CreateContext();

        var buckets = await new CandleRepository(db).GetBucketClosesAsync(Btc, TimePeriod.OneMinute, T0.AddMinutes(1), T0.AddMinutes(5), 2, Ct);

        Assert.Equal([new BucketClose(T0.AddMinutes(4), 4m), new BucketClose(T0.AddMinutes(3), 3m)], buckets);
    }

    [Fact]
    public async Task GetBucketCloses_Day_SplitsAtUtcMidnight()
    {
        await SeedAsync([
            C(Btc, At("2026-10-17T23:58:00Z"), 1m),
            C(Btc, At("2026-10-17T23:59:00Z"), 2m),
            C(Btc, At("2026-10-18T00:00:00Z"), 3m),
        ]);
        await using var db = Postgres.CreateContext();

        var buckets = await new CandleRepository(db).GetBucketClosesAsync(
            Btc, TimePeriod.OneDay, At("2026-10-17T00:00:00Z"), At("2026-10-19T00:00:00Z"), 5, Ct);

        Assert.Equal([new BucketClose(At("2026-10-18T00:00:00Z"), 3m), new BucketClose(At("2026-10-17T00:00:00Z"), 2m)], buckets);
    }

    [Fact]
    public async Task GetBucketCloses_Week_StartsOnMonday()
    {
        // Sunday 2026-10-18 23:59 belongs to the week of Monday 2026-10-12; Monday 2026-10-19 00:00 starts a new one.
        await SeedAsync([
            C(Btc, At("2026-10-12T00:00:00Z"), 1m),
            C(Btc, At("2026-10-18T23:59:00Z"), 2m),
            C(Btc, At("2026-10-19T00:00:00Z"), 3m),
        ]);
        await using var db = Postgres.CreateContext();

        var buckets = await new CandleRepository(db).GetBucketClosesAsync(
            Btc, TimePeriod.OneWeek, At("2026-10-05T00:00:00Z"), At("2026-10-26T00:00:00Z"), 5, Ct);

        Assert.Equal([new BucketClose(At("2026-10-19T00:00:00Z"), 3m), new BucketClose(At("2026-10-12T00:00:00Z"), 2m)], buckets);
    }

    [Theory]
    [InlineData("1m")]
    [InlineData("5m")]
    [InlineData("30m")]
    [InlineData("1d")]
    [InlineData("1w")]
    public async Task GetBucketCloses_BucketStartsMatchTimePeriodFloor(string code)
    {
        // SQL date_bin and C# TimePeriod.Floor must agree on every boundary (the SMA math depends on it).
        var period = TimePeriod.Parse(code);
        var random = new Random(42);
        var times = Enumerable.Range(0, 200)
            .Select(_ => TimePeriod.OneMinute.Floor(At("2026-01-01T00:00:00Z").AddMinutes(random.Next(0, 400_000))))
            .Distinct()
            .ToArray();
        await SeedAsync(times.Select(t => C(Btc, t, 1m)));
        await using var db = Postgres.CreateContext();

        var buckets = await new CandleRepository(db).GetBucketClosesAsync(
            Btc, period, At("2025-12-01T00:00:00Z"), At("2027-01-01T00:00:00Z"), 1000, Ct);

        Assert.Equal(
            times.Select(period.Floor).Distinct().Order(),
            buckets.Select(b => b.BucketStart).Order());
    }

    // ---- Latest ----

    [Fact]
    public async Task GetLatestOpenTime_IsNewestCandleOfThatSymbol_OrNull()
    {
        await SeedAsync(Minutes(Btc, T0, 1m, 2m, 3m));
        await SeedAsync(Minutes(Eth, T0.AddHours(1), 1m));
        await using var db = Postgres.CreateContext();
        var repository = new CandleRepository(db);

        Assert.Equal(T0.AddMinutes(2), await repository.GetLatestOpenTimeAsync(Btc, Ct));
        Assert.Null(await repository.GetLatestOpenTimeAsync(Ada, Ct));
    }

    [Fact]
    public async Task GetLatestOpenTimes_OnlySymbolsWithData()
    {
        await SeedAsync(Minutes(Btc, T0, 1m, 2m, 3m));
        await SeedAsync(Minutes(Eth, T0, 1m));
        await using var db = Postgres.CreateContext();

        var latest = await new CandleRepository(db).GetLatestOpenTimesAsync(Ct);

        Assert.Equal(new Dictionary<int, DateTimeOffset> { [Btc] = T0.AddMinutes(2), [Eth] = T0 }, latest);
    }

    // ---- Gap scan ----

    [Fact]
    public async Task FindMissingRanges_MergesConsecutiveMissingMinutes()
    {
        // Present: 0 1 _ _ 4 5 _ _ 8 _   → missing [2,4) [6,8) [9,10)
        await SeedAsync([C(Btc, T0, 1m), C(Btc, T0.AddMinutes(1), 1m), C(Btc, T0.AddMinutes(4), 1m), C(Btc, T0.AddMinutes(5), 1m), C(Btc, T0.AddMinutes(8), 1m)]);
        await using var db = Postgres.CreateContext();

        var ranges = await new CandleRepository(db).FindMissingRangesAsync(Btc, T0, T0.AddMinutes(10), Ct);

        Assert.Equal(
            [
                new TimeRange(T0.AddMinutes(2), T0.AddMinutes(4)),
                new TimeRange(T0.AddMinutes(6), T0.AddMinutes(8)),
                new TimeRange(T0.AddMinutes(9), T0.AddMinutes(10)),
            ],
            ranges);
    }

    [Fact]
    public async Task FindMissingRanges_IgnoresMinutesBeforeFirstCandle()
    {
        await SeedAsync([C(Btc, T0.AddMinutes(5), 1m)]);
        await using var db = Postgres.CreateContext();

        var ranges = await new CandleRepository(db).FindMissingRangesAsync(Btc, T0, T0.AddMinutes(8), Ct);

        Assert.Equal([new TimeRange(T0.AddMinutes(6), T0.AddMinutes(8))], ranges);
    }

    [Fact]
    public async Task FindMissingRanges_SymbolWithoutCandles_IsEmpty()
    {
        await SeedAsync(Minutes(Btc, T0, 1m));
        await using var db = Postgres.CreateContext();

        Assert.Empty(await new CandleRepository(db).FindMissingRangesAsync(Ada, T0, T0.AddHours(1), Ct));
    }

    [Fact]
    public async Task FindMissingRanges_FloorsUnalignedBounds_AndCompleteDataHasNoGaps()
    {
        await SeedAsync(Minutes(Btc, T0, 1m, 2m, 3m));
        await using var db = Postgres.CreateContext();

        var ranges = await new CandleRepository(db).FindMissingRangesAsync(Btc, T0.AddSeconds(30), T0.AddMinutes(3).AddSeconds(59), Ct);

        Assert.Empty(ranges);
    }
}
