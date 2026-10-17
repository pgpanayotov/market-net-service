using MarketData.Core.Abstractions;
using MarketData.Core.Domain;

namespace MarketData.Infrastructure.Binance;

/// <summary>
/// Live closed 1-minute candles, one subscription per symbol over the combined stream. Subscribing and
/// unsubscribing affect only that symbol; other streams keep running.
/// </summary>
public interface IMarketDataStream
{
    /// <summary>A closed 1-minute candle arrived. Raised on the socket thread: handlers must not block or do I/O.</summary>
    event EventHandler<Candle>? CandleClosed;

    /// <summary>A socket carrying subscriptions lost its connection. The client reconnects by itself.</summary>
    event EventHandler? ConnectionLost;

    /// <summary>A lost socket is connected again and its subscriptions are restored. Candles closed in between were missed.</summary>
    event EventHandler? ConnectionRestored;

    /// <summary><c>true</c> when every subscription's socket is connected (also when there are none).</summary>
    bool IsConnected { get; }

    /// <summary>Symbols with a live subscription.</summary>
    IReadOnlyCollection<SymbolInfo> Subscriptions { get; }

    /// <summary>Subscribes to the symbol's <c>@kline_1m</c> stream. Returns <c>false</c> when it is already subscribed.</summary>
    /// <exception cref="MarketDataStreamException">The subscription was rejected or could not be sent.</exception>
    Task<bool> SubscribeAsync(SymbolInfo symbol, CancellationToken cancellationToken);

    /// <summary>Closes the symbol's subscription. Returns <c>false</c> when it was not subscribed.</summary>
    Task<bool> UnsubscribeAsync(int symbolId, CancellationToken cancellationToken);

    Task UnsubscribeAllAsync(CancellationToken cancellationToken);
}

public sealed class MarketDataStreamException : Exception
{
    public MarketDataStreamException()
    {
    }

    public MarketDataStreamException(string message)
        : base(message)
    {
    }

    public MarketDataStreamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
