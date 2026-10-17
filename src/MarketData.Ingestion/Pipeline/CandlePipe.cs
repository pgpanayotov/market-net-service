using System.Threading.Channels;
using MarketData.Core.Domain;
using MarketData.Ingestion.Backfill;
using Microsoft.Extensions.Options;

namespace MarketData.Ingestion.Pipeline;

/// <summary>
/// The bounded in-memory hand-off from the stream callback to <see cref="CandleWriter"/>. When full it
/// drops the oldest candle and asks for a gap scan: losing candles here is fine, because the scan
/// re-fetches them over REST (ADR-005).
/// </summary>
public sealed class CandlePipe
{
    private readonly Channel<Candle> _channel;

    public CandlePipe(IOptions<IngestionOptions> options, BackfillScheduler backfill, IngestionMetrics metrics)
    {
        _channel = Channel.CreateBounded<Candle>(
            new BoundedChannelOptions(options.Value.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ =>
            {
                metrics.CandlesDropped.Add(1);
                backfill.RequestScan(BackfillReason.Dropped);
            });
        metrics.ObserveChannelDepth(() => _channel.Reader.Count);
    }

    public ChannelReader<Candle> Reader => _channel.Reader;

    /// <summary>Never blocks; with <c>DropOldest</c> the write always succeeds.</summary>
    public void Write(Candle candle) => _channel.Writer.TryWrite(candle);
}
