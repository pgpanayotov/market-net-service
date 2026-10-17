using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Infrastructure.Binance;
using MarketData.Ingestion.Backfill;
using MarketData.Ingestion.Pipeline;

namespace MarketData.Ingestion.Symbols;

/// <summary>
/// Converges live stream subscriptions to the active symbols in the database, which is the source of
/// truth (ADR-007). Every operation is idempotent and serialized, so a reconcile and a notification for
/// the same symbol can't interleave. A newly subscribed symbol gets a tail backfill capped at
/// <see cref="IngestionOptions.BackfillOnAdd"/>.
/// </summary>
public sealed partial class SymbolSubscriptionManager(
    IMarketDataStream stream,
    IServiceScopeFactory scopes,
    BackfillScheduler backfill,
    IngestionState state,
    TimeProvider timeProvider,
    ILogger<SymbolSubscriptionManager> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Subscribes active symbols that aren't subscribed and unsubscribes the rest.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<SymbolInfo> active;
            using (var scope = scopes.CreateScope())
            {
                active = await scope.ServiceProvider.GetRequiredService<ISymbolRepository>().GetActiveAsync(cancellationToken).ConfigureAwait(false);
            }

            var activeIds = active.Select(s => s.Id).ToHashSet();
            foreach (var stale in stream.Subscriptions.Where(s => !activeIds.Contains(s.Id)))
            {
                await UnsubscribeCoreAsync(stale.Id, cancellationToken).ConfigureAwait(false);
            }

            foreach (var symbol in active)
            {
                await SubscribeCoreAsync(symbol, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies one <c>symbol_changes</c> hint: re-reads the symbol and subscribes or unsubscribes to match
    /// its stored state. The payload's <c>op</c> is ignored, so duplicates and reordering are harmless.
    /// </summary>
    public async Task ApplyAsync(int symbolId, string symbolName, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SymbolInfo? current = null;
            if (SymbolName.TryCreate(symbolName, out var name))
            {
                using var scope = scopes.CreateScope();
                current = await scope.ServiceProvider.GetRequiredService<ISymbolRepository>().FindAsync(name, cancellationToken).ConfigureAwait(false);
            }

            if (current is { IsActive: true })
            {
                await SubscribeCoreAsync(current, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UnsubscribeCoreAsync(current?.Id ?? symbolId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnsubscribeAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.UnsubscribeAllAsync(cancellationToken).ConfigureAwait(false);
            state.Reset();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task SubscribeCoreAsync(SymbolInfo symbol, CancellationToken cancellationToken)
    {
        try
        {
            if (await stream.SubscribeAsync(symbol, cancellationToken).ConfigureAwait(false))
            {
                state.MarkSubscribed(symbol, timeProvider.GetUtcNow());
                backfill.RequestTail(BackfillReason.SymbolAdded, symbol);
            }
        }
        catch (MarketDataStreamException ex)
        {
            // Retried by the next reconcile; readiness reports the symbol as stale meanwhile.
            LogSubscribeFailed(ex, symbol.Name.Value);
        }
    }

    private async Task UnsubscribeCoreAsync(int symbolId, CancellationToken cancellationToken)
    {
        if (await stream.UnsubscribeAsync(symbolId, cancellationToken).ConfigureAwait(false))
        {
            state.Forget(symbolId);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not subscribe to {Symbol}; retrying on the next reconcile")]
    private partial void LogSubscribeFailed(Exception exception, string symbol);
}
