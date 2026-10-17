using System.Text.Json;
using System.Threading.Channels;
using MarketData.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MarketData.Infrastructure.IntegrationTests;

public class NotificationTests(PostgresFixture postgres) : PostgresTest(postgres)
{
    private const string TestChannel = "test_events";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task CommittedNotification_IsDelivered()
    {
        await using var listener = await StartListenerAsync();
        var payload = new SymbolChangedPayload(SymbolChangeOperation.Add, 100, "SOLUSDT", At("2026-10-18T12:04:00Z"));

        await NotifyAsync(payload);

        Assert.Equal(payload, Deserialize(await NextAsync(listener.Received)));
    }

    [Fact]
    public async Task RolledBackNotification_IsNeverDelivered()
    {
        await using var listener = await StartListenerAsync();
        await using (var db = Postgres.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await new PgNotificationPublisher(db).NotifyAsync(TestChannel, Payload("ROLLEDBACK"), Ct);
            await transaction.RollbackAsync(Ct);
        }

        await NotifyAsync(Payload("COMMITTED"));

        // Notifications arrive in commit order, so if the rolled-back one had been sent it would come first.
        Assert.Equal("COMMITTED", Deserialize(await NextAsync(listener.Received)).Symbol);
    }

    [Fact]
    public async Task NotificationInsideTransaction_IsDeliveredOnCommit()
    {
        await using var listener = await StartListenerAsync();
        await using var db = Postgres.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await new PgNotificationPublisher(db).NotifyAsync(TestChannel, Payload("INTX"), Ct);

        await transaction.CommitAsync(Ct);

        Assert.Equal("INTX", Deserialize(await NextAsync(listener.Received)).Symbol);
    }

    [Fact]
    public async Task TerminatedBackend_ReconnectsAndReconcilesAgain()
    {
        await using var listener = await StartListenerAsync();

        await TerminateListenerSessionAsync();

        Assert.Equal(2, await NextAsync(listener.Connected));
        await NotifyAsync(Payload("AFTERKILL"));
        Assert.Equal("AFTERKILL", Deserialize(await NextAsync(listener.Received)).Symbol);
        Assert.True(listener.IsListening);
    }

    [Fact]
    public async Task FailingReconcile_IsRetried()
    {
        await using var listener = await StartListenerAsync(failFirstConnect: true);

        // Connect #1 threw inside OnConnectedAsync, so StartListenerAsync returned after connect #2.
        Assert.Equal(2, listener.ConnectedCount);
        await NotifyAsync(Payload("RETRIED"));
        Assert.Equal("RETRIED", Deserialize(await NextAsync(listener.Received)).Symbol);
    }

    [Fact]
    public async Task FailingHandler_KeepsConnection()
    {
        await using var listener = await StartListenerAsync(failOnSymbol: "POISONPILL");

        await NotifyAsync(Payload("POISONPILL"));
        await NotifyAsync(Payload("NEXTONE"));

        Assert.Equal("NEXTONE", Deserialize(await NextAsync(listener.Received)).Symbol);
        Assert.Equal(1, listener.ConnectedCount);
    }

    [Fact]
    public void Payload_SerializesToStableJson()
    {
        var symbolChanged = new SymbolChangedPayload(SymbolChangeOperation.Remove, 2, "ADAUSDT", At("2026-10-18T12:00:00Z"));

        Assert.Equal(
            """{"op":"remove","symbolId":2,"symbol":"ADAUSDT","changedAt":"2026-10-18T12:00:00+00:00"}""",
            JsonSerializer.Serialize(symbolChanged, NotificationJsonContext.Default.SymbolChangedPayload));
        Assert.Equal(
            symbolChanged,
            JsonSerializer.Deserialize("""{"op":"remove","symbolId":2,"symbol":"ADAUSDT","changedAt":"2026-10-18T12:00:00Z"}""", NotificationJsonContext.Default.SymbolChangedPayload));
    }

    [Fact]
    public async Task Publisher_RejectsOversizedPayloadAndInvalidChannel()
    {
        await using var db = Postgres.CreateContext();
        var publisher = new PgNotificationPublisher(db);

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.NotifyAsync(TestChannel, Payload(new string('X', 8000)), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.NotifyAsync("bad; DROP TABLE symbols", Payload("BTCUSDT"), Ct));
    }

    private static SymbolChangedPayload Payload(string symbol) => new(SymbolChangeOperation.Add, 1, symbol, At("2026-10-18T12:00:00Z"));

    private static SymbolChangedPayload Deserialize(string json) =>
        JsonSerializer.Deserialize(json, NotificationJsonContext.Default.SymbolChangedPayload)!;

    private static async Task<T> NextAsync<T>(Channel<T> channel) =>
        await channel.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);

    private async Task NotifyAsync(SymbolChangedPayload payload)
    {
        await using var db = Postgres.CreateContext();
        await new PgNotificationPublisher(db).NotifyAsync(TestChannel, payload, Ct);
    }

    private async Task TerminateListenerSessionAsync()
    {
        await using var connection = new NpgsqlConnection(Postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = @name", connection);
        command.Parameters.AddWithValue("name", PgNotificationListener.ApplicationNameFor(TestChannel));
        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
    }

    /// <summary>Starts a listener and returns once it is listening (after its first successful reconcile).</summary>
    private async Task<RecordingListener> StartListenerAsync(bool failFirstConnect = false, string? failOnSymbol = null)
    {
        var listener = new RecordingListener(Postgres.ConnectionString, failFirstConnect, failOnSymbol);
        await listener.StartAsync(Ct);
        var expected = failFirstConnect ? 2 : 1;
        while (await NextAsync(listener.Connected) < expected)
        {
        }

        return listener;
    }

    private sealed class RecordingListener(string connectionString, bool failFirstConnect, string? failOnSymbol)
        : PgNotificationListener(
            TestChannel,
            new PgListenerSettings(connectionString) { InitialRetryDelay = TimeSpan.FromMilliseconds(50), MaxRetryDelay = TimeSpan.FromMilliseconds(200) },
            NullLogger.Instance,
            TimeProvider.System),
          IAsyncDisposable
    {
        private int _connectedCount;

        public Channel<int> Connected { get; } = Channel.CreateUnbounded<int>();

        public Channel<string> Received { get; } = Channel.CreateUnbounded<string>();

        public int ConnectedCount => Volatile.Read(ref _connectedCount);

        public async ValueTask DisposeAsync()
        {
            await StopAsync(CancellationToken.None);
            Dispose();
        }

        protected override Task OnConnectedAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _connectedCount);
            Connected.Writer.TryWrite(count);
            if (failFirstConnect && count == 1)
            {
                throw new InvalidOperationException("Simulated reconcile failure.");
            }

            return Task.CompletedTask;
        }

        protected override Task OnNotificationAsync(string payload, CancellationToken cancellationToken)
        {
            if (failOnSymbol is not null && Deserialize(payload).Symbol == failOnSymbol)
            {
                throw new InvalidOperationException("Simulated handler failure.");
            }

            Received.Writer.TryWrite(payload);
            return Task.CompletedTask;
        }
    }
}
