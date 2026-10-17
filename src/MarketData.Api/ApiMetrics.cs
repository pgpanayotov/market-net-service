using System.Diagnostics.Metrics;
using MarketData.Infrastructure;

namespace MarketData.Api;

/// <summary>API instruments, on the shared <see cref="MarketDataMetrics.MeterName"/> meter.</summary>
public static class ApiMetrics
{
    private static readonly Meter Meter = new(MarketDataMetrics.MeterName);

    /// <summary>Analysis cache lookups, tagged by <c>endpoint</c> and <c>result</c> (hit|miss).</summary>
    public static readonly Counter<long> AnalysisCacheLookups =
        Meter.CreateCounter<long>("marketdata.analysis_cache.lookups", description: "Analysis result cache lookups");
}
