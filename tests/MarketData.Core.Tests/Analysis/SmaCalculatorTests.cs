using MarketData.Core.Abstractions;
using MarketData.Core.Analysis;
using MarketData.Core.Domain;
using static MarketData.Core.Tests.TestTime;

namespace MarketData.Core.Tests.Analysis;

public class SmaCalculatorTests
{
    private static readonly DateTimeOffset Ref = At("2026-10-18T12:05:00Z");

    /// <summary>5-minute buckets ending at <see cref="Ref"/>: closes[0] is the oldest.</summary>
    private static List<BucketClose> FiveMinuteBuckets(params decimal[] closes) =>
        [.. closes.Select((c, i) => new BucketClose(Ref - TimePeriod.FiveMinutes.Times(closes.Length - i), c))];

    [Fact]
    public void Average_OfNoBuckets_IsNull()
    {
        Assert.Null(SmaCalculator.Average([]));
    }

    [Fact]
    public void Average_IsArithmeticMean()
    {
        Assert.Equal(20m, SmaCalculator.Average(FiveMinuteBuckets(10m, 20m, 30m)));
    }

    [Fact]
    public void Average_RoundsToPricePrecision()
    {
        Assert.Equal(0.3333333333m, SmaCalculator.Average(FiveMinuteBuckets(0m, 0m, 1m)));
    }

    [Fact]
    public void Series_FullHistory_SlidesOneBucketAtATime()
    {
        // n=3, count=3 → 5 buckets: 1 2 3 4 5
        var series = SmaCalculator.Series(FiveMinuteBuckets(1m, 2m, 3m, 4m, 5m), TimePeriod.FiveMinutes, n: 3, count: 3, Ref);

        Assert.Equal(
            [
                new SmaValue(Ref - TimeSpan.FromMinutes(10), 2m, 3),
                new SmaValue(Ref - TimeSpan.FromMinutes(5), 3m, 3),
                new SmaValue(Ref, 4m, 3),
            ],
            series);
    }

    [Fact]
    public void Series_LastValue_EqualsSingleValueAverageOfLastNBuckets()
    {
        var buckets = FiveMinuteBuckets(7m, 1m, 9m, 4m, 6m, 2m);

        var series = SmaCalculator.Series(buckets, TimePeriod.FiveMinutes, n: 4, count: 3, Ref);

        Assert.Equal(SmaCalculator.Average(buckets[^4..]), series[^1].Average);
    }

    [Fact]
    public void Series_AtStartOfHistory_EmitsPartialWindows()
    {
        // Only 2 buckets exist; n=3, count=3.
        var series = SmaCalculator.Series(FiveMinuteBuckets(10m, 20m), TimePeriod.FiveMinutes, n: 3, count: 3, Ref);

        Assert.Equal(
            [
                new SmaValue(Ref - TimeSpan.FromMinutes(5), 10m, 1),
                new SmaValue(Ref, 15m, 2),
            ],
            series);
    }

    [Fact]
    public void Series_WithGap_UsesTimeWindowsNotOlderBuckets()
    {
        var buckets = FiveMinuteBuckets(1m, 2m, 3m, 4m, 5m);
        buckets.RemoveAt(3); // remove close 4 (bucket ending at Ref - 5m)

        var series = SmaCalculator.Series(buckets, TimePeriod.FiveMinutes, n: 2, count: 2, Ref);

        Assert.Equal(
            [
                new SmaValue(Ref - TimeSpan.FromMinutes(5), 3m, 1),
                new SmaValue(Ref, 5m, 1),
            ],
            series);
    }

    [Fact]
    public void Series_SkipsWindowsWithNoData()
    {
        // Single bucket far in the past relative to the windows.
        var buckets = new[] { new BucketClose(Ref - TimeSpan.FromHours(5), 1m) };

        Assert.Empty(SmaCalculator.Series(buckets, TimePeriod.FiveMinutes, n: 2, count: 3, Ref));
    }

    [Fact]
    public void Series_IgnoresBucketsAtOrAfterReferenceTime_AndAcceptsUnsortedInput()
    {
        var buckets = FiveMinuteBuckets(1m, 2m, 3m);
        buckets.Add(new BucketClose(Ref, 1000m));
        buckets.Reverse();

        var series = SmaCalculator.Series(buckets, TimePeriod.FiveMinutes, n: 3, count: 1, Ref);

        Assert.Equal([new SmaValue(Ref, 2m, 3)], series);
    }

    [Fact]
    public void Series_SlidingSumIsExact()
    {
        // Prices with many decimals: the sliding result must equal a fresh sum per window.
        var closes = Enumerable.Range(1, 50).Select(i => 0.12345678m * i + 67000.0000000001m).ToArray();
        var buckets = FiveMinuteBuckets(closes);

        var series = SmaCalculator.Series(buckets, TimePeriod.FiveMinutes, n: 10, count: 41, Ref);

        Assert.Equal(41, series.Count);
        for (var k = 0; k < series.Count; k++)
        {
            Assert.Equal(SmaCalculator.Average(buckets.GetRange(k, 10)), series[k].Average);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Series_RejectsNonPositiveArguments(int n, int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SmaCalculator.Series([], TimePeriod.OneMinute, n, count, Ref));
    }
}
