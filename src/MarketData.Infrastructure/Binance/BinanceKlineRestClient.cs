using System.Net;
using System.Runtime.CompilerServices;

using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;

using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;

using MarketData.Core.Domain;

using Microsoft.Extensions.Logging;

namespace MarketData.Infrastructure.Binance;

/// <summary>
/// <see cref="IKlineRestClient"/> on Binance.Net. Honours <c>Retry-After</c> on HTTP 429 (rate limited)
/// and 418 (IP banned for ignoring 429s): no request is sent before that time passes, across all callers.
/// </summary>
public sealed partial class BinanceKlineRestClient(
    IBinanceRestClient client,
    TimeProvider timeProvider,
    ILogger<BinanceKlineRestClient> logger) : IKlineRestClient
{
    /// <summary>Binance's maximum <c>limit</c> for <c>/api/v3/klines</c>.</summary>
    public const int MaxKlinesPerRequest = 1000;

    /// <summary>Used when a 429 has no usable <c>Retry-After</c> header.</summary>
    public static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(60);

    /// <summary>Used when a 418 has no usable <c>Retry-After</c> header. Bans start at 2 minutes.</summary>
    public static readonly TimeSpan DefaultBanDelay = TimeSpan.FromMinutes(2);

    private const int MaxRateLimitRetries = 3;
    private const int MaxTransientRetries = 4;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Minute = MarketDataConstants.CandleInterval;

    private readonly Lock _blockedLock = new();
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public async IAsyncEnumerable<IReadOnlyList<Candle>> GetClosedCandlesAsync(
        SymbolInfo symbol,
        TimeRange range,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var cursor = CeilingToMinute(range.From);
        while (cursor < range.To)
        {
            var limit = (int)Math.Min(MaxKlinesPerRequest, Math.Ceiling((range.To - cursor) / Minute));
            var klines = await GetPageAsync(symbol.Name.Value, cursor, range.To, limit, cancellationToken).ConfigureAwait(false);
            if (klines.Length == 0)
            {
                yield break;
            }

            var now = timeProvider.GetUtcNow();
            var page = klines
                .Select(k => KlineMapper.ToCandle(symbol.Id, k, now))
                .Where(c => c.OpenTime >= cursor && c.OpenTime < range.To && c.OpenTime + Minute <= now)
                .ToList();
            if (page.Count > 0)
            {
                yield return page;
            }

            // A short page means the range is exhausted (Binance skips minutes it has no kline for).
            if (klines.Length < limit)
            {
                yield break;
            }

            cursor = FloorToMinute(new DateTimeOffset(DateTime.SpecifyKind(klines[^1].OpenTime, DateTimeKind.Utc))) + Minute;
        }
    }

    private async Task<IBinanceKline[]> GetPageAsync(string symbol, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken)
    {
        var rateLimited = 0;
        var transient = 0;
        while (true)
        {
            await WaitWhileBlockedAsync(cancellationToken).ConfigureAwait(false);
            var result = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.OneMinute,
                    startTime: from.UtcDateTime,
                    endTime: (to - TimeSpan.FromMilliseconds(1)).UtcDateTime,
                    limit: limit,
                    ct: cancellationToken)
                .ConfigureAwait(false);
            if (result.Success)
            {
                return result.Data ?? [];
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (RateLimitDelay(result) is { } wait)
            {
                Block(wait);
                LogRateLimited(symbol, (int?)result.ResponseStatusCode, wait.TotalSeconds);
                if (++rateLimited > MaxRateLimitRetries)
                {
                    throw new KlineRequestException($"Klines for {symbol} are still rate limited after {MaxRateLimitRetries} retries: {result.Error}");
                }

                continue;
            }

            if (IsTransient(result) && transient < MaxTransientRetries)
            {
                var delay = ExponentialBackoff.Delay(transient++, InitialRetryDelay, MaxRetryDelay);
                LogTransientFailure(symbol, result.Error?.ToString(), delay.TotalMilliseconds);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new KlineRequestException($"Klines request for {symbol} failed ({(int?)result.ResponseStatusCode}): {result.Error}");
        }
    }

    /// <summary>How long to back off for a 429/418 response; <c>null</c> for any other failure.</summary>
    private TimeSpan? RateLimitDelay(HttpResult result)
    {
        var status = result.ResponseStatusCode;
        var banned = status == (HttpStatusCode)418;
        if (status != HttpStatusCode.TooManyRequests && !banned && result.Error?.ErrorType != ErrorType.RateLimitRequest)
        {
            return null;
        }

        var retryAfter = result.ResponseHeaders?.RetryAfter;
        var fromHeader = retryAfter?.Delta ?? (retryAfter?.Date - timeProvider.GetUtcNow());
        return fromHeader is { } d && d > TimeSpan.Zero ? d : banned ? DefaultBanDelay : DefaultRateLimitDelay;
    }

    private static bool IsTransient(HttpResult result) =>
        result.Error?.IsTransient == true || result.ResponseStatusCode is null || (int)result.ResponseStatusCode >= 500;

    private void Block(TimeSpan wait)
    {
        var until = timeProvider.GetUtcNow() + wait;
        lock (_blockedLock)
        {
            if (until > _blockedUntil)
            {
                _blockedUntil = until;
            }
        }
    }

    private async Task WaitWhileBlockedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (_blockedLock)
            {
                remaining = _blockedUntil - timeProvider.GetUtcNow();
            }

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset CeilingToMinute(DateTimeOffset value) =>
        value.UtcTicks % Minute.Ticks == 0 ? value.ToUniversalTime() : FloorToMinute(value) + Minute;

    private static DateTimeOffset FloorToMinute(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % Minute.Ticks), TimeSpan.Zero);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance rate limited klines for {Symbol} (HTTP {Status}); pausing REST requests for {Seconds:F0}s")]
    private partial void LogRateLimited(string symbol, int? status, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Klines request for {Symbol} failed transiently ({Error}); retrying in {DelayMs:F0} ms")]
    private partial void LogTransientFailure(string symbol, string? error, double delayMs);
}
