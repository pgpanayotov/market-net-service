using System.Text.Json;
using MarketData.Infrastructure.Notifications;

namespace MarketData.Ingestion.Symbols;

/// <summary>
/// <c>LISTEN symbol_changes</c> (ADR-007). On every (re)connect it reconciles all symbols,
/// so changes made while disconnected are not lost; each notification then re-applies one symbol.
/// </summary>
public sealed partial class SymbolChangeListener(
    SymbolSubscriptionManager subscriptions,
    PgListenerSettings settings,
    ILogger<SymbolChangeListener> logger,
    TimeProvider timeProvider)
    : PgNotificationListener(NotificationChannels.SymbolChanges, settings, logger, timeProvider)
{
    protected override Task OnConnectedAsync(CancellationToken cancellationToken) => subscriptions.ReconcileAsync(cancellationToken);

    protected override async Task OnNotificationAsync(string payload, CancellationToken cancellationToken)
    {
        var change = JsonSerializer.Deserialize(payload, NotificationJsonContext.Default.SymbolChangedPayload)
            ?? throw new JsonException("Empty symbol_changes payload.");
        LogChange(change.Symbol, change.Op);
        await subscriptions.ApplyAsync(change.SymbolId, change.Symbol, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Symbol change notified for {Symbol} ({Op}); re-reading it")]
    private partial void LogChange(string symbol, SymbolChangeOperation op);
}
