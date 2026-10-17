using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarketData.Infrastructure.Binance;

public static class BinanceMarketDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Binance.Net clients for the configured endpoints (<see cref="BinanceEndpointOptions"/>), the
    /// kline stream and the kline REST client. Used by the ingestion worker only; the API never opens a
    /// Binance WebSocket (ADR-007).
    /// </summary>
    public static IServiceCollection AddBinanceMarketData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(BinanceEndpointOptions.SectionName);
        services.AddOptions<BinanceEndpointOptions>()
            .Bind(section)
            .Validate(o => BinanceEndpointOptions.IsAbsolute(o.RestBaseUrl, "http", "https"), "MarketData:Binance:RestBaseUrl must be an absolute http(s) URL.")
            .Validate(o => BinanceEndpointOptions.IsAbsolute(o.SocketBaseUrl, "ws", "wss"), "MarketData:Binance:SocketBaseUrl must be an absolute ws(s) URL.")
            .ValidateOnStart();
        var options = section.Get<BinanceEndpointOptions>() ?? new BinanceEndpointOptions();

        services.AddBinance(binance => binance.Environment = options.ToEnvironment());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMarketDataStream, BinanceMarketDataStream>();
        services.TryAddSingleton<IKlineRestClient, BinanceKlineRestClient>();
        return services;
    }
}
