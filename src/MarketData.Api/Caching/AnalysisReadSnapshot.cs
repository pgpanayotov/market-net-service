using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Core.Enums;

namespace MarketData.Api.Caching;

/// <summary>
/// Read-only view for one analysis request that remembers the symbol lookup and the newest-candle
/// probe, so the cache key and the computation see the same data version with one query each.
/// Writes are not supported: admin endpoints use the real repository.
/// </summary>
internal sealed class AnalysisReadSnapshot(ISymbolRepository symbols, ICandleQueryRepository candles)
    : ISymbolRepository, ICandleQueryRepository
{
    private readonly Dictionary<SymbolName, SymbolInfo?> _symbols = [];
    private readonly Dictionary<int, DateTimeOffset?> _latest = [];

    public async Task<SymbolInfo?> FindAsync(SymbolName name, CancellationToken cancellationToken)
    {
        if (!_symbols.TryGetValue(name, out var info))
        {
            info = await symbols.FindAsync(name, cancellationToken).ConfigureAwait(false);
            _symbols[name] = info;
        }

        return info;
    }

    public async Task<DateTimeOffset?> GetLatestOpenTimeAsync(int symbolId, CancellationToken cancellationToken)
    {
        if (!_latest.TryGetValue(symbolId, out var latest))
        {
            latest = await candles.GetLatestOpenTimeAsync(symbolId, cancellationToken).ConfigureAwait(false);
            _latest[symbolId] = latest;
        }

        return latest;
    }

    public Task<CloseAverage?> GetAverageCloseAsync(int symbolId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        candles.GetAverageCloseAsync(symbolId, from, to, cancellationToken);

    public Task<IReadOnlyList<BucketClose>> GetBucketClosesAsync(
        int symbolId,
        TimePeriod period,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken) =>
        candles.GetBucketClosesAsync(symbolId, period, from, to, limit, cancellationToken);

    public Task<DateTimeOffset?> GetFirstOpenTimeAsync(int symbolId, CancellationToken cancellationToken) =>
        candles.GetFirstOpenTimeAsync(symbolId, cancellationToken);

    public Task<IReadOnlyDictionary<int, DateTimeOffset>> GetLatestOpenTimesAsync(CancellationToken cancellationToken) =>
        candles.GetLatestOpenTimesAsync(cancellationToken);

    public Task<IReadOnlyList<SymbolDetails>> ListAsync(CancellationToken cancellationToken) =>
        symbols.ListAsync(cancellationToken);

    public Task<IReadOnlyList<SymbolInfo>> GetActiveAsync(CancellationToken cancellationToken) =>
        symbols.GetActiveAsync(cancellationToken);

    public Task<(SymbolActivation Outcome, SymbolInfo Symbol)> ActivateAsync(SymbolName name, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The analysis snapshot is read-only.");

    public Task<SymbolDeactivation> DeactivateAsync(SymbolName name, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The analysis snapshot is read-only.");
}
