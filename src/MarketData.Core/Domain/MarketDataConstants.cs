namespace MarketData.Core.Domain;

public static class MarketDataConstants
{
    /// <summary>Length of every stored candle (ADR-001).</summary>
    public static readonly TimeSpan CandleInterval = TimeSpan.FromMinutes(1);

    /// <summary>Window of the 24h average endpoint.</summary>
    public static readonly TimeSpan AverageWindow = TimeSpan.FromHours(24);

    /// <summary>Decimal places of computed averages; matches the <c>numeric(28,10)</c> price columns.</summary>
    public const int PricePrecision = 10;
}
