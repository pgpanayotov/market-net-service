using MarketData.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MarketData.Ingestion.Health;

public sealed class DatabaseHealthCheck(MarketDataDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database unreachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
