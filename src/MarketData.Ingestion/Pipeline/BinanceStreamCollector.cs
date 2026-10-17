using MarketData.Core.Domain;
using MarketData.Infrastructure.Binance;
using MarketData.Ingestion.Backfill;
using MarketData.Ingestion.Leadership;
using MarketData.Ingestion.Symbols;
using Microsoft.Extensions.Options;

namespace MarketData.Ingestion.Pipeline;

/// <summary>
/// Feeds closed candles from the stream into <see cref="CandlePipe"/> (the callback does no I/O), turns
/// reconnects into a tail backfill, and reconciles subscriptions with the database on start and every
/// <see cref="IngestionOptions.ReconcileInterval"/>, in case a <c>symbol_changes</c> hint was missed.
/// On the end of the term it drops every subscription, so a standby holds no stream.
/// </summary>
public sealed partial class BinanceStreamCollector(
    IMarketDataStream stream,
    SymbolSubscriptionManager subscriptions,
    CandlePipe pipe,
    BackfillScheduler backfill,
    IngestionMetrics metrics,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<BinanceStreamCollector> logger) : ILeaderService
{
    public async Task RunAsync(CancellationToken leadershipToken)
    {
        stream.CandleClosed += OnCandleClosed;
        stream.ConnectionRestored += OnConnectionRestored;
        try
        {
            while (true)
            {
                try
                {
                    await subscriptions.ReconcileAsync(leadershipToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!leadershipToken.IsCancellationRequested)
                {
                    LogReconcileFailed(ex);
                }

                await Task.Delay(options.Value.ReconcileInterval, timeProvider, leadershipToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (leadershipToken.IsCancellationRequested)
        {
        }
        finally
        {
            stream.CandleClosed -= OnCandleClosed;
            stream.ConnectionRestored -= OnConnectionRestored;
            await subscriptions.UnsubscribeAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnCandleClosed(object? sender, Candle candle)
    {
        var closeTime = candle.OpenTime + MarketDataConstants.CandleInterval;
        metrics.IngestLag.Record((candle.ReceivedAt - closeTime).TotalSeconds);
        pipe.Write(candle);
    }

    private void OnConnectionRestored(object? sender, EventArgs e)
    {
        metrics.WsReconnects.Add(1);
        backfill.RequestTail(BackfillReason.Reconnect);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciling subscriptions with the database failed; retrying on the next interval")]
    private partial void LogReconcileFailed(Exception exception);
}
