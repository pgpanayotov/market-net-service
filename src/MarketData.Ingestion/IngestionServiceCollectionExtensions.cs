using MarketData.Infrastructure;
using MarketData.Infrastructure.Binance;
using MarketData.Infrastructure.Notifications;
using MarketData.Ingestion.Backfill;
using MarketData.Ingestion.Health;
using MarketData.Ingestion.Leadership;
using MarketData.Ingestion.Pipeline;
using MarketData.Ingestion.Symbols;

namespace MarketData.Ingestion;

/// <summary>The worker's database connection string (<c>ConnectionStrings:MarketData</c>), for its dedicated sessions.</summary>
public sealed record IngestionConnection(string ConnectionString);

public static class IngestionServiceCollectionExtensions
{
    public const string ConnectionStringName = "MarketData";

    public static IServiceCollection AddMarketDataIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string 'ConnectionStrings:{ConnectionStringName}' is required.");
        }

        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .Validate(o => o.ChannelCapacity > 0 && o.WriteBatchSize > 0, "Channel capacity and batch size must be positive.")
            .Validate(o => o.BackfillOnAdd > TimeSpan.Zero && o.GapScanWindow > TimeSpan.Zero, "Backfill and scan windows must be positive.")
            .ValidateOnStart();

        services.AddMarketDataPersistence(connectionString);
        services.AddBinanceMarketData(configuration);

        services.AddSingleton(new IngestionConnection(connectionString));
        services.AddSingleton(new PgListenerSettings(connectionString));
        services.AddSingleton<IngestionMetrics>();
        services.AddSingleton<LeadershipState>();
        services.AddSingleton<IngestionState>();
        services.AddSingleton<BackfillScheduler>();
        services.AddSingleton<CandlePipe>();
        services.AddSingleton<SymbolSubscriptionManager>();

        // Leader-only services: started and stopped by LeaderLock, never by the host directly.
        services.AddSingleton<SymbolChangeListener>();
        services.AddSingleton<ILeaderService>(sp => sp.GetRequiredService<SymbolChangeListener>());
        services.AddSingleton<ILeaderService, BinanceStreamCollector>();
        services.AddSingleton<ILeaderService, CandleWriter>();
        services.AddSingleton<ILeaderService, GapBackfiller>();
        services.AddHostedService<LeaderLock>();

        services.AddIngestionHealthChecks();
        return services;
    }
}
