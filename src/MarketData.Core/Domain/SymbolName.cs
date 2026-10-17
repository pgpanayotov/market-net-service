using System.Text.RegularExpressions;

namespace MarketData.Core.Domain;

/// <summary>
/// A normalized trading-pair name such as <c>BTCUSDT</c>. Input is matched case-insensitively and
/// stored upper-case.
/// </summary>
public readonly partial record struct SymbolName
{
    private SymbolName(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string? input, out SymbolName symbol)
    {
        var normalized = input?.Trim().ToUpperInvariant();
        if (normalized is null || !Pattern().IsMatch(normalized))
        {
            symbol = default;
            return false;
        }

        symbol = new SymbolName(normalized);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

    [GeneratedRegex("^[A-Z0-9]{5,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
