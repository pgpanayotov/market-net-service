namespace MarketData.Infrastructure;

/// <summary>Retry delays: <c>initial · 2^attempt</c>, capped at <c>max</c>, with ±<c>jitter</c> spread so retries don't align.</summary>
public static class ExponentialBackoff
{
    /// <param name="attempt">0 for the first retry.</param>
    /// <param name="jitter">Relative spread, e.g. 0.2 for ±20%.</param>
    /// <param name="sample">A value in [0, 1) choosing where in the spread the delay falls; random when omitted.</param>
    public static TimeSpan Delay(int attempt, TimeSpan initial, TimeSpan max, double jitter = 0.2, double? sample = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        ArgumentOutOfRangeException.ThrowIfNegative(jitter);
        var exponential = initial.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 30));
        var capped = Math.Min(exponential, max.TotalMilliseconds);
#pragma warning disable CA5394 // Jitter, not security.
        var s = sample ?? Random.Shared.NextDouble();
#pragma warning restore CA5394
        return TimeSpan.FromMilliseconds(capped * (1 - jitter + (2 * jitter * s)));
    }
}
