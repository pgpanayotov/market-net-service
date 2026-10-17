using System.Diagnostics.Metrics;

namespace MarketData.Infrastructure;

/// <summary>Instruments shared by both hosts. Hosts export the meter with OpenTelemetry (<c>AddMeter(MeterName)</c>).</summary>
public static class MarketDataMetrics
{
    public const string MeterName = "MarketData";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Listener reconnects after a lost or failed connection, tagged by <c>channel</c>.</summary>
    public static readonly Counter<long> ListenerReconnects =
        Meter.CreateCounter<long>("marketdata.pg_listener.reconnects", description: "Postgres LISTEN connection re-establishments");

    /// <summary>Notifications delivered to a handler, tagged by <c>channel</c> and <c>outcome</c> (ok|error).</summary>
    public static readonly Counter<long> NotificationsReceived =
        Meter.CreateCounter<long>("marketdata.notifications.received", description: "Postgres notifications handled");
}
