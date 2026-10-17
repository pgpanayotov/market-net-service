using System.Globalization;
using MarketData.Core.Domain;
using MarketData.Infrastructure.Persistence;
using MarketData.Infrastructure.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

// Tests share one database and reset it between tests, so xunit.runner.json disables parallelization.
[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace MarketData.Infrastructure.IntegrationTests;

/// <summary>
/// One PostgreSQL 17 container per test run. Migrations are applied here with <c>MigrateAsync</c>,
/// which only test code may do (ADR-008).
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

    public MarketDataDbContext CreateContext(string? connectionString = null)
    {
        var options = new DbContextOptionsBuilder<MarketDataDbContext>();
        MarketDataDbContext.Configure(options, connectionString ?? ConnectionString);
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

    /// <summary>Creates an empty database in the same container, for schema-version tests.</summary>
    public async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name}; CREATE DATABASE {name};", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }
}

/// <summary>Base class that resets the shared database before each test.</summary>
public abstract class PostgresTest(PostgresFixture postgres) : IAsyncLifetime
{
    protected const int Btc = 1;
    protected const int Ada = 2;
    protected const int Eth = 3;

    protected PostgresFixture Postgres { get; } = postgres;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await Postgres.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    protected static Candle C(int symbolId, DateTimeOffset openTime, decimal close) =>
        new(symbolId, openTime, close, close, close, close, Volume: 1.5m, QuoteVolume: close * 1.5m, TradeCount: 10, ReceivedAt: openTime.AddSeconds(61));

    /// <summary>One candle per minute starting at <paramref name="start"/>, with the given closes.</summary>
    protected static IEnumerable<Candle> Minutes(int symbolId, DateTimeOffset start, params decimal[] closes) =>
        closes.Select((close, i) => C(symbolId, start.AddMinutes(i), close));

    protected async Task SeedAsync(IEnumerable<Candle> candles)
    {
        await using var db = Postgres.CreateContext();
        await new CandleRepository(db).UpsertAsync([.. candles], Ct);
    }
}
