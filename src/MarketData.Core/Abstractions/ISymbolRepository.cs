using MarketData.Core.Domain;
using MarketData.Core.Enums;

namespace MarketData.Core.Abstractions;

/// <summary>
/// Symbol storage. Writes join the caller's ambient transaction, so the API can commit a change
/// and its <c>symbol_changes</c> notification together (ADR-007).
/// </summary>
public interface ISymbolRepository
{
    /// <summary>Finds a symbol whether it is active or not (history of removed symbols stays queryable).</summary>
    Task<SymbolInfo?> FindAsync(SymbolName name, CancellationToken cancellationToken);

    /// <summary>All symbols, active and inactive, ordered by name.</summary>
    Task<IReadOnlyList<SymbolDetails>> ListAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SymbolInfo>> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Inserts the symbol, or reactivates it if it was removed.</summary>
    Task<(SymbolActivation Outcome, SymbolInfo Symbol)> ActivateAsync(SymbolName name, CancellationToken cancellationToken);

    /// <summary>Soft-deletes the symbol (<c>is_active = false</c>); candles are never purged.</summary>
    Task<SymbolDeactivation> DeactivateAsync(SymbolName name, CancellationToken cancellationToken);
}
