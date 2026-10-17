using System.Diagnostics;
using MarketData.Core.Domain;
using MarketData.Infrastructure.Persistence;
using MarketData.Infrastructure.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

namespace MarketData.Ingestion.IntegrationTests.Worker;

/// <summary>Base class: resets the shared database before each test and disposes the workers it started.</summary>
public abstract class WorkerTest(PostgresFixture postgres) : IAsyncLifetime
{
    protected const int Btc = 1;
    protected const int Ada = 2;
    protected const int Eth = 3;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly List<WorkerFactory> _workers = [];

    protected PostgresFixture Postgres { get; } = postgres;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The start of the current minute, UTC.</summary>
    protected static DateTimeOffset ThisMinute
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
        }
    }

    public async ValueTask InitializeAsync() => await Postgres.ResetAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers)
        {
            await worker.DisposeAsync();
        }
    }

    protected WorkerFactory StartWorker(IReadOnlyDictionary<string, string>? settings = null)
    {
        var worker = new WorkerFactory(Postgres.ConnectionString, settings);
        _workers.Add(worker);
        return worker.Start();
    }

    /// <summary>Polls <paramref name="condition"/> until it holds; fails with <paramref name="what"/> after the timeout.</summary>
    protected static async Task EventuallyAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > (timeout ?? DefaultTimeout))
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(50, Ct);
        }
    }

    protected static Task EventuallyAsync(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        EventuallyAsync(() => Task.FromResult(condition()), what, timeout);

    protected async Task<List<CandleRow>> CandlesAsync(int symbolId)
    {
        await using var db = Postgres.CreateContext();
        return await db.Candles.AsNoTracking().Where(c => c.SymbolId == symbolId).OrderBy(c => c.OpenTime).ToListAsync(Ct);
    }

    protected async Task SeedAsync(IEnumerable<Candle> candles)
    {
        await using var db = Postgres.CreateContext();
        await new CandleRepository(db).UpsertAsync([.. candles], Ct);
    }

    protected static object Values(CandleRow c) => new { c.SymbolId, c.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume, c.QuoteVolume, c.TradeCount };

    protected static object Values(Candle c) => new { c.SymbolId, c.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume, c.QuoteVolume, c.TradeCount };
}
