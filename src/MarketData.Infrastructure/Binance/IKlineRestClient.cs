using MarketData.Core.Domain;

namespace MarketData.Infrastructure.Binance;

/// <summary>Historical 1-minute klines from REST <c>/api/v3/klines</c>, for tail backfill and gap repair (ADR-005).</summary>
public interface IKlineRestClient
{
    /// <summary>
    /// Closed candles with open time in <paramref name="range"/>, oldest first, one page (at most
    /// <see cref="BinanceKlineRestClient.MaxKlinesPerRequest"/>) per request. The current, still open
    /// minute is never returned. Minutes the exchange has no kline for are skipped.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<Candle>> GetClosedCandlesAsync(SymbolInfo symbol, TimeRange range, CancellationToken cancellationToken);
}

/// <summary>A kline request failed permanently, or kept failing after the allowed retries.</summary>
public sealed class KlineRequestException : Exception
{
    public KlineRequestException()
    {
    }

    public KlineRequestException(string message)
        : base(message)
    {
    }

    public KlineRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
