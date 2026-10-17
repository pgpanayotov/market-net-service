using MarketData.Core.Domain;

namespace MarketData.Core.Analysis;

/// <summary>One series value; <see cref="Average"/> is <c>null</c> (and the data point count 0) for a window without data.</summary>
public readonly record struct SmaValue(DateTimeOffset Time, decimal? Average, int DataPointsUsed);

/// <summary>Simple moving average over bucket closes (ADR-006).</summary>
public static class SmaCalculator
{
    /// <summary>Mean of all closes; <c>null</c> when there are none.</summary>
    public static decimal? Average(IReadOnlyCollection<BucketClose> buckets)
    {
        if (buckets.Count == 0)
        {
            return null;
        }

        var sum = 0m;
        foreach (var bucket in buckets)
        {
            sum += bucket.Close;
        }

        return Round(sum / buckets.Count);
    }

    /// <summary>
    /// Rolling SMA(<paramref name="n"/>) with <paramref name="count"/> values. Value <c>k</c> covers the
    /// buckets starting in [E<sub>k</sub> − n·p, E<sub>k</sub>), where E<sub>k</sub> =
    /// <paramref name="referenceTime"/> − (count − 1 − k)·p. Windows are defined by time, so gaps reduce
    /// <see cref="SmaValue.DataPointsUsed"/> rather than pulling in older buckets. Windows with no data
    /// are kept as items with <c>null</c> average and 0 data points, so exactly <paramref name="count"/> values come back, oldest first. Runs in O(buckets + count) using a sliding sum, which is exact because
    /// <see cref="decimal"/> addition and subtraction don't round at these magnitudes.
    /// </summary>
    public static IReadOnlyList<SmaValue> Series(
        IEnumerable<BucketClose> buckets,
        TimePeriod period,
        int n,
        int count,
        DateTimeOffset referenceTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var sorted = buckets.OrderBy(b => b.BucketStart).ToArray();
        var window = period.Times(n);
        var result = new List<SmaValue>(count);
        var sum = 0m;
        int lo = 0, hi = 0;

        for (var k = 0; k < count; k++)
        {
            var end = referenceTime - period.Times(count - 1 - k);
            var start = end - window;

            for (; hi < sorted.Length && sorted[hi].BucketStart < end; hi++)
            {
                sum += sorted[hi].Close;
            }

            for (; lo < hi && sorted[lo].BucketStart < start; lo++)
            {
                sum -= sorted[lo].Close;
            }

            var used = hi - lo;
            result.Add(used > 0 ? new SmaValue(end, Round(sum / used), used) : new SmaValue(end, null, 0));
        }

        return result;
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, MarketDataConstants.PricePrecision, MidpointRounding.ToEven);
}
