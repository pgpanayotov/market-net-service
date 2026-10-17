namespace MarketData.Core.Domain;

/// <summary>Aggregate of the 1-minute closes in a time range.</summary>
public readonly record struct CloseAverage(decimal Average, int Count, DateTimeOffset FirstOpenTime);
