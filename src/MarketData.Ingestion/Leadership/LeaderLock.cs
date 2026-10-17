using Microsoft.Extensions.Options;
using Npgsql;

namespace MarketData.Ingestion.Leadership;

/// <summary>
/// Leader election with <c>pg_try_advisory_lock</c> held on a dedicated session (ADR-007). The lock lives
/// as long as that session: when the process dies or the connection drops, Postgres releases it and a
/// standby takes over on its next attempt. While leader, the session is probed regularly; a failed probe
/// ends the term, which stops every <see cref="ILeaderService"/> before the role becomes standby again.
/// </summary>
/// <remarks>
/// Between the session dying and the old leader noticing (at most <see cref="IngestionOptions.LeaderCheckInterval"/>)
/// two workers can write at once. That is harmless: every write is an idempotent upsert.
/// </remarks>
public sealed partial class LeaderLock(
    IEnumerable<ILeaderService> services,
    LeadershipState state,
    IngestionConnection connection,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<LeaderLock> logger) : BackgroundService
{
    public const string ApplicationName = "marketdata-leader";

    private readonly IngestionOptions _options = options.Value;
    private bool _announcedStandby;
    private readonly string _connectionString = new NpgsqlConnectionStringBuilder(connection.ConnectionString)
    {
        Pooling = false,
        KeepAlive = 10,
        ApplicationName = ApplicationName,
    }.ConnectionString;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var session = new NpgsqlConnection(_connectionString);
                await session.OpenAsync(stoppingToken).ConfigureAwait(false);
                if (await TryLockAsync(session, stoppingToken).ConfigureAwait(false))
                {
                    await LeadAsync(session, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    // Announce standby once per transition, not on every retry.
                    if (!_announcedStandby)
                    {
                        LogStandby(_options.LeaderRetryInterval.TotalSeconds);
                        _announcedStandby = true;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogLockFailed(ex);
            }
            finally
            {
                state.BecomeStandby();
            }

            try
            {
                await Task.Delay(_options.LeaderRetryInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> TryLockAsync(NpgsqlConnection session, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", session);
        command.Parameters.AddWithValue("key", _options.LeaderLockKey);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Runs one leadership term; returns when it ends. Closing the session afterwards releases the lock.</summary>
    private async Task LeadAsync(NpgsqlConnection session, CancellationToken stoppingToken)
    {
        using var term = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        state.BecomeLeader(timeProvider.GetUtcNow());
        _announcedStandby = false;
        LogLeader();

        var tasks = services.Select(s => Task.Run(() => s.RunAsync(term.Token), CancellationToken.None)).ToList();
        var watchdog = WatchSessionAsync(session, term.Token);
        var first = await Task.WhenAny([.. tasks, watchdog]).ConfigureAwait(false);

        // Demote before anything else, so readiness stops claiming leadership while services wind down.
        state.BecomeStandby();
        await term.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll([.. tasks, watchdog]).ConfigureAwait(false);
        }
        catch (Exception) when (term.IsCancellationRequested)
        {
            // Cancellation of the remaining services; the cause is logged below.
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            if (first == watchdog)
            {
                LogLeadershipLost(first.Exception?.GetBaseException());
            }
            else
            {
                LogServiceEnded(first.Exception?.GetBaseException());
            }
        }
    }

    /// <summary>Completes (with an exception) once the lock session stops answering.</summary>
    private async Task WatchSessionAsync(NpgsqlConnection session, CancellationToken cancellationToken)
    {
        await using var probe = new NpgsqlCommand("SELECT 1", session);
        while (true)
        {
            await Task.Delay(_options.LeaderCheckInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Acquired the leader lock; starting ingestion")]
    private partial void LogLeader();

    [LoggerMessage(Level = LogLevel.Information, Message = "Another worker holds the leader lock; standing by (retry in {Seconds:F0}s)")]
    private partial void LogStandby(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Leader lock attempt failed")]
    private partial void LogLockFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the leader lock session; ingestion stopped")]
    private partial void LogLeadershipLost(Exception? exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A leader service ended unexpectedly; releasing the leader lock")]
    private partial void LogServiceEnded(Exception? exception);
}
