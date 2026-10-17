using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MarketData.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> and the migration bundle, so neither depends on an app's <c>Program</c>
/// (ADR-008). The bundle's <c>--connection</c> argument overrides the connection string resolved here.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MarketDataDbContext>
{
    public const string ConnectionStringVariable = "ConnectionStrings__MarketData";

    /// <summary>Matches the local docker-compose defaults.</summary>
    public const string LocalConnectionString = "Host=localhost;Port=5432;Database=marketdata;Username=postgres;Password=postgres";

    public MarketDataDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } value
            ? value
            : LocalConnectionString;

        var options = new DbContextOptionsBuilder<MarketDataDbContext>();
        MarketDataDbContext.Configure(options, connectionString);
        return new MarketDataDbContext(options.Options);
    }
}
