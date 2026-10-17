namespace MarketData.Core.Domain;

/// <summary>A closed 1-minute kline (ADR-001). Produced by the WebSocket feed and by REST backfill alike.</summary>
public sealed record Candle(
    int SymbolId,
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal QuoteVolume,
    int TradeCount,
    DateTimeOffset ReceivedAt);

/// <summary>Half-open time range [<see cref="From"/>, <see cref="To"/>).</summary>
public readonly record struct TimeRange(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Length => To - From;
}
