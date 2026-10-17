using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Infrastructure;
using MarketData.Ingestion.Backfill;
using MarketData.Ingestion.Leadership;
using Microsoft.Extensions.Options;

namespace MarketData.Ingestion.Pipeline;

/// <summary>
/// Drains <see cref="CandlePipe"/> in batches (up to <see cref="IngestionOptions.WriteBatchSize"/> rows or
/// <see cref="IngestionOptions.WriteBatchWindow"/>) into one idempotent upsert each. While the database is
/// down it retries the batch with capped exponential backoff and the pipe drops its oldest candles; on
/// recovery it asks for a tail backfill and a gap scan, which restore whatever was dropped (ADR-005).
/// It sends no notification: the API reads the newest candle per request.
/// </summary>
public sealed partial class CandleWriter(
    CandlePipe pipe,
    IServiceScopeFactory scopes,
    BackfillScheduler backfill,
    IngestionState state,
    IngestionMetrics metrics,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<CandleWriter> logger) : ILeaderService
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly IngestionOptions _options = options.Value;

    public async Task RunAsync(CancellationToken leadershipToken)
    {
        var batch = new List<Candle>(_options.WriteBatchSize);
        try
        {
            while (await pipe.Reader.WaitToReadAsync(leadershipToken).ConfigureAwait(false))
            {
                await FillBatchAsync(batch, leadershipToken).ConfigureAwait(false);
                await WriteWithRetryAsync(batch, leadershipToken).ConfigureAwait(false);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (leadershipToken.IsCancellationRequested)
        {
            // Unwritten candles are recovered by the next leader's tail backfill and gap scan.
        }
    }

    private async Task FillBatchAsync(List<Candle> batch, CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + _options.WriteBatchWindow;
        while (true)
        {
            while (batch.Count < _options.WriteBatchSize && pipe.Reader.TryRead(out var candle))
            {
                batch.Add(candle);
            }

            var remaining = deadline - timeProvider.GetUtcNow();
            if (batch.Count >= _options.WriteBatchSize || remaining <= TimeSpan.Zero)
            {
                return;
            }

            using var window = new CancellationTokenSource(remaining, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Token);
            try
            {
                if (!await pipe.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task WriteWithRetryAsync(List<Candle> batch, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            try
            {
                using (var scope = scopes.CreateScope())
                {
                    var repository = scope.ServiceProvider.GetRequiredService<ICandleIngestRepository>();
                    await repository.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
                }

                foreach (var group in batch.GroupBy(c => c.SymbolId))
                {
                    metrics.CandlesIngested.Add(group.Count(), IngestionMetrics.Symbol(state.NameOf(group.Key)));
                }

                if (failures > 0)
                {
                    LogRecovered(failures);
                    backfill.RequestTail(BackfillReason.DbRecovered);
                    backfill.RequestScan(BackfillReason.DbRecovered);
                }

                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                metrics.WriteFailures.Add(1);
                var delay = ExponentialBackoff.Delay(failures++, InitialRetryDelay, _options.WriteMaxRetryDelay);
                LogWriteFailed(ex, batch.Count, delay.TotalMilliseconds);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing {Count} candles failed; retrying in {DelayMs:F0} ms")]
    private partial void LogWriteFailed(Exception exception, int count, double delayMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Candle writes recovered after {Failures} failed attempts; requesting backfill and gap scan")]
    private partial void LogRecovered(int failures);
}
