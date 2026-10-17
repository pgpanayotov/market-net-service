using System.Text.Json.Serialization;

namespace MarketData.Infrastructure.Notifications;

/// <summary>
/// Postgres <c>LISTEN/NOTIFY</c> channels (ADR-007). Only admin changes are signalled: they are the one
/// event the worker can't predict. New candles arrive every minute, and the API reads the newest one
/// with an index probe per request instead.
/// </summary>
public static class NotificationChannels
{
    /// <summary>API → worker: a symbol was added or removed. Sent in the same transaction as the <c>symbols</c> write.</summary>
    public const string SymbolChanges = "symbol_changes";
}

public enum SymbolChangeOperation
{
    [JsonStringEnumMemberName("add")]
    Add,

    [JsonStringEnumMemberName("remove")]
    Remove,
}

/// <summary>
/// A hint that a symbol changed. Receivers should re-read that symbol from the database rather than
/// trust <see cref="Op"/>, so reordered or duplicated notifications are harmless.
/// </summary>
public sealed record SymbolChangedPayload(SymbolChangeOperation Op, int SymbolId, string Symbol, DateTimeOffset ChangedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SymbolChangedPayload))]
public sealed partial class NotificationJsonContext : JsonSerializerContext;
