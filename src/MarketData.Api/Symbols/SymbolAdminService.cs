using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Infrastructure.Notifications;
using MarketData.Infrastructure.Persistence;

namespace MarketData.Api.Symbols;

/// <summary>
/// Admin symbol writes. Each write and its <c>symbol_changes</c> notification commit in one
/// transaction (ADR-007): the worker is told only about changes that were persisted, and a rollback
/// sends nothing.
/// </summary>
public sealed class SymbolAdminService(
    MarketDataDbContext db,
    ISymbolRepository symbols,
    IPgNotificationPublisher publisher,
    TimeProvider timeProvider)
{
    public async Task<SymbolActivation> AddAsync(SymbolName name, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (outcome, symbol) = await symbols.ActivateAsync(name, cancellationToken).ConfigureAwait(false);
        if (outcome != SymbolActivation.AlreadyActive)
        {
            await NotifyAsync(SymbolChangeOperation.Add, symbol, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    public async Task<SymbolDeactivation> RemoveAsync(SymbolName name, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var outcome = await symbols.DeactivateAsync(name, cancellationToken).ConfigureAwait(false);
        if (outcome == SymbolDeactivation.Deactivated)
        {
            var symbol = await symbols.FindAsync(name, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Symbol {name} vanished during deactivation.");
            await NotifyAsync(SymbolChangeOperation.Remove, symbol, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private Task NotifyAsync(SymbolChangeOperation op, SymbolInfo symbol, CancellationToken cancellationToken) =>
        publisher.NotifyAsync(
            NotificationChannels.SymbolChanges,
            new SymbolChangedPayload(op, symbol.Id, symbol.Name.Value, timeProvider.GetUtcNow()),
            cancellationToken);
}
