using MarketData.Core.Domain;
using static MarketData.Core.Tests.TestTime;

namespace MarketData.Core.Tests.Domain;

public class TimePeriodTests
{
    [Theory]
    [InlineData("1m", 1)]
    [InlineData("5m", 5)]
    [InlineData("30m", 30)]
    [InlineData("1d", 1440)]
    [InlineData("1w", 10080)]
    [InlineData(" 5m ", 5)]
    public void TryParse_AcceptsSupportedCodes(string input, int expectedMinutes)
    {
        Assert.True(TimePeriod.TryParse(input, out var period));
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), period.Duration);
        Assert.Equal(input.Trim(), period.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1h")]
    [InlineData("2m")]
    [InlineData("1M")]
    [InlineData("1W")]
    public void TryParse_RejectsUnsupportedCodes(string? input)
    {
        Assert.False(TimePeriod.TryParse(input, out var period));
        Assert.Equal(default, period);
    }

    [Fact]
    public void Parse_ThrowsFormatExceptionListingSupportedCodes()
    {
        var ex = Assert.Throws<FormatException>(() => TimePeriod.Parse("1h"));
        Assert.Contains("1m, 5m, 30m, 1d, 1w", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReturnsKnownInstance()
    {
        Assert.Equal(TimePeriod.OneWeek, TimePeriod.Parse("1w"));
    }

    [Theory]
    [InlineData("1m", "2026-10-18T12:07:42Z", "2026-10-18T12:07:00Z")]
    [InlineData("5m", "2026-10-18T12:07:00Z", "2026-10-18T12:05:00Z")]
    [InlineData("5m", "2026-10-18T12:05:00Z", "2026-10-18T12:05:00Z")]
    [InlineData("30m", "2026-10-18T12:59:59.999Z", "2026-10-18T12:30:00Z")]
    [InlineData("1d", "2026-10-18T23:59:00Z", "2026-10-18T00:00:00Z")]
    // 2026-10-18 is a Sunday: its week started on Monday 2026-10-12.
    [InlineData("1w", "2026-10-18T23:59:00Z", "2026-10-12T00:00:00Z")]
    [InlineData("1w", "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z")]
    // Before the origin the remainder is negative and must still floor downwards.
    [InlineData("1w", "1999-12-31T10:00:00Z", "1999-12-27T00:00:00Z")]
    [InlineData("5m", "1999-12-31T23:59:59Z", "1999-12-31T23:55:00Z")]
    public void Floor_AlignsToUtcBucketStart(string code, string time, string expected)
    {
        Assert.Equal(At(expected), TimePeriod.Parse(code).Floor(At(time)));
    }

    [Fact]
    public void Floor_ConvertsNonUtcOffsetsToUtc()
    {
        var local = new DateTimeOffset(2026, 10, 18, 14, 7, 0, TimeSpan.FromHours(2));

        var floored = TimePeriod.OneDay.Floor(local);

        Assert.Equal(At("2026-10-18T00:00:00Z"), floored);
        Assert.Equal(TimeSpan.Zero, floored.Offset);
    }

    [Fact]
    public void Floor_OnDefaultInstance_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => default(TimePeriod).Floor(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Times_MultipliesDuration()
    {
        Assert.Equal(TimeSpan.FromDays(7 * 1000), TimePeriod.OneWeek.Times(1000));
    }

    [Fact]
    public void ToString_ReturnsCode_AndEmptyForDefault()
    {
        Assert.Equal("30m", TimePeriod.ThirtyMinutes.ToString());
        Assert.Equal(string.Empty, default(TimePeriod).ToString());
    }
}
