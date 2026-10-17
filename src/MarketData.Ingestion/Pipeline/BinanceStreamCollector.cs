using MarketData.Core.Domain;
using MarketData.Infrastructure.Binance;
using MarketData.Ingestion.Backfill;
using MarketData.Ingestion.Symbols;
using Microsoft.Extensions.Options;

namespace MarketData.Ingestion.Pipeline;

/// <summary>
/// Feeds closed candles from the stream into <see cref="CandlePipe"/> (the callback does no I/O), turns
/// reconnects into a tail backfill, and reconciles subscriptions with the database on start and every
/// <see cref="IngestionOptions.ReconcileInterval"/>, in case a <c>symbol_changes</c> hint was missed.
/// On shutdown it drops every subscription.
/// </summary>
public sealed partial class BinanceStreamCollector(
    IMarketDataStream stream,
    SymbolSubscriptionManager subscriptions,
    CandlePipe pipe,
    BackfillScheduler backfill,
    IngestionMetrics metrics,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<BinanceStreamCollector> logger) : BackgroundService
{
    /// <summary>
    /// Hooks the stream events before any subscription can exist. On .NET 10 <see cref="ExecuteAsync"/> runs
    /// on the thread pool, possibly after <c>SymbolChangeListener</c> has already subscribed, so hooking
    /// there could miss the first candles and reconnects. The host runs <c>StartAsync</c> during startup.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        stream.CandleClosed += OnCandleClosed;
        stream.ConnectionRestored += OnConnectionRestored;
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await subscriptions.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogReconcileFailed(ex);
                }

                await Task.Delay(options.Value.ReconcileInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
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
