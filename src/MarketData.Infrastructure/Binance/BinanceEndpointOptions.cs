using Binance.Net;

namespace MarketData.Infrastructure.Binance;

/// <summary>
/// Binance endpoints (<c>MarketData:Binance</c>). The defaults are the official Spot endpoints. Where
/// those are blocked (for example HTTP 451 in some regions), the market-data-only mirrors
/// <c>https://data-api.binance.vision</c> (REST) and <c>wss://data-stream.binance.vision</c> (WebSocket)
/// serve the same klines.
/// </summary>
public sealed class BinanceEndpointOptions
{
    public const string SectionName = "MarketData:Binance";

    public const string DefaultRestBaseUrl = "https://api.binance.com";

    public const string DefaultSocketBaseUrl = "wss://stream.binance.com:9443";

    /// <summary>Spot REST base URL, used for <c>/api/v3/klines</c> backfill.</summary>
    public string RestBaseUrl { get; set; } = DefaultRestBaseUrl;

    /// <summary>Spot WebSocket stream base URL; Binance.Net appends the combined-stream path.</summary>
    public string SocketBaseUrl { get; set; } = DefaultSocketBaseUrl;

    public static bool IsAbsolute(string? url, params string[] schemes) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A Binance.Net environment with the configured Spot addresses. The futures addresses, which this
    /// service never uses, keep their live values.
    /// </summary>
    public BinanceEnvironment ToEnvironment()
    {
        var live = BinanceEnvironment.Live;
        return BinanceEnvironment.CreateCustom(
            "marketdata",
            RestBaseUrl.TrimEnd('/'),
            SocketBaseUrl.TrimEnd('/') + "/",
            live.SpotSocketApiAddress,
            live.RiskDataSocketAddress,
            live.UsdFuturesRestAddress,
            live.UsdFuturesSocketAddress,
            live.UsdFuturesSocketApiAddress,
            live.CoinFuturesRestAddress,
            live.CoinFuturesSocketAddress,
            live.CoinFuturesSocketApiAddress);
    }
}
