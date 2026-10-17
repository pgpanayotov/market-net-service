using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using MarketData.Core.Abstractions;

namespace MarketData.Ingestion.Pipeline;

/// <summary>
/// In-memory bookkeeping of the current leadership term: which symbols were subscribed and when
/// (readiness gives a new symbol time to receive data; metrics are tagged by name), and the gaps found
/// by the last scan (gauge). Nothing here is a source of truth; it is rebuilt from the database each term.
/// </summary>
public sealed class IngestionState
{
    private readonly ConcurrentDictionary<int, (string Symbol, DateTimeOffset At)> _subscribed = new();
    private readonly ConcurrentDictionary<int, (string Symbol, long Minutes)> _gaps = new();

    public IngestionState(IngestionMetrics metrics) =>
        metrics.ObserveGaps(() => _gaps.Values.Select(g => new Measurement<long>(g.Minutes, IngestionMetrics.Symbol(g.Symbol))));

    public void MarkSubscribed(SymbolInfo symbol, DateTimeOffset at) => _subscribed[symbol.Id] = (symbol.Name.Value, at);

    public DateTimeOffset? SubscribedAt(int symbolId) => _subscribed.TryGetValue(symbolId, out var s) ? s.At : null;

    /// <summary>The symbol's name for metric tags; its id when it is not subscribed.</summary>
    public string NameOf(int symbolId) =>
        _subscribed.TryGetValue(symbolId, out var s) ? s.Symbol : symbolId.ToString(CultureInfo.InvariantCulture);

    public void Forget(int symbolId)
    {
        _subscribed.TryRemove(symbolId, out _);
        _gaps.TryRemove(symbolId, out _);
    }

    public void RecordGap(SymbolInfo symbol, long missingMinutes) => _gaps[symbol.Id] = (symbol.Name.Value, missingMinutes);

    public void Reset()
    {
        _subscribed.Clear();
        _gaps.Clear();
    }
}
