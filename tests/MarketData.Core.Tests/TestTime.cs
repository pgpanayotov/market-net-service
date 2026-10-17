using System.Globalization;

namespace MarketData.Core.Tests;

internal static class TestTime
{
    public static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
