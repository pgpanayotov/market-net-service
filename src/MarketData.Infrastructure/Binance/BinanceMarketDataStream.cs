using System.Collections.Concurrent;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Objects.Sockets;
using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using Microsoft.Extensions.Logging;

namespace MarketData.Infrastructure.Binance;

/// <summary>
/// <see cref="IMarketDataStream"/> on Binance.Net. Each symbol is its own <c>&lt;symbol&gt;@kline_1m</c>
/// subscription; Binance.Net multiplexes them over combined-stream sockets and reconnects (including the
/// 24h forced close) and resubscribes by itself.
/// </summary>
public sealed partial class BinanceMarketDataStream : IMarketDataStream, IAsyncDisposable
{
    private readonly IBinanceSocketClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BinanceMarketDataStream> _logger;
    private readonly ConcurrentDictionary<int, Entry> _entries = new();
    private readonly HashSet<int> _lostSockets = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Binance.Net closes a subscription when the token passed to Subscribe is cancelled, so subscriptions
    // are tied to this object's lifetime rather than to the caller's token.
    private readonly CancellationTokenSource _lifetime = new();

    public BinanceMarketDataStream(IBinanceSocketClient client, TimeProvider timeProvider, ILogger<BinanceMarketDataStream> logger)
    {
        _client = client;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public event EventHandler<Candle>? CandleClosed;

    public event EventHandler? ConnectionLost;

    public event EventHandler? ConnectionRestored;

    public bool IsConnected => _entries.Values.All(e => e.Connected);

    public IReadOnlyCollection<SymbolInfo> Subscriptions => [.. _entries.Values.Select(e => e.Symbol)];

    public async Task<bool> SubscribeAsync(SymbolInfo symbol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.ContainsKey(symbol.Id))
            {
                return false;
            }

            var result = await _client.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(
                    symbol.Name.Value,
                    KlineInterval.OneMinute,
                    update => OnKline(symbol.Id, update),
                    _lifetime.Token)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                throw new MarketDataStreamException($"Subscribing to {symbol.Name}@kline_1m failed: {result.Error}");
            }

            var entry = new Entry(symbol, result.Data);
            result.Data.ConnectionLost += () => OnConnectionLost(entry);
            result.Data.ConnectionRestored += _ => OnConnectionRestored(entry);
            _entries[symbol.Id] = entry;
            LogSubscribed(symbol.Name.Value, result.Data.SocketId);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UnsubscribeAsync(int symbolId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_entries.TryRemove(symbolId, out var entry))
            {
                return false;
            }

            await entry.Subscription.CloseAsync().ConfigureAwait(false);
            LogUnsubscribed(entry.Symbol.Name.Value);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnsubscribeAllAsync(CancellationToken cancellationToken)
    {
        foreach (var id in _entries.Keys.ToList())
        {
            await UnsubscribeAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var entry in _entries.Values)
        {
            await entry.Subscription.CloseAsync().ConfigureAwait(false);
        }

        _entries.Clear();
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private void OnKline(int symbolId, DataEvent<IBinanceStreamKlineData> update)
    {
        if (KlineMapper.TryMapClosed(symbolId, update.Data.Data, _timeProvider.GetUtcNow(), out var candle))
        {
            CandleClosed?.Invoke(this, candle);
        }
    }

    // Several subscriptions share a socket and each reports the same connection event, so the public
    // events fire once per socket.
    private void OnConnectionLost(Entry entry)
    {
        entry.Connected = false;
        bool first;
        lock (_lostSockets)
        {
            first = _lostSockets.Add(entry.Subscription.SocketId);
        }

        if (first)
        {
            LogConnectionLost(entry.Subscription.SocketId);
            ConnectionLost?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnConnectionRestored(Entry entry)
    {
        entry.Connected = true;
        bool first;
        lock (_lostSockets)
        {
            first = _lostSockets.Remove(entry.Subscription.SocketId);
        }

        if (first)
        {
            LogConnectionRestored(entry.Subscription.SocketId);
            ConnectionRestored?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Entry(SymbolInfo symbol, UpdateSubscription subscription)
    {
        private volatile bool _connected = true;

        public SymbolInfo Symbol { get; } = symbol;

        public UpdateSubscription Subscription { get; } = subscription;

        public bool Connected
        {
            get => _connected;
            set => _connected = value;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscribed to {Symbol}@kline_1m on socket {SocketId}")]
    private partial void LogSubscribed(string symbol, int socketId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unsubscribed from {Symbol}@kline_1m")]
    private partial void LogUnsubscribed(string symbol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance socket {SocketId} lost its connection; Binance.Net is reconnecting")]
    private partial void LogConnectionLost(int socketId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Binance socket {SocketId} reconnected")]
    private partial void LogConnectionRestored(int socketId);
}
