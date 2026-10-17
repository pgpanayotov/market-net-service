using MarketData.Core.Domain;
using MarketData.Infrastructure.Notifications;
using MarketData.Infrastructure.Persistence;

using Microsoft.Extensions.Time.Testing;

namespace MarketData.Ingestion.IntegrationTests.Worker;

/// <summary>
/// <c>symbol_changes</c> drives subscriptions. The periodic reconcile is set to an hour, so these tests
/// see the notification path (and the reconcile-on-connect path), not the 60s safety net.
/// </summary>
public sealed class SymbolChangesTests(PostgresFixture postgres) : WorkerTest(postgres)
{
    [Fact]
    public async Task Added_symbol_is_subscribed_and_backfilled_and_removed_symbol_is_unsubscribed()
    {
        var worker = StartWorker();
        await EventuallyAsync(() => worker.Listener.IsListening && worker.Stream.SubscribedNames.Count == 3, "a listening worker");

        var sol = await ChangeAsync("SOLUSDT", SymbolChangeOperation.Add);

        await EventuallyAsync(() => worker.Stream.SubscribedNames.Contains("SOLUSDT"), "SOLUSDT subscribed");
        await EventuallyAsync(async () => (await CandlesAsync(sol.Id)).Count >= 30, "SOLUSDT backfilled (HistoryDepth = 30 min)");
        Assert.True((await CandlesAsync(sol.Id)).Count <= 31);

        await ChangeAsync("SOLUSDT", SymbolChangeOperation.Remove);

        await EventuallyAsync(() => !worker.Stream.SubscribedNames.Contains("SOLUSDT"), "SOLUSDT unsubscribed");
        Assert.Equal(3, worker.Stream.SubscribedNames.Count);
        Assert.NotEmpty(await CandlesAsync(sol.Id));
    }

    [Fact]
    public async Task Stale_or_duplicate_hints_are_checked_against_the_database()
    {
        var worker = StartWorker();
        await EventuallyAsync(() => worker.Listener.IsListening && worker.Stream.SubscribedNames.Count == 3, "a listening worker");

        // An "add" for a symbol that is not active in the database, and a "remove" for one that is: both are ignored.
        await NotifyOnlyAsync(new SymbolChangedPayload(SymbolChangeOperation.Add, 999, "XRPUSDT", DateTimeOffset.UtcNow));
        await NotifyOnlyAsync(new SymbolChangedPayload(SymbolChangeOperation.Remove, Btc, "BTCUSDT", DateTimeOffset.UtcNow));
        var sol = await ChangeAsync("SOLUSDT", SymbolChangeOperation.Add);
        await ChangeAsync("SOLUSDT", SymbolChangeOperation.Add, write: false);

        await EventuallyAsync(() => worker.Stream.SubscribedNames.Contains("SOLUSDT"), "SOLUSDT subscribed");
        Assert.Equal(["ADAUSDT", "BTCUSDT", "ETHUSDT", "SOLUSDT"], worker.Stream.SubscribedNames.Order());
        Assert.Equal(sol.Id, worker.Stream.Subscriptions.Single(s => s.Name.Value == "SOLUSDT").Id);
    }

    [Fact]
    public async Task Changes_made_while_no_worker_runs_are_applied_by_the_reconcile_on_connect()
    {
        await using (var db = Postgres.CreateContext())
        {
            var repository = new SymbolRepository(db, new FakeTimeProvider(DateTimeOffset.UtcNow));
            await repository.DeactivateAsync(Name("ADAUSDT"), Ct);
            await repository.ActivateAsync(Name("SOLUSDT"), Ct);
        }

        var worker = StartWorker();

        await EventuallyAsync(() => worker.Stream.SubscribedNames.SetEquals(["BTCUSDT", "ETHUSDT", "SOLUSDT"]), "subscriptions matching the database");
    }

    /// <summary>What the API does (ADR-007): the symbols write and its <c>pg_notify</c> in one transaction.</summary>
    private async Task<SymbolInfo> ChangeAsync(string symbol, SymbolChangeOperation op, bool write = true)
    {
        await using var db = Postgres.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(Ct);
        var repository = new SymbolRepository(db, TimeProvider.System);
        if (write && op == SymbolChangeOperation.Add)
        {
            await repository.ActivateAsync(Name(symbol), Ct);
        }
        else if (write)
        {
            await repository.DeactivateAsync(Name(symbol), Ct);
        }

        var info = (await repository.FindAsync(Name(symbol), Ct))!;
        await new PgNotificationPublisher(db).NotifyAsync(NotificationChannels.SymbolChanges, new SymbolChangedPayload(op, info.Id, symbol, DateTimeOffset.UtcNow), Ct);
        await tx.CommitAsync(Ct);
        return info;
    }

    private async Task NotifyOnlyAsync(SymbolChangedPayload payload)
    {
        await using var db = Postgres.CreateContext();
        await new PgNotificationPublisher(db).NotifyAsync(NotificationChannels.SymbolChanges, payload, Ct);
    }

    private static SymbolName Name(string value) => SymbolName.TryCreate(value, out var name) ? name : throw new ArgumentException(value);
}
