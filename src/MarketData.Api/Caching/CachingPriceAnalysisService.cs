using MarketData.Contracts;
using MarketData.Core.Abstractions;
using MarketData.Core.Analysis;
using MarketData.Core.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace MarketData.Api.Caching;

public sealed class AnalysisCacheOptions
{
    public const string SectionName = "MarketData:Cache";

    /// <summary>Expiry of a result whose window can still change (ADR-006).</summary>
    public TimeSpan RecentAbsoluteExpiry { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Sliding expiry of a result whose window is older than <see cref="SettledAfter"/>.</summary>
    public TimeSpan SettledSlidingExpiry { get; set; } = TimeSpan.FromHours(1);

    /// <summary>A window ending this long before the newest candle is outside the gap scan (ADR-005), so it no longer changes.</summary>
    public TimeSpan SettledAfter { get; set; } = TimeSpan.FromHours(48);

    /// <summary>Total cache size, in units of one result plus one per point or series item.</summary>
    public long SizeLimit { get; set; } = 200_000;
}

/// <summary>The analysis result cache, separate from any shared <see cref="IMemoryCache"/> because it has a size limit.</summary>
public sealed class AnalysisCache(AnalysisCacheOptions options) : IDisposable
{
    public MemoryCache Cache { get; } = new(new MemoryCacheOptions { SizeLimit = options.SizeLimit });

    public void Dispose() => Cache.Dispose();
}

/// <summary>
/// Caches successful analysis results keyed on the data version (ADR-006, system-design §6.4):
/// <c>(endpoint, symbol, parameters, latestOpenTime)</c>. A newly stored candle changes the key, so
/// nothing is invalidated. Errors are never cached.
/// </summary>
/// <remarks>
/// The key needs the symbol's newest open time, which the wrapped <see cref="PriceAnalysisService"/>
/// reads as well. Both run against one <see cref="AnalysisReadSnapshot"/> per request, so the symbol
/// lookup and the newest-candle probe hit the database once, and a cached result always belongs to
/// the <c>latestOpenTime</c> in its key.
/// </remarks>
public sealed class CachingPriceAnalysisService : IPriceAnalysisService
{
    private const string AverageEndpoint = "24hAvgPrice";
    private const string SmaEndpoint = "SimpleMovingAverage";

    private readonly AnalysisReadSnapshot _snapshot;
    private readonly PriceAnalysisService _inner;
    private readonly TimeProvider _timeProvider;
    private readonly AnalysisCache _cache;
    private readonly AnalysisCacheOptions _options;

    public CachingPriceAnalysisService(
        ISymbolRepository symbols,
        ICandleQueryRepository candles,
        TimeProvider timeProvider,
        AnalysisCache cache,
        AnalysisCacheOptions options)
    {
        _snapshot = new AnalysisReadSnapshot(symbols, candles);
        _inner = new PriceAnalysisService(_snapshot, _snapshot, timeProvider);
        _timeProvider = timeProvider;
        _cache = cache;
        _options = options;
    }

    public async Task<AnalysisResult<AvgPriceResponse>> Get24hAverageAsync(string? symbol, CancellationToken cancellationToken)
    {
        if (!SymbolName.TryCreate(symbol, out var name)
            || await GetLatestOpenTimeAsync(name, cancellationToken).ConfigureAwait(false) is not { } latest)
        {
            // Invalid or unknown symbol, or no data: the inner service reports it from the snapshot.
            return await _inner.Get24hAverageAsync(symbol, cancellationToken).ConfigureAwait(false);
        }

        // The 24h window always ends at the newest candle, so it is never settled.
        return await GetOrComputeAsync(
            new AverageKey(name, latest),
            AverageEndpoint,
            settled: false,
            () => _inner.Get24hAverageAsync(symbol, cancellationToken),
            _ => 1).ConfigureAwait(false);
    }

    public async Task<AnalysisResult<SmaResponse>> GetSmaAsync(SmaQuery query, CancellationToken cancellationToken)
    {
        AnalysisValidator.Validate(query, _timeProvider.GetUtcNow(), out var valid);
        if (valid is null
            || await GetLatestOpenTimeAsync(valid.Symbol, cancellationToken).ConfigureAwait(false) is not { } latest)
        {
            return await _inner.GetSmaAsync(query, cancellationToken).ConfigureAwait(false);
        }

        // Same anchoring as PriceAnalysisService (ADR-006). The result depends on `s` only through
        // referenceTime, so every `s` in one bucket shares an entry.
        var dataEnd = latest + MarketDataConstants.CandleInterval;
        var anchor = valid.Start is { } start && start < dataEnd ? start : dataEnd;
        var referenceTime = valid.Period.Floor(anchor);

        // A settled window no longer changes, so the key drops latestOpenTime and the entry survives new candles.
        var settled = referenceTime <= dataEnd - _options.SettledAfter;
        var key = new SmaKey(valid.Symbol, valid.N, valid.Period, referenceTime, valid.SeriesCount, valid.IncludePoints, settled ? null : latest);

        return await GetOrComputeAsync(
            key,
            SmaEndpoint,
            settled,
            () => _inner.GetSmaAsync(query, cancellationToken),
            r => 1 + (r.Points?.Count ?? 0) + (r.Series?.Count ?? 0)).ConfigureAwait(false);
    }

    private async Task<DateTimeOffset?> GetLatestOpenTimeAsync(SymbolName name, CancellationToken cancellationToken)
    {
        var info = await _snapshot.FindAsync(name, cancellationToken).ConfigureAwait(false);
        return info is null ? null : await _snapshot.GetLatestOpenTimeAsync(info.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AnalysisResult<T>> GetOrComputeAsync<T>(
        object key,
        string endpoint,
        bool settled,
        Func<Task<AnalysisResult<T>>> compute,
        Func<T, long> size)
        where T : class
    {
        if (_cache.Cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            ApiMetrics.AnalysisCacheLookups.Add(1, new("endpoint", endpoint), new("result", "hit"));
            return cached;
        }

        ApiMetrics.AnalysisCacheLookups.Add(1, new("endpoint", endpoint), new("result", "miss"));
        var result = await compute().ConfigureAwait(false);
        if (result.Value is { } value)
        {
            var entry = new MemoryCacheEntryOptions { Size = size(value) };
            if (settled)
            {
                entry.SlidingExpiration = _options.SettledSlidingExpiry;
            }
            else
            {
                entry.AbsoluteExpirationRelativeToNow = _options.RecentAbsoluteExpiry;
            }

            _cache.Cache.Set(key, value, entry);
        }

        return result;
    }

    private sealed record AverageKey(SymbolName Symbol, DateTimeOffset LatestOpenTime);

    private sealed record SmaKey(
        SymbolName Symbol,
        int N,
        TimePeriod Period,
        DateTimeOffset ReferenceTime,
        int? SeriesCount,
        bool IncludePoints,
        DateTimeOffset? LatestOpenTime);
}
