using System.Text.RegularExpressions;

namespace MarketData.Infrastructure.Notifications;

internal static partial class PgChannel
{
    /// <summary><c>LISTEN</c> takes an identifier, not a parameter, so channel names are restricted to plain lower-case identifiers.</summary>
    public static void Validate(string channel)
    {
        if (!Identifier().IsMatch(channel))
        {
            throw new ArgumentException($"'{channel}' is not a valid notification channel name.", nameof(channel));
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
