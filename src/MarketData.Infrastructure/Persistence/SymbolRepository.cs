using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Core.Enums;

using Microsoft.EntityFrameworkCore;

namespace MarketData.Infrastructure.Persistence;

public sealed class SymbolRepository(MarketDataDbContext db, TimeProvider timeProvider) : ISymbolRepository
{
    public async Task<SymbolInfo?> FindAsync(SymbolName name, CancellationToken cancellationToken)
    {
        var row = await db.Symbols.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Name == name.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : new SymbolInfo(row.Id, name, row.IsActive);
    }

    public async Task<IReadOnlyList<SymbolDetails>> ListAsync(CancellationToken cancellationToken)
    {
        // The correlated max() is a backward PK index probe per symbol, not a scan of candles_1m.
        const string sql = """
            SELECT s.id, s.name, s.is_active, s.created_at,
                   (SELECT max(c.open_time) FROM candles_1m c WHERE c.symbol_id = s.id)
            FROM symbols s
            ORDER BY s.name
            """;

        await using var command = await db.CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SymbolDetails>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SymbolDetails(
                reader.GetInt32(0),
                ParseStored(reader.GetString(1)),
                reader.GetBoolean(2),
                reader.GetUtcDateTimeOffset(3),
                reader.IsDBNull(4) ? null : reader.GetUtcDateTimeOffset(4)));
        }

        return result;
    }

    public async Task<IReadOnlyList<SymbolInfo>> GetActiveAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Symbols.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(r => new SymbolInfo(r.Id, ParseStored(r.Name), IsActive: true))];
    }

    public async Task<(SymbolActivation Outcome, SymbolInfo Symbol)> ActivateAsync(SymbolName name, CancellationToken cancellationToken)
    {
        // One statement, so concurrent adds of the same symbol can't race into a unique violation.
        // A row comes back only when something changed; xmax = 0 means a fresh insert rather than a reactivation.
        const string sql = """
            INSERT INTO symbols AS s (name, is_active, created_at, updated_at)
            VALUES (@name, TRUE, @now, @now)
            ON CONFLICT (name) DO UPDATE SET is_active = TRUE, updated_at = EXCLUDED.updated_at
            WHERE NOT s.is_active
            RETURNING s.id, (s.xmax = 0)
            """;

        await using (var command = await db.CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false))
        {
            command.With("name", name.Value).With("now", timeProvider.GetUtcNow().Utc());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var outcome = reader.GetBoolean(1) ? SymbolActivation.Created : SymbolActivation.Reactivated;
                return (outcome, new SymbolInfo(reader.GetInt32(0), name, IsActive: true));
            }
        }

        var existing = await FindAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Symbol {name} vanished during activation.");
        return (SymbolActivation.AlreadyActive, existing);
    }

    public async Task<SymbolDeactivation> DeactivateAsync(SymbolName name, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE symbols SET is_active = FALSE, updated_at = @now
            WHERE name = @name AND is_active
            """;

        await using var command = await db.CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false);
        command.With("name", name.Value).With("now", timeProvider.GetUtcNow().Utc());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
        {
            return SymbolDeactivation.Deactivated;
        }

        return await FindAsync(name, cancellationToken).ConfigureAwait(false) is null
            ? SymbolDeactivation.NotFound
            : SymbolDeactivation.AlreadyInactive;
    }

    private static SymbolName ParseStored(string value) =>
        SymbolName.TryCreate(value, out var name)
            ? name
            : throw new InvalidOperationException($"Stored symbol name '{value}' is invalid.");
}
