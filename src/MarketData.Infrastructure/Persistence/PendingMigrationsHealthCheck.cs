using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MarketData.Infrastructure.Persistence;

/// <summary>
/// Read-only startup guard (ADR-008): reports unhealthy while the database lags behind this build's
/// migrations, so an app never runs against an old schema and the fix is obvious.
/// </summary>
public sealed class PendingMigrationsHealthCheck(MarketDataDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            return pending.Count == 0
                ? HealthCheckResult.Healthy("Database schema is current.")
                : HealthCheckResult.Unhealthy(
                    $"Database schema is behind: {pending.Count} pending migration(s) ({string.Join(", ", pending)}). Run the migrator.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Cannot read the database schema version.", ex);
        }
    }
}
