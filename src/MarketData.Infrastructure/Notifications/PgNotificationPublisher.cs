using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MarketData.Infrastructure.Persistence;

namespace MarketData.Infrastructure.Notifications;

public interface IPgNotificationPublisher
{
    /// <summary>
    /// Sends <c>pg_notify(channel, json)</c> on the scoped DbContext's connection. Inside an open
    /// transaction the notification is delivered only if that transaction commits (ADR-007).
    /// </summary>
    Task NotifyAsync<T>(string channel, T payload, CancellationToken cancellationToken);
}

public sealed class PgNotificationPublisher(MarketDataDbContext db) : IPgNotificationPublisher
{
    /// <summary>Postgres rejects payloads of 8000 bytes or more.</summary>
    public const int MaxPayloadBytes = 7999;

    public async Task NotifyAsync<T>(string channel, T payload, CancellationToken cancellationToken)
    {
        PgChannel.Validate(channel);
        var typeInfo = (JsonTypeInfo<T>)NotificationJsonContext.Default.GetTypeInfo(typeof(T))!;
        var json = JsonSerializer.Serialize(payload, typeInfo);
        if (Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes)
        {
            throw new ArgumentException($"Notification payload exceeds {MaxPayloadBytes} bytes.", nameof(payload));
        }

        await using var command = await db.CreateCommandAsync("SELECT pg_notify(@channel, @payload)", cancellationToken).ConfigureAwait(false);
        command.With("channel", channel).With("payload", json);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
