using MarketData.Ingestion.Leadership;
using Npgsql;

namespace MarketData.Ingestion.IntegrationTests.Worker;

public sealed class LeadershipTests(PostgresFixture postgres) : WorkerTest(postgres)
{
    [Fact]
    public async Task Second_worker_stays_standby_and_takes_over_when_the_leader_session_drops()
    {
        // The first worker retries slowly after losing the lock, so the standby wins the race deterministically.
        var first = StartWorker(new Dictionary<string, string> { ["MarketData:LeaderRetryInterval"] = "00:01:00" });
        await EventuallyAsync(() => first.Leadership.IsLeader && first.Stream.SubscribedNames.Count == 3, "first worker leading");

        var second = StartWorker();
        await EventuallyAsync(async () => (await second.GetReadinessAsync(Ct)).Body.Role == "standby", "second worker reporting standby");

        // Several lock retries (every 300 ms) pass without the standby taking over or opening streams.
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        Assert.Equal(WorkerRole.Standby, second.Leadership.Role);
        Assert.Empty(second.Stream.Subscriptions);
        var (status, standby) = await second.GetReadinessAsync(Ct);
        Assert.Equal(200, status);
        Assert.Equal("standby", standby.Role);

        await TerminateLeaderSessionAsync();

        await EventuallyAsync(() => second.Leadership.IsLeader && second.Stream.SubscribedNames.Count == 3, "second worker taking over");
        await EventuallyAsync(() => !first.Leadership.IsLeader && first.Stream.Subscriptions.Count == 0, "first worker demoted and unsubscribed");
        Assert.Equal("standby", (await first.GetReadinessAsync(Ct)).Body.Role);
        Assert.Equal("leader", (await second.GetReadinessAsync(Ct)).Body.Role);
    }

    /// <summary>Kills the session holding the advisory lock, as a crashed process or network drop would.</summary>
    private async Task TerminateLeaderSessionAsync()
    {
        await using var connection = new NpgsqlConnection(Postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_locks WHERE locktype = 'advisory' AND granted",
            connection);
        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
    }
}
