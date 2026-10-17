using System.Collections.Concurrent;

using MarketData.Core.Domain;
using MarketData.Infrastructure.Binance;

namespace MarketData.Ingestion.IntegrationTests.Fakes;

/// <summary>An in-memory <see cref="IMarketDataStream"/>: tests see the subscriptions and push candles and connection events.</summary>
public sealed class FakeMarketDataStream : IMarketDataStream
{
    private readonly ConcurrentDictionary<int, SymbolInfo> _subscriptions = new();
    private volatile bool _connected = true;

    public event EventHandler<Candle>? CandleClosed;

    public event EventHandler? ConnectionLost;

    public event EventHandler? ConnectionRestored;

    public bool IsConnected => _connected;

    public IReadOnlyCollection<SymbolInfo> Subscriptions => [.. _subscriptions.Values];

    public IReadOnlySet<string> SubscribedNames => _subscriptions.Values.Select(s => s.Name.Value).ToHashSet();

    public Task<bool> SubscribeAsync(SymbolInfo symbol, CancellationToken cancellationToken) => Task.FromResult(_subscriptions.TryAdd(symbol.Id, symbol));

    public Task<bool> UnsubscribeAsync(int symbolId, CancellationToken cancellationToken) => Task.FromResult(_subscriptions.TryRemove(symbolId, out _));

    public Task UnsubscribeAllAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    /// <summary>Delivers a closed candle as the Binance callback would.</summary>
    public void Emit(Candle candle) => CandleClosed?.Invoke(this, candle);

    public void Disconnect()
    {
        _connected = false;
        ConnectionLost?.Invoke(this, EventArgs.Empty);
    }

    public void Reconnect()
    {
        _connected = true;
        ConnectionRestored?.Invoke(this, EventArgs.Empty);
    }
}
