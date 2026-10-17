using Binance.Net;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using MarketData.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MarketData.Infrastructure.Binance;

/// <summary>Checks a symbol against Binance <c>exchangeInfo</c> before the admin API stores it.</summary>
public interface IExchangeSymbolValidator
{
    /// <summary><c>true</c> when the symbol is a spot symbol with status <c>TRADING</c>.</summary>
    /// <exception cref="ExchangeInfoUnavailableException">Binance could not be queried.</exception>
    Task<bool> IsTradableAsync(SymbolName symbol, CancellationToken cancellationToken);
}

public sealed class ExchangeInfoUnavailableException(string message) : Exception(message);

public sealed class ExchangeInfoOptions
{
    public const string SectionName = "MarketData:Binance";

    /// <summary>
    /// Spot REST base URL, for regions where <c>api.binance.com</c> is blocked (for example
    /// <c>https://data-api.binance.vision</c>). Binance live when unset.
    /// </summary>
    public string? RestBaseUrl { get; set; }

    /// <summary>How long the list of tradable symbols is reused.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Loads every TRADING spot symbol in one <c>exchangeInfo</c> call and keeps the set for
/// <see cref="ExchangeInfoOptions.CacheDuration"/>. Failed loads are not cached, so the next request retries.
/// </summary>
public sealed partial class ExchangeInfoSymbolValidator(
    IBinanceRestClient client,
    ExchangeInfoOptions options,
    TimeProvider timeProvider,
    ILogger<ExchangeInfoSymbolValidator> logger) : IExchangeSymbolValidator, IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private (HashSet<string> Symbols, DateTimeOffset LoadedAt)? _cached;

    public async Task<bool> IsTradableAsync(SymbolName symbol, CancellationToken cancellationToken)
    {
        var symbols = await GetTradableSymbolsAsync(cancellationToken).ConfigureAwait(false);
        return symbols.Contains(symbol.Value);
    }

    public void Dispose() => _refreshLock.Dispose();

    private async Task<HashSet<string>> GetTradableSymbolsAsync(CancellationToken cancellationToken)
    {
        if (TryGetFresh() is { } fresh)
        {
            return fresh;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetFresh() is { } loaded)
            {
                return loaded;
            }

            var result = await client.SpotApi.ExchangeData
                .GetExchangeInfoAsync(PermissionType.Spot, false, SymbolStatus.Trading, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                LogLoadFailed(logger, result.Error?.ToString());
                throw new ExchangeInfoUnavailableException($"Binance exchangeInfo failed: {result.Error}");
            }

            var symbols = result.Data.Symbols
                .Where(s => s.Status == SymbolStatus.Trading && s.IsSpotTradingAllowed)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.Ordinal);
            _cached = (symbols, timeProvider.GetUtcNow());
            LogLoaded(logger, symbols.Count);
            return symbols;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private HashSet<string>? TryGetFresh() =>
        _cached is { } cached && timeProvider.GetUtcNow() - cached.LoadedAt < options.CacheDuration ? cached.Symbols : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} tradable spot symbols from Binance exchangeInfo")]
    private static partial void LogLoaded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance exchangeInfo failed: {Error}")]
    private static partial void LogLoadFailed(ILogger logger, string? error);
}

public static class ExchangeInfoServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IExchangeSymbolValidator"/> and the Binance REST client it uses.
    /// </summary>
    public static IServiceCollection AddExchangeInfoSymbolValidator(this IServiceCollection services, ExchangeInfoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddBinance(binance =>
        {
            if (!string.IsNullOrWhiteSpace(options.RestBaseUrl))
            {
                var live = BinanceEnvironment.Live;
                binance.Environment = BinanceEnvironment.CreateCustom(
                    "custom",
                    options.RestBaseUrl,
                    live.SpotSocketStreamAddress,
                    live.SpotSocketApiAddress,
                    live.RiskDataSocketAddress,
                    live.UsdFuturesRestAddress,
                    live.UsdFuturesSocketAddress,
                    live.UsdFuturesSocketApiAddress,
                    live.CoinFuturesRestAddress,
                    live.CoinFuturesSocketAddress,
                    live.CoinFuturesSocketApiAddress);
            }
        });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton<IExchangeSymbolValidator, ExchangeInfoSymbolValidator>();
        return services;
    }
}
