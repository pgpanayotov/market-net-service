namespace MarketData.Ingestion.Leadership;

public enum WorkerRole
{
    Standby,
    Leader,
}

/// <summary>
/// Work that runs only on the leader (ADR-007). <see cref="LeaderLock"/> starts every registered service
/// when the lock is taken and cancels the token when leadership ends. A service runs until then; returning
/// early or throwing ends the whole term, after which the lock is released and retried.
/// </summary>
public interface ILeaderService
{
    Task RunAsync(CancellationToken leadershipToken);
}

/// <summary>This worker's current role, for readiness and the <c>worker_is_leader</c> gauge.</summary>
public sealed class LeadershipState
{
    private volatile Term? _term;

    public WorkerRole Role => _term is null ? WorkerRole.Standby : WorkerRole.Leader;

    public bool IsLeader => _term is not null;

    /// <summary>When the current leadership term started; <c>null</c> on a standby.</summary>
    public DateTimeOffset? LeaderSince => _term?.Since;

    internal void BecomeLeader(DateTimeOffset since) => _term = new Term(since);

    internal void BecomeStandby() => _term = null;

    private sealed record Term(DateTimeOffset Since);
}
