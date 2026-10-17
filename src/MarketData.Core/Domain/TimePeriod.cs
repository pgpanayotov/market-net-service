namespace MarketData.Core.Domain;

/// <summary>
/// One of the SMA bucket sizes accepted by the API: <c>1m</c>, <c>5m</c>, <c>30m</c>, <c>1d</c>, <c>1w</c>.
/// </summary>
/// <remarks>
/// Codes are case-sensitive. <c>1M</c> conventionally means one month (Binance intervals), so it is
/// rejected rather than read as one minute.
/// </remarks>
public readonly record struct TimePeriod
{
    /// <summary>
    /// Origin for bucket alignment: Monday 2000-01-03 00:00 UTC. Every period divides a week evenly,
    /// so minute and day buckets align on UTC boundaries and week buckets start on Monday, as Binance
    /// <c>1w</c> klines do. This must match the <c>date_bin</c> origin in the SQL rollup.
    /// </summary>
    public static readonly DateTimeOffset BucketOrigin = new(2000, 1, 3, 0, 0, 0, TimeSpan.Zero);

    public static readonly TimePeriod OneMinute = new("1m", TimeSpan.FromMinutes(1));
    public static readonly TimePeriod FiveMinutes = new("5m", TimeSpan.FromMinutes(5));
    public static readonly TimePeriod ThirtyMinutes = new("30m", TimeSpan.FromMinutes(30));
    public static readonly TimePeriod OneDay = new("1d", TimeSpan.FromDays(1));
    public static readonly TimePeriod OneWeek = new("1w", TimeSpan.FromDays(7));

    public static IReadOnlyList<TimePeriod> All { get; } = [OneMinute, FiveMinutes, ThirtyMinutes, OneDay, OneWeek];

    private TimePeriod(string code, TimeSpan duration)
    {
        Code = code;
        Duration = duration;
    }

    public string Code { get; }

    public TimeSpan Duration { get; }

    public static bool TryParse(string? value, out TimePeriod period)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Code, value?.Trim(), StringComparison.Ordinal))
            {
                period = candidate;
                return true;
            }
        }

        period = default;
        return false;
    }

    public static TimePeriod Parse(string value) =>
        TryParse(value, out var period)
            ? period
            : throw new FormatException($"'{value}' is not a supported period. Use one of: {string.Join(", ", All)}.");

    /// <summary>Start of the bucket that contains <paramref name="time"/>, in UTC.</summary>
    public DateTimeOffset Floor(DateTimeOffset time)
    {
        if (Duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Cannot floor with an uninitialized TimePeriod.");
        }

        var remainder = (time.UtcTicks - BucketOrigin.UtcTicks) % Duration.Ticks;
        if (remainder < 0)
        {
            remainder += Duration.Ticks;
        }

        return new DateTimeOffset(time.UtcTicks - remainder, TimeSpan.Zero);
    }

    /// <summary>Total length of <paramref name="count"/> consecutive buckets.</summary>
    public TimeSpan Times(int count) => TimeSpan.FromTicks(Duration.Ticks * count);

    public override string ToString() => Code ?? string.Empty;
}
