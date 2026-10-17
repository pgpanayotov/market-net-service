using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketData.Infrastructure.Notifications;

/// <param name="ConnectionString">Base connection string. Pooling is turned off for the listener (a <c>LISTEN</c> lives on one session).</param>
public sealed record PgListenerSettings(string ConnectionString)
{
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Long-running <c>LISTEN</c> on one channel (ADR-007). Notifications are hints, not a reliable
/// queue: they are lost while disconnected. So on every (re)connect the listener first issues
/// <c>LISTEN</c> and then calls <see cref="OnConnectedAsync"/> to reconcile from the database. No
/// change can fall between the two, and handlers must be idempotent.
/// </summary>
/// <remarks>
/// Uses a dedicated, non-pooled session. It must not go through a transaction-mode pooler such as PgBouncer.
/// </remarks>
public abstract partial class PgNotificationListener : BackgroundService
{
    private readonly PgListenerSettings _settings;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _connectionString;
    private volatile bool _isListening;

    protected PgNotificationListener(string channel, PgListenerSettings settings, ILogger logger, TimeProvider timeProvider)
    {
        PgChannel.Validate(channel);
        ChannelName = channel;
        _settings = settings;
        _logger = logger;
        _timeProvider = timeProvider;
        _connectionString = new NpgsqlConnectionStringBuilder(settings.ConnectionString)
        {
            Pooling = false,
            KeepAlive = 30,
            ApplicationName = ApplicationNameFor(channel),
        }.ConnectionString;
    }

    public string ChannelName { get; }

    /// <summary><c>true</c> between a successful <c>LISTEN</c> + reconcile and the next connection loss. Used by readiness checks.</summary>
    public bool IsListening => _isListening;

    /// <summary>Postgres <c>application_name</c> of the listener session (visible in <c>pg_stat_activity</c>).</summary>
    public static string ApplicationNameFor(string channel) => $"marketdata-listen-{channel}";

    /// <summary>Called after every successful <c>LISTEN</c>, before notifications are dispatched. A throw counts as a failed connection and is retried.</summary>
    protected abstract Task OnConnectedAsync(CancellationToken cancellationToken);

    /// <summary>Handles one payload. A throw is logged and counted; the connection stays up.</summary>
    protected abstract Task OnNotificationAsync(string payload, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(() => attempt = 0, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _isListening = false;
                var delay = RetryDelay(attempt++);
                MarketDataMetrics.ListenerReconnects.Add(1, new KeyValuePair<string, object?>("channel", ChannelName));
                LogConnectionLost(ex, ChannelName, delay.TotalMilliseconds);
                try
                {
                    await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ListenAsync(Action onReady, CancellationToken stoppingToken)
    {
        var pending = new ConcurrentQueue<string>();
        await using var connection = new NpgsqlConnection(_connectionString);
        connection.Notification += (_, e) =>
        {
            if (e.Channel == ChannelName)
            {
                pending.Enqueue(e.Payload);
            }
        };

        try
        {
            await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
            await using (var listen = new NpgsqlCommand($"LISTEN {ChannelName}", connection))
            {
                await listen.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
            }

            // Reconcile after LISTEN: anything committed from now on is either seen by the
            // reconcile or arrives as a notification (or both).
            await OnConnectedAsync(stoppingToken).ConfigureAwait(false);
            _isListening = true;
            onReady();
            LogListening(ChannelName);

            while (true)
            {
                await DispatchAsync(pending, stoppingToken).ConfigureAwait(false);
                await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _isListening = false;
        }
    }

    private async Task DispatchAsync(ConcurrentQueue<string> pending, CancellationToken stoppingToken)
    {
        while (pending.TryDequeue(out var payload))
        {
            var outcome = "ok";
            try
            {
                await OnNotificationAsync(payload, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                outcome = "error";
                LogHandlerFailed(ex, ChannelName, payload);
            }

            MarketDataMetrics.NotificationsReceived.Add(
                1,
                new KeyValuePair<string, object?>("channel", ChannelName),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>Exponential backoff with ±20% jitter, capped at <see cref="PgListenerSettings.MaxRetryDelay"/>.</summary>
    private TimeSpan RetryDelay(int attempt)
    {
        var exponential = _settings.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 16));
        var capped = Math.Min(exponential, _settings.MaxRetryDelay.TotalMilliseconds);
#pragma warning disable CA5394 // Jitter, not security.
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
#pragma warning restore CA5394
        return TimeSpan.FromMilliseconds(capped * jitter);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening on Postgres channel {Channel}")]
    private partial void LogListening(string channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Postgres listener for {Channel} lost its connection; retrying in {DelayMs:F0} ms")]
    private partial void LogConnectionLost(Exception exception, string channel, double delayMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handler for Postgres channel {Channel} failed on payload {Payload}")]
    private partial void LogHandlerFailed(Exception exception, string channel, string payload);
}
