using MarketData.Core.Domain;
using MarketData.Infrastructure;
using MarketData.Ingestion.Backfill;

namespace MarketData.Ingestion.IntegrationTests.Unit;

public sealed class ExponentialBackoffTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(0, 500)]
    [InlineData(1, 1000)]
    [InlineData(3, 4000)]
    [InlineData(6, 30_000)]
    [InlineData(1000, 30_000)]
    public void Doubles_per_attempt_up_to_the_cap(int attempt, double expectedMs)
    {
        Assert.Equal(expectedMs, ExponentialBackoff.Delay(attempt, Initial, Max, jitter: 0.2, sample: 0.5).TotalMilliseconds, precision: 6);
    }

    [Theory]
    [InlineData(0.0, 0.8)]
    [InlineData(0.999999, 1.2)]
    public void Jitter_spreads_the_delay_by_the_given_fraction(double sample, double factor)
    {
        var delay = ExponentialBackoff.Delay(2, Initial, Max, jitter: 0.2, sample: sample);

        Assert.Equal(2000 * factor, delay.TotalMilliseconds, precision: 2);
    }

    [Fact]
    public void Random_jitter_stays_within_bounds()
    {
        for (var i = 0; i < 1000; i++)
        {
            var ms = ExponentialBackoff.Delay(10, Initial, Max).TotalMilliseconds;
            Assert.InRange(ms, 24_000, 36_000);
        }
    }
}

public sealed class BackfillSchedulerTests
{
    private static readonly SymbolInfo Sol = new(100, SymbolName.TryCreate("SOLUSDT", out var s) ? s : default, IsActive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Repeated_requests_coalesce_until_taken()
    {
        var scheduler = new BackfillScheduler();
        scheduler.RequestScan(BackfillReason.Dropped);
        scheduler.RequestScan(BackfillReason.Dropped);
        scheduler.RequestScan(BackfillReason.DbRecovered);
        scheduler.RequestTail(BackfillReason.Reconnect);
        scheduler.RequestTail(BackfillReason.Reconnect);

        var taken = scheduler.TakePending();

        Assert.Equal(2, taken.Count);
        Assert.Equal(BackfillReason.Dropped, taken.Single(r => r.Kind == BackfillKind.Scan).Reason);
        Assert.Empty(scheduler.TakePending());
    }

    [Fact]
    public void Symbol_tails_run_before_all_symbol_tails_and_scans()
    {
        var scheduler = new BackfillScheduler();
        scheduler.RequestScan(BackfillReason.Startup);
        scheduler.RequestTail(BackfillReason.Startup);
        scheduler.RequestTail(BackfillReason.SymbolAdded, Sol);

        var taken = scheduler.TakePending();

        Assert.Equal(
            [(BackfillKind.Tail, (int?)100), (BackfillKind.Tail, null), (BackfillKind.Scan, null)],
            taken.Select(r => (r.Kind, r.Symbol?.Id)));
    }

    [Fact]
    public async Task Wait_returns_when_requested_or_after_the_timeout()
    {
        var scheduler = new BackfillScheduler();

        Assert.False(await scheduler.WaitAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct));

        scheduler.RequestScan(BackfillReason.Periodic);
        Assert.True(await scheduler.WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System, Ct));

        scheduler.TakePending();
        Assert.False(await scheduler.WaitAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct));
    }
}
