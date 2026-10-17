using MarketData.Infrastructure;
using MarketData.Infrastructure.Binance;
using MarketData.Infrastructure.Notifications;
using MarketData.Ingestion.Backfill;
using MarketData.Ingestion.Health;
using MarketData.Ingestion.Pipeline;
using MarketData.Ingestion.Symbols;

namespace MarketData.Ingestion;

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
            .Validate(o => o.HistoryDepth > TimeSpan.Zero && o.GapScanWindow > TimeSpan.Zero, "History depth and scan window must be positive.")
            .ValidateOnStart();

        services.AddMarketDataPersistence(connectionString);
        services.AddBinanceMarketData(configuration);

        services.AddSingleton(new PgListenerSettings(connectionString));
        services.AddSingleton<IngestionMetrics>();
        services.AddSingleton<IngestionState>();
        services.AddSingleton<BackfillScheduler>();
        services.AddSingleton<CandlePipe>();
        services.AddSingleton<SymbolSubscriptionManager>();

        // Plain hosted services: no leader election (ADR-009). Two workers overlapping for a few seconds
        // during a deploy is safe, because every write is an idempotent upsert.
        // The collector first: it hooks the stream events in StartAsync, before the listener can subscribe anything.
        services.AddHostedService<BinanceStreamCollector>();
        services.AddSingleton<SymbolChangeListener>();
        services.AddHostedService(sp => sp.GetRequiredService<SymbolChangeListener>());
        services.AddHostedService<CandleWriter>();
        services.AddHostedService<GapBackfiller>();

        services.AddIngestionHealthChecks();
        return services;
    }
}
