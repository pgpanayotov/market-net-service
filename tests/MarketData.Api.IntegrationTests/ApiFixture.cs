using System.Globalization;
using MarketData.Api.Caching;
using MarketData.Api.IntegrationTests;
using MarketData.Core.Domain;
using MarketData.Infrastructure.Binance;
using MarketData.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

// Tests share one database and reset it between tests, so xunit.runner.json disables parallelization.
[assembly: AssemblyFixture(typeof(ApiFixture))]

namespace MarketData.Api.IntegrationTests;

/// <summary>
/// One PostgreSQL 17 container and one API host per test run. Migrations are applied here with
/// <c>MigrateAsync</c>, which only test code may do (ADR-008).
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string AdminKey = "test-admin-key";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public ApiFactory Factory { get; private set; } = null!;

    public StubExchangeValidator Exchange { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        Factory = new ApiFactory(ConnectionString, Exchange);
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    public MarketDataDbContext CreateContext(string? connectionString = null)
    {
        var options = new DbContextOptionsBuilder<MarketDataDbContext>();
        MarketDataDbContext.Configure(options, connectionString ?? ConnectionString);
        return new MarketDataDbContext(options.Options);
    }

    /// <summary>Back to the post-migration state (seeded symbols only, all active, no candles) with an empty cache.</summary>
    public async Task ResetAsync()
    {
        await using (var db = CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                $"""
                TRUNCATE candles_1m;
                DELETE FROM symbols WHERE id >= {MarketDataDbContext.FirstGeneratedSymbolId};
                UPDATE symbols SET is_active = TRUE;
                """);
        }

        Factory.Services.GetRequiredService<AnalysisCache>().Cache.Clear();
        Exchange.Unavailable = false;
    }

    /// <summary>Creates an empty database in the same container, for schema-version tests.</summary>
    public async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE); CREATE DATABASE {name};", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }
}

/// <summary>The API host against the test database, with Binance exchangeInfo stubbed.</summary>
public sealed class ApiFactory(string connectionString, IExchangeSymbolValidator exchange) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:MarketData", connectionString);
        builder.UseSetting("Admin:ApiKey", ApiFixture.AdminKey);
        builder.ConfigureTestServices(services => services.AddSingleton(exchange));
    }
}

public sealed class StubExchangeValidator : IExchangeSymbolValidator
{
    private static readonly HashSet<string> Tradable = ["BTCUSDT", "ETHUSDT", "ADAUSDT", "SOLUSDT", "XRPUSDT"];

    public bool Unavailable { get; set; }

    public Task<bool> IsTradableAsync(SymbolName symbol, CancellationToken cancellationToken) =>
        Unavailable
            ? throw new ExchangeInfoUnavailableException("Simulated outage.")
            : Task.FromResult(Tradable.Contains(symbol.Value));
}

/// <summary>Base class that resets the shared database and cache before each test.</summary>
public abstract class ApiTest(ApiFixture fixture) : IAsyncLifetime
{
    protected const int Btc = 1;
    protected const int Eth = 3;

    protected ApiFixture Fixture { get; } = fixture;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await Fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected HttpClient CreateClient(string? accept = null, bool admin = false)
    {
        var client = Fixture.Factory.CreateClient();
        if (accept is not null)
        {
            client.DefaultRequestHeaders.Add("Accept", accept);
        }

        if (admin)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiFixture.AdminKey);
        }

        return client;
    }

    protected static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    protected static Candle C(int symbolId, DateTimeOffset openTime, decimal close) =>
        new(symbolId, openTime, close, close, close, close, Volume: 1.5m, QuoteVolume: close * 1.5m, TradeCount: 10, ReceivedAt: openTime.AddSeconds(61));

    /// <summary>One candle per minute starting at <paramref name="start"/>, with the given closes.</summary>
    protected static IEnumerable<Candle> Minutes(int symbolId, DateTimeOffset start, params decimal[] closes) =>
        closes.Select((close, i) => C(symbolId, start.AddMinutes(i), close));

    protected async Task SeedAsync(IEnumerable<Candle> candles)
    {
        await using var db = Fixture.CreateContext();
        await new CandleRepository(db).UpsertAsync([.. candles], Ct);
    }
}
