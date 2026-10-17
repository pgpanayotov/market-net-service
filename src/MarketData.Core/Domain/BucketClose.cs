namespace MarketData.Core.Domain;

/// <summary>Close of the last 1-minute candle in a bucket.</summary>
public readonly record struct BucketClose(DateTimeOffset BucketStart, decimal Close);
