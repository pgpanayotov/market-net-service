using MarketData.Core.Abstractions;
using MarketData.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarketData.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the DbContext and repositories. This never applies migrations: schema changes come
    /// only from the migration bundle (ADR-008). Hosts add <see cref="PendingMigrationsHealthCheck"/>
    /// to their readiness checks.
    /// </summary>
    public static IServiceCollection AddMarketDataPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<MarketDataDbContext>(options => MarketDataDbContext.Configure(options, connectionString));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ISymbolRepository, SymbolRepository>();
        services.AddScoped<CandleRepository>();
        services.AddScoped<ICandleQueryRepository>(sp => sp.GetRequiredService<CandleRepository>());
        services.AddScoped<ICandleIngestRepository>(sp => sp.GetRequiredService<CandleRepository>());
        return services;
    }
}
