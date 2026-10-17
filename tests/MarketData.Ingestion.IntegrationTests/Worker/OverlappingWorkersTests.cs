using MarketData.Core.Domain;

namespace MarketData.Ingestion.IntegrationTests.Worker;

/// <summary>
/// ADR-009: there is no leader election, so a rolling deploy briefly runs two workers. That must be
/// harmless: both ingest, and every candle is still stored exactly once (idempotent upserts).
/// </summary>
public sealed class OverlappingWorkersTests(PostgresFixture postgres) : WorkerTest(postgres)
{
    [Fact]
    public async Task Two_workers_side_by_side_store_each_candle_once()
    {
        var first = StartWorker();
        var second = StartWorker();
        first.Rest.HasHistory = false;
        second.Rest.HasHistory = false;
        await EventuallyAsync(() => first.Stream.SubscribedNames.Count == 3 && second.Stream.SubscribedNames.Count == 3, "both workers ingesting");

        var minute = ThisMinute.AddMinutes(-1);
        var candle = new Candle(Btc, minute, 1m, 2m, 0.5m, 1.5m, 10m, 15m, 7, DateTimeOffset.UtcNow);
        first.Stream.Emit(candle);
        second.Stream.Emit(candle with { ReceivedAt = DateTimeOffset.UtcNow });

        await EventuallyAsync(async () => (await CandlesAsync(Btc)).Any(c => c.OpenTime == minute), "the candle in the database");
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct); // both writers' batch windows (100 ms) have passed
        var stored = (await CandlesAsync(Btc)).Where(c => c.OpenTime == minute).ToList();
        Assert.Single(stored);
        Assert.Equal(1.5m, stored[0].Close);

        var (firstStatus, _) = await first.GetReadinessAsync(Ct);
        var (secondStatus, _) = await second.GetReadinessAsync(Ct);
        Assert.Equal(200, firstStatus);
        Assert.Equal(200, secondStatus);
    }
}
