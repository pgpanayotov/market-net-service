using System.Diagnostics.Metrics;
using MarketData.Infrastructure;

namespace MarketData.Ingestion;

/// <summary>
/// Ingestion instruments (system-design §7) on the shared <see cref="MarketDataMetrics.MeterName"/> meter,
/// so one <c>AddMeter</c> exports them with the listener metrics. A Prometheus exporter appends <c>_total</c> to counters.
/// </summary>
public sealed class IngestionMetrics : IDisposable
{
    private readonly Meter _meter = new(MarketDataMetrics.MeterName);

    public IngestionMetrics()
    {
        CandlesIngested = _meter.CreateCounter<long>("marketdata.candles.ingested", description: "Closed candles from the WebSocket written to the database, by symbol");
        IngestLag = _meter.CreateHistogram<double>("marketdata.ingest.lag", unit: "s", description: "Time from a candle's close to its arrival (received_at - close_time)");
        WsReconnects = _meter.CreateCounter<long>("marketdata.ws.reconnects", description: "Binance WebSocket reconnects");
        BackfillCandles = _meter.CreateCounter<long>("marketdata.backfill.candles", description: "Candles fetched over REST and written, by symbol and reason");
        CandlesDropped = _meter.CreateCounter<long>("marketdata.channel.dropped", description: "Candles dropped because the writer channel was full");
        WriteFailures = _meter.CreateCounter<long>("marketdata.writer.failures", description: "Failed candle batch writes (retried)");
    }

    public Counter<long> CandlesIngested { get; }

    public Histogram<double> IngestLag { get; }

    public Counter<long> WsReconnects { get; }

    public Counter<long> BackfillCandles { get; }

    public Counter<long> CandlesDropped { get; }

    public Counter<long> WriteFailures { get; }

    public void ObserveChannelDepth(Func<int> depth) =>
        _meter.CreateObservableGauge("marketdata.channel.depth", depth, description: "Candles waiting in the writer channel");

    public void ObserveGaps(Func<IEnumerable<Measurement<long>>> gaps) =>
        _meter.CreateObservableGauge("marketdata.gap.minutes", gaps, description: "Missing minutes found by the latest gap scan, by symbol");

    public void Dispose() => _meter.Dispose();

    public static KeyValuePair<string, object?> Symbol(string symbol) => new("symbol", symbol);
}
