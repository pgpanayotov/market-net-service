using System.Collections.Concurrent;
using System.Threading.Channels;
using MarketData.Core.Abstractions;

namespace MarketData.Ingestion.Backfill;

public enum BackfillReason
{
    Startup,
    Reconnect,
    SymbolAdded,
    Periodic,
    Dropped,
    DbRecovered,
}

public enum BackfillKind
{
    /// <summary>From the newest stored candle up to the last closed minute.</summary>
    Tail,

    /// <summary>Holes inside the gap-scan window, behind the newest candle.</summary>
    Scan,
}

/// <param name="Symbol">One symbol, or <c>null</c> for every active symbol.</param>
public sealed record BackfillRequest(BackfillKind Kind, BackfillReason Reason, SymbolInfo? Symbol);

/// <summary>
/// Requests for <see cref="GapBackfiller"/>, from any thread. Requests are coalesced: asking again for
/// the same work before it starts is a no-op, so bursts (a reconnect reported by several subscriptions,
/// many dropped candles) cost one run.
/// </summary>
public sealed class BackfillScheduler
{
    private readonly ConcurrentDictionary<(BackfillKind Kind, int? SymbolId), BackfillRequest> _pending = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void RequestTail(BackfillReason reason, SymbolInfo? symbol = null) => Request(new BackfillRequest(BackfillKind.Tail, reason, symbol));

    public void RequestScan(BackfillReason reason) => Request(new BackfillRequest(BackfillKind.Scan, reason, null));

    /// <summary>Waits until something is requested or <paramref name="timeout"/> passes; <c>true</c> when requested.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await _signal.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes and returns the pending requests in run order: per-symbol tails (capped for added
    /// symbols), then all-symbol tails, then scans.
    /// </summary>
    public IReadOnlyList<BackfillRequest> TakePending()
    {
        _signal.Reader.TryRead(out _);
        var taken = new List<BackfillRequest>();
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var request))
            {
                taken.Add(request);
            }
        }

        return [.. taken.OrderBy(r => r.Kind).ThenBy(r => r.Symbol is null)];
    }

    /// <summary>Drops pending work, e.g. when a leadership term ends.</summary>
    public void Clear()
    {
        _pending.Clear();
        _signal.Reader.TryRead(out _);
    }

    public void Request(BackfillRequest request)
    {
        if (_pending.TryAdd((request.Kind, request.Symbol?.Id), request))
        {
            _signal.Writer.TryWrite(true);
        }
    }
}
