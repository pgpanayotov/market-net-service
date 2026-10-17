using System.Text.Json;
using System.Threading.Channels;
using MarketData.Infrastructure.Notifications;
using Npgsql;

namespace MarketData.Api.IntegrationTests;

/// <summary>A plain <c>LISTEN symbol_changes</c> session that records every payload.</summary>
public sealed class SymbolChangesRecorder : IAsyncDisposable
{
    private const string Sentinel = "sentinel";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly NpgsqlConnection _connection;
    private readonly Channel<string> _received = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _connectionString;
    private Task _loop = Task.CompletedTask;

    private SymbolChangesRecorder(string connectionString)
    {
        _connectionString = connectionString;
        _connection = new NpgsqlConnection(connectionString);
        _connection.Notification += (_, e) => _received.Writer.TryWrite(e.Payload);
    }

    public static async Task<SymbolChangesRecorder> StartAsync(string connectionString, CancellationToken cancellationToken)
    {
        var recorder = new SymbolChangesRecorder(connectionString);
        await recorder._connection.OpenAsync(cancellationToken);
        await using (var command = new NpgsqlCommand($"LISTEN {NotificationChannels.SymbolChanges}", recorder._connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        recorder._loop = recorder.WaitLoopAsync();
        return recorder;
    }

    public async Task<SymbolChangedPayload> NextAsync(CancellationToken cancellationToken)
    {
        var payload = await _received.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken);
        Assert.NotEqual(Sentinel, payload);
        return JsonSerializer.Deserialize(payload, NotificationJsonContext.Default.SymbolChangedPayload)!;
    }

    /// <summary>
    /// Asserts that nothing else was notified: notifications arrive in commit order, so a sentinel
    /// sent now must be the next one received.
    /// </summary>
    public async Task AssertNothingElseAsync(CancellationToken cancellationToken)
    {
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_notify(@c, @p)", connection);
            command.Parameters.AddWithValue("c", NotificationChannels.SymbolChanges);
            command.Parameters.AddWithValue("p", Sentinel);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var next = await _received.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken);
        Assert.Equal(Sentinel, next);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        await _connection.DisposeAsync();
        _stop.Dispose();
    }

    private async Task WaitLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await _connection.WaitAsync(_stop.Token);
        }
    }
}
