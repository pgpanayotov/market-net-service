using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Infrastructure.Persistence;
using MarketData.Ingestion.IntegrationTests.Fakes;

namespace MarketData.Ingestion.IntegrationTests.Worker;

public sealed class IngestionPipelineTests(PostgresFixture postgres) : WorkerTest(postgres)
{
    private static readonly string[] Seeded = ["ADAUSDT", "BTCUSDT", "ETHUSDT"];

    [Fact]
    public async Task Worker_subscribes_active_symbols_and_persists_streamed_candles()
    {
        var worker = StartWorker(configure: w => w.Rest.HasHistory = false);
        await EventuallyAsync(() => worker.Stream.SubscribedNames.SetEquals(Seeded), "subscriptions to the seeded symbols");

        var minute = ThisMinute.AddMinutes(-1);
        var btc = new Candle(Btc, minute, 106950.01m, 106970m, 106940m, 106960.5m, 12.34567m, 1320456.78901234m, 1243, DateTimeOffset.UtcNow);
        var ada = new Candle(Ada, minute, 0.6345m, 0.6351m, 0.6340m, 0.63487654m, 81234.5m, 51577.12345678m, 310, DateTimeOffset.UtcNow);
        worker.Stream.Emit(btc);
        worker.Stream.Emit(ada);

        await EventuallyAsync(async () => (await CandlesAsync(Btc)).Count == 1 && (await CandlesAsync(Ada)).Count == 1, "streamed candles in the database");
        Assert.Equal(Values(btc), Values((await CandlesAsync(Btc)).Single()));
        Assert.Equal(Values(ada), Values((await CandlesAsync(Ada)).Single()));
        Assert.Equal(btc.ReceivedAt, (await CandlesAsync(Btc)).Single().ReceivedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Symbols_without_data_get_a_capped_startup_backfill()
    {
        var worker = StartWorker();

        await EventuallyAsync(async () => (await CandlesAsync(Eth)).Count >= 30, "30 minutes of ETH history");

        var rows = await CandlesAsync(Eth);
        Assert.Equal(ThisMinute.AddMinutes(-30), rows[0].OpenTime, TimeSpan.FromMinutes(1));
        Assert.All(rows, r => Assert.Equal(Values(StubKlineRestClient.Synthetic(Eth, r.OpenTime)), Values(r)));
    }

    [Fact]
    public async Task Symbol_with_only_recent_data_gets_its_head_filled_back_to_the_history_depth()
    {
        var first = ThisMinute.AddMinutes(-10);
        await SeedAsync(Enumerable.Range(0, 9).Select(i => StubKlineRestClient.Synthetic(Btc, first.AddMinutes(i))));

        var worker = StartWorker();

        await EventuallyAsync(async () => (await CandlesAsync(Btc)).Count >= 30, "BTC deepened back to the 30 minute history depth");
        var rows = await CandlesAsync(Btc);
        Assert.Equal(ThisMinute.AddMinutes(-30), rows[0].OpenTime, TimeSpan.FromMinutes(1));
        Assert.All(rows, r => Assert.Equal(Values(StubKlineRestClient.Synthetic(Btc, r.OpenTime)), Values(r)));
        Assert.Contains(worker.Rest.Requests, r => r.Symbol == "BTCUSDT" && r.Range.To == first && Math.Abs((r.Range.From - ThisMinute.AddMinutes(-30)).TotalMinutes) <= 1);
    }

    [Fact]
    public async Task Nothing_is_refetched_when_the_history_depth_is_already_satisfied()
    {
        var first = ThisMinute.AddMinutes(-40);
        await SeedAsync(Enumerable.Range(0, 39).Select(i => StubKlineRestClient.Synthetic(Btc, first.AddMinutes(i))));

        var worker = StartWorker();

        // ADA and ETH have no data, so their depth fills prove the whole startup pass ran.
        await EventuallyAsync(async () => (await CandlesAsync(Ada)).Count >= 30 && (await CandlesAsync(Eth)).Count >= 30, "the other symbols filled");
        await Task.Delay(500, Ct);
        Assert.DoesNotContain(worker.Rest.Requests, r => r.Symbol == "BTCUSDT" && r.Range.From < first.AddMinutes(39));
        Assert.Equal(first, (await CandlesAsync(Btc))[0].OpenTime);
    }

    [Fact]
    public async Task Gap_scan_repairs_a_hole_behind_the_newest_candle()
    {
        var start = ThisMinute.AddMinutes(-40);
        var holeStart = start.AddMinutes(15);
        var holeEnd = holeStart.AddMinutes(5);
        var series = Enumerable.Range(0, 40).Select(i => start.AddMinutes(i)).ToList();
        await SeedAsync(series.Where(t => t < holeStart || t >= holeEnd).Select(t => StubKlineRestClient.Synthetic(Btc, t)));

        var worker = StartWorker();

        // A tail backfill starts after the newest candle, so only the scan can fetch exactly the hole.
        await EventuallyAsync(() => worker.Rest.Requests.Any(r => r.Symbol == "BTCUSDT" && r.Range == new TimeRange(holeStart, holeEnd)), "a REST request for the hole");
        await EventuallyAsync(async () => (await CandlesAsync(Btc)).Count(c => c.OpenTime >= holeStart && c.OpenTime < holeEnd) == 5, "the hole filled");

        await using var db = Postgres.CreateContext();
        var missing = await new CandleRepository(db).FindMissingRangesAsync(Btc, start, ThisMinute.AddMinutes(-1), Ct);
        Assert.Empty(missing);
        var repaired = (await CandlesAsync(Btc)).Where(c => c.OpenTime >= holeStart && c.OpenTime < holeEnd);
        Assert.All(repaired, r => Assert.Equal(Values(StubKlineRestClient.Synthetic(Btc, r.OpenTime)), Values(r)));
    }

    [Fact]
    public async Task Reconnect_triggers_a_tail_backfill()
    {
        var worker = StartWorker();
        await EventuallyAsync(async () => (await CandlesAsync(Btc)).Count >= 30, "startup backfill");
        var requestsBefore = worker.Rest.Requests.Count;

        await using (var db = Postgres.CreateContext())
        {
            // Lose the newest few minutes, as if the socket had been down.
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlAsync(
                db.Database,
                $"DELETE FROM candles_1m WHERE symbol_id = {Btc} AND open_time >= {ThisMinute.AddMinutes(-5)}",
                Ct);
        }

        worker.Stream.Disconnect();
        Assert.False(worker.Stream.IsConnected);
        worker.Stream.Reconnect();

        await EventuallyAsync(() => worker.Rest.Requests.Skip(requestsBefore).Any(r => r.Symbol == "BTCUSDT"), "a tail request after the reconnect");
        await EventuallyAsync(async () => (await CandlesAsync(Btc)).Any(c => c.OpenTime >= ThisMinute.AddMinutes(-2)), "the newest minutes restored");
    }

    [Fact]
    public async Task Readiness_reports_the_checks_and_fails_while_the_stream_is_down()
    {
        var worker = StartWorker();
        await EventuallyAsync(async () => (await worker.GetReadinessAsync(Ct)).StatusCode == 200, "a ready worker");

        var (_, ready) = await worker.GetReadinessAsync(Ct);
        Assert.Equal(["database", "schema", "ingestion"], ready.Checks.Select(c => c.Name));

        worker.Stream.Disconnect();
        var (status, body) = await worker.GetReadinessAsync(Ct);
        Assert.Equal(503, status);
        Assert.Contains("WebSocket disconnected", body.Checks.Single(c => c.Name == "ingestion").Description, StringComparison.Ordinal);

        worker.Stream.Reconnect();
        await EventuallyAsync(async () => (await worker.GetReadinessAsync(Ct)).StatusCode == 200, "ready again after the reconnect");

        using var client = worker.CreateClient();
        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);
        Assert.Equal(200, (int)live.StatusCode);
    }

    [Fact]
    public async Task Readiness_fails_when_an_active_symbol_is_stale()
    {
        var worker = StartWorker(
            new Dictionary<string, string> { ["MarketData:FreshnessThreshold"] = "00:00:01" },
            w => w.Rest.HasHistory = false);
        await EventuallyAsync(() => worker.Stream.SubscribedNames.Count == 3, "ingestion started");

        await EventuallyAsync(
            async () =>
            {
                var (status, body) = await worker.GetReadinessAsync(Ct);
                return status == 503 && body.Checks.Single(c => c.Name == "ingestion").Description!.Contains("BTCUSDT", StringComparison.Ordinal);
            },
            "readiness failing for symbols without fresh candles");
    }
}
