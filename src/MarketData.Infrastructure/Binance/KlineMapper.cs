using System.Diagnostics.CodeAnalysis;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using MarketData.Core.Domain;

namespace MarketData.Infrastructure.Binance;

/// <summary>
/// The one mapping from a Binance kline to a <see cref="Candle"/>, shared by the WebSocket feed and REST
/// backfill. Both paths upsert the same rows (ADR-005), so they must produce identical values.
/// </summary>
public static class KlineMapper
{
    public static Candle ToCandle(int symbolId, IBinanceKline kline, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(kline);
        return new Candle(
            symbolId,
            AsUtc(kline.OpenTime),
            kline.OpenPrice,
            kline.HighPrice,
            kline.LowPrice,
            kline.ClosePrice,
            kline.Volume,
            kline.QuoteVolume,
            kline.TradeCount,
            receivedAt.ToUniversalTime());
    }

    /// <summary>
    /// Maps a streamed kline only when it is a closed (<c>x == true</c>) 1-minute candle. Open klines are
    /// interim updates sent about every 2s and are never stored (ADR-001).
    /// </summary>
    public static bool TryMapClosed(int symbolId, IBinanceStreamKline kline, DateTimeOffset receivedAt, [NotNullWhen(true)] out Candle? candle)
    {
        ArgumentNullException.ThrowIfNull(kline);
        if (!kline.Final || kline.Interval != KlineInterval.OneMinute)
        {
            candle = null;
            return false;
        }

        candle = ToCandle(symbolId, kline, receivedAt);
        return true;
    }

    /// <summary>Binance.Net returns UTC <see cref="DateTime"/>s; the kind is forced so a missing kind can't shift the time.</summary>
    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
