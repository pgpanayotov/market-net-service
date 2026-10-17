using MarketData.Infrastructure.Persistence;
using MarketData.Ingestion.IntegrationTests.Worker;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

// Tests share one database and reset it between tests, so xunit.runner.json disables parallelization.
[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace MarketData.Ingestion.IntegrationTests.Worker;

/// <summary>
/// One PostgreSQL 17 container per test run. Migrations are applied here with <c>MigrateAsync</c>,
/// which only test code may do (ADR-008); the worker itself only checks for pending migrations.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public MarketDataDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MarketDataDbContext>();
        MarketDataDbContext.Configure(options, ConnectionString);
        return new MarketDataDbContext(options.Options);
    }

    /// <summary>Back to the post-migration state: no candles, only the seeded symbols, all active.</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            $"""
            TRUNCATE candles_1m;
            DELETE FROM symbols WHERE id >= {MarketDataDbContext.FirstGeneratedSymbolId};
            UPDATE symbols SET is_active = TRUE;
            """);
    }
}
