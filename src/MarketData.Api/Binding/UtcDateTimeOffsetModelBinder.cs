using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace MarketData.Api.Binding;

/// <summary>
/// Binds an ISO-8601 timestamp, reading a value without an offset as UTC (system-design §5.1). The
/// default binder would read it in the server's local time zone.
/// </summary>
public sealed class UtcDateTimeOffsetModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (value == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, value);
        var text = value.FirstValue;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        if (TryParse(text, out var parsed)
            // An unencoded '+' in a query string arrives as a space: "12:00:00 02:00".
            || (text.Contains(' ', StringComparison.Ordinal) && TryParse(text.Replace(' ', '+'), out parsed)))
        {
            bindingContext.Result = ModelBindingResult.Success(parsed);
        }
        else
        {
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, "Must be an ISO-8601 date-time, e.g. 2026-10-18T12:00:00Z.");
        }

        return Task.CompletedTask;
    }

    private static bool TryParse(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
}
