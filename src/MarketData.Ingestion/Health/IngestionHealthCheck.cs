using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Infrastructure.Binance;
using MarketData.Ingestion.Leadership;
using MarketData.Ingestion.Pipeline;
using MarketData.Ingestion.Symbols;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace MarketData.Ingestion.Health;

/// <summary>
/// The worker's role, and for the leader whether ingestion works (system-design §7): the WebSocket is
/// connected, the <c>symbol_changes</c> listener is listening, and every active symbol's newest stored
/// candle closed within <see cref="IngestionOptions.FreshnessThreshold"/>. A standby is healthy: it is a
/// hot spare, not a failure.
/// </summary>
/// <remarks>
/// Freshness is read from the database (one index probe per symbol) rather than from writer state, so
/// candles stored by backfill count too. A symbol gets the threshold as grace after the term starts or
/// after it is subscribed, whichever is later.
/// </remarks>
public sealed class IngestionHealthCheck(
    LeadershipState leadership,
    IMarketDataStream stream,
    SymbolChangeListener listener,
    IngestionState state,
    ISymbolRepository symbols,
    ICandleQueryRepository candles,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public const string RoleKey = "role";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var since = leadership.LeaderSince;
        if (since is null)
        {
            return HealthCheckResult.Healthy("Standby: another worker holds the leader lock.", Data(WorkerRole.Standby));
        }

        var problems = new List<string>();
        if (!stream.IsConnected)
        {
            problems.Add("Binance WebSocket disconnected");
        }

        if (!listener.IsListening)
        {
            problems.Add("symbol_changes listener not connected");
        }

        var threshold = options.Value.FreshnessThreshold;
        var now = timeProvider.GetUtcNow();
        var active = await symbols.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var latest = await candles.GetLatestOpenTimesAsync(cancellationToken).ConfigureAwait(false);
        var stale = active
            .Where(s =>
            {
                var graceStart = state.SubscribedAt(s.Id) is { } at && at > since.Value ? at : since.Value;
                var fresh = latest.TryGetValue(s.Id, out var open) && now - (open + MarketDataConstants.CandleInterval) <= threshold;
                return !fresh && now - graceStart > threshold;
            })
            .Select(s => s.Name.Value)
            .ToList();
        if (stale.Count > 0)
        {
            problems.Add($"no candle within {threshold.TotalMinutes:0.#} min for {string.Join(", ", stale)}");
        }

        return problems.Count == 0
            ? HealthCheckResult.Healthy($"Leader: ingesting {active.Count} symbol(s).", Data(WorkerRole.Leader))
            : HealthCheckResult.Unhealthy($"Leader: {string.Join("; ", problems)}.", data: Data(WorkerRole.Leader));
    }

    private static Dictionary<string, object> Data(WorkerRole role) => new() { [RoleKey] = role == WorkerRole.Leader ? "leader" : "standby" };
}
