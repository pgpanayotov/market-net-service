namespace MarketData.Ingestion;

/// <summary>Worker settings, bound from the <c>MarketData</c> section (e.g. <c>MarketData:HistoryDepth</c>).</summary>
public sealed class IngestionOptions
{
    public const string SectionName = "MarketData";

    /// <summary>
    /// How much history every active symbol should have, counted back from now. Replaces the former
    /// <c>BackfillOnAdd</c>. At the start of a leader term and when a symbol is added or reactivated, a
    /// symbol with no data is filled from <c>now − HistoryDepth</c>, and one whose oldest candle is
    /// younger than that is deepened backwards. The default covers a 1w SMA with n up to 5.
    /// </summary>
    public TimeSpan HistoryDepth { get; set; } = TimeSpan.FromDays(35);

    /// <summary>Capacity of the in-memory candle channel between the stream and the writer. Oldest candles are dropped when full.</summary>
    public int ChannelCapacity { get; set; } = 10_000;

    /// <summary>Most rows per upsert.</summary>
    public int WriteBatchSize { get; set; } = 500;

    /// <summary>Longest a candle waits in the writer for its batch to fill.</summary>
    public TimeSpan WriteBatchWindow { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Cap of the writer's exponential retry delay while the database is unavailable.</summary>
    public TimeSpan WriteMaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the gap scan runs (it also runs after drops and database recovery).</summary>
    public TimeSpan GapScanInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How far back the gap scan looks (ADR-005).</summary>
    public TimeSpan GapScanWindow { get; set; } = TimeSpan.FromHours(48);

    /// <summary>Periodic reconcile of active symbols against live subscriptions, in case a notification was missed.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often a standby retries the leader lock.</summary>
    public TimeSpan LeaderRetryInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the leader checks that its lock session is still alive.</summary>
    public TimeSpan LeaderCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Key of the <c>pg_try_advisory_lock</c> that elects the leader. Every worker of one deployment must use the same key.</summary>
    public long LeaderLockKey { get; set; } = 0x4D41_524B_4554; // "MARKET"

    /// <summary>Readiness fails for the leader when an active symbol's newest candle closed longer ago than this.</summary>
    public TimeSpan FreshnessThreshold { get; set; } = TimeSpan.FromMinutes(2);
}
