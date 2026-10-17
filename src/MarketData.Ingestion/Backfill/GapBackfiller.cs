using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Infrastructure;
using MarketData.Infrastructure.Binance;
using MarketData.Ingestion.Leadership;
using MarketData.Ingestion.Pipeline;
using Microsoft.Extensions.Options;

namespace MarketData.Ingestion.Backfill;

/// <summary>
/// Makes the store converge to complete (ADR-005), one request at a time:
/// <list type="bullet">
///   <item><b>Tail</b>: from the newest stored candle to the last closed minute. Runs at the start of a
///   term, after a WebSocket reconnect, after database recovery, and for each newly subscribed symbol
///   (capped at <see cref="IngestionOptions.BackfillOnAdd"/>).</item>
///   <item><b>Scan</b>: holes behind the newest candle within <see cref="IngestionOptions.GapScanWindow"/>.
///   Runs every <see cref="IngestionOptions.GapScanInterval"/>, and after drops or database recovery.</item>
/// </list>
/// Each run recomputes what is missing from the database, so a failed request is simply queued again (after a backoff).
/// </summary>
public sealed partial class GapBackfiller(
    BackfillScheduler scheduler,
    IKlineRestClient rest,
    IServiceScopeFactory scopes,
    IngestionState state,
    IngestionMetrics metrics,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<GapBackfiller> logger) : ILeaderService
{
    private static readonly TimeSpan Minute = MarketDataConstants.CandleInterval;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    private readonly IngestionOptions _options = options.Value;

    public async Task RunAsync(CancellationToken leadershipToken)
    {
        scheduler.RequestTail(BackfillReason.Startup);
        scheduler.RequestScan(BackfillReason.Startup);
        var nextScan = timeProvider.GetUtcNow() + _options.GapScanInterval;
        var failures = 0;
        try
        {
            while (true)
            {
                var wait = nextScan - timeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await scheduler.WaitAsync(wait, timeProvider, leadershipToken).ConfigureAwait(false);
                }

                if (timeProvider.GetUtcNow() >= nextScan)
                {
                    scheduler.RequestScan(BackfillReason.Periodic);
                }

                var requests = scheduler.TakePending();
                var failed = false;
                foreach (var request in requests)
                {
                    if (request.Kind == BackfillKind.Scan)
                    {
                        nextScan = timeProvider.GetUtcNow() + _options.GapScanInterval;
                    }

                    if (!await RunAsync(request, leadershipToken).ConfigureAwait(false))
                    {
                        failed = true;
                        scheduler.Request(request);
                    }
                }

                // A failing dependency (database, REST) would otherwise be hit again by every new request.
                failures = failed ? failures + 1 : 0;
                if (failed)
                {
                    await Task.Delay(ExponentialBackoff.Delay(failures - 1, InitialRetryDelay, MaxRetryDelay), timeProvider, leadershipToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (leadershipToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Requests of an ended term are moot; the next term starts with its own startup tail and scan.
            scheduler.Clear();
        }
    }

    /// <summary>Runs one request for each of its symbols; <c>false</c> when any symbol failed.</summary>
    private async Task<bool> RunAsync(BackfillRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<SymbolInfo> symbols;
        try
        {
            symbols = request.Symbol is { } one ? [one] : await GetActiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogRunFailed(ex, request.Kind, request.Reason, "*");
            return false;
        }

        var ok = true;
        foreach (var symbol in symbols)
        {
            try
            {
                var candles = request.Kind == BackfillKind.Tail
                    ? await TailAsync(symbol, request.Reason, cancellationToken).ConfigureAwait(false)
                    : await ScanAsync(symbol, request.Reason, cancellationToken).ConfigureAwait(false);
                if (candles > 0)
                {
                    LogBackfilled(candles, symbol.Name.Value, request.Kind, request.Reason);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogRunFailed(ex, request.Kind, request.Reason, symbol.Name.Value);
                ok = false;
            }
        }

        return ok;
    }

    private async Task<int> TailAsync(SymbolInfo symbol, BackfillReason reason, CancellationToken cancellationToken)
    {
        DateTimeOffset? latest;
        using (var scope = scopes.CreateScope())
        {
            latest = await scope.ServiceProvider.GetRequiredService<ICandleQueryRepository>()
                .GetLatestOpenTimeAsync(symbol.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        var end = FloorToMinute(timeProvider.GetUtcNow());
        var cap = end - _options.BackfillOnAdd;
        var start = latest switch
        {
            null => cap,
            { } l when reason == BackfillReason.SymbolAdded => Max(l + Minute, cap),
            { } l => l + Minute,
        };
        return start < end ? await FetchAsync(symbol, new TimeRange(start, end), reason, cancellationToken).ConfigureAwait(false) : 0;
    }

    private async Task<int> ScanAsync(SymbolInfo symbol, BackfillReason reason, CancellationToken cancellationToken)
    {
        // The newest minute is left to the WebSocket, whose final kline lands a few seconds after the close.
        var now = FloorToMinute(timeProvider.GetUtcNow());
        IReadOnlyList<TimeRange> gaps;
        using (var scope = scopes.CreateScope())
        {
            gaps = await scope.ServiceProvider.GetRequiredService<ICandleIngestRepository>()
                .FindMissingRangesAsync(symbol.Id, now - _options.GapScanWindow, now - Minute, cancellationToken)
                .ConfigureAwait(false);
        }

        state.RecordGap(symbol, (long)gaps.Sum(g => g.Length / Minute));
        var written = 0;
        foreach (var gap in gaps)
        {
            written += await FetchAsync(symbol, gap, reason, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>Fetches and upserts page by page, so a long backfill makes progress even if it fails midway.</summary>
    private async Task<int> FetchAsync(SymbolInfo symbol, TimeRange range, BackfillReason reason, CancellationToken cancellationToken)
    {
        var written = 0;
        await foreach (var page in rest.GetClosedCandlesAsync(symbol, range, cancellationToken).ConfigureAwait(false))
        {
            using (var scope = scopes.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ICandleIngestRepository>().UpsertAsync(page, cancellationToken).ConfigureAwait(false);
            }

            written += page.Count;
            metrics.BackfillCandles.Add(
                page.Count,
                IngestionMetrics.Symbol(symbol.Name.Value),
                new KeyValuePair<string, object?>("reason", ReasonTag(reason)));
        }

        return written;
    }

    private async Task<IReadOnlyList<SymbolInfo>> GetActiveAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISymbolRepository>().GetActiveAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReasonTag(BackfillReason reason) => reason switch
    {
        BackfillReason.Startup => "startup",
        BackfillReason.Reconnect => "reconnect",
        BackfillReason.SymbolAdded => "symbol_added",
        BackfillReason.Periodic => "periodic",
        BackfillReason.Dropped => "dropped",
        BackfillReason.DbRecovered => "db_recovered",
        _ => "other",
    };

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset FloorToMinute(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % Minute.Ticks), TimeSpan.Zero);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backfilled {Count} candles for {Symbol} ({Kind}, {Reason})")]
    private partial void LogBackfilled(int count, string symbol, BackfillKind kind, BackfillReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backfill {Kind} ({Reason}) failed for {Symbol}; it is queued again")]
    private partial void LogRunFailed(Exception exception, BackfillKind kind, BackfillReason reason, string symbol);
}
