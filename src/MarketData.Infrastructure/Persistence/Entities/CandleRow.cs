namespace MarketData.Infrastructure.Persistence.Entities;

/// <summary>A row of <c>candles_1m</c>. Mapping: <see cref="Configurations.CandleRowConfiguration"/>.</summary>
public sealed class CandleRow
{
    public int SymbolId { get; set; }

    public DateTimeOffset OpenTime { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public decimal Volume { get; set; }

    public decimal QuoteVolume { get; set; }

    public int TradeCount { get; set; }

    /// <summary>When the service received the candle; used for the ingest-lag metric.</summary>
    public DateTimeOffset ReceivedAt { get; set; }
}
