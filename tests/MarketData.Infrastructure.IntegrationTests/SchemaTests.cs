using MarketData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MarketData.Infrastructure.IntegrationTests;

public class SchemaTests(PostgresFixture postgres) : PostgresTest(postgres)
{
    [Fact]
    public async Task HealthCheck_MigratedDatabase_IsHealthy()
    {
        await using var db = Postgres.CreateContext();

        var result = await new PendingMigrationsHealthCheck(db).CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task HealthCheck_EmptyDatabase_ReportsPendingMigrations()
    {
        var connectionString = await Postgres.CreateEmptyDatabaseAsync("schema_behind");
        await using var db = Postgres.CreateContext(connectionString);

        var result = await new PendingMigrationsHealthCheck(db).CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Run the migrator", result.Description, StringComparison.Ordinal);
        Assert.Contains("Initial", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthCheck_UnreachableDatabase_IsUnhealthy()
    {
        await using var db = Postgres.CreateContext("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");

        var result = await new PendingMigrationsHealthCheck(db).CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task Migrations_AreIdempotent()
    {
        await using var db = Postgres.CreateContext();

        await db.Database.MigrateAsync(Ct);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.Equal(3, await db.Symbols.CountAsync(Ct));
    }
}
