using MarketData.Core.Domain;

namespace MarketData.Core.Analysis;

/// <summary>Raw parameters of <c>GET /api/{symbol}/SimpleMovingAverage</c>.</summary>
/// <param name="Start">Query parameter <c>s</c>; the API binds a value without an offset as UTC.</param>
public sealed record SmaQuery(
    string? Symbol,
    int N,
    string? Period,
    DateTimeOffset? Start = null,
    bool Series = false,
    int? Count = null,
    bool IncludePoints = true);

public static class AnalysisLimits
{
    public const int MaxN = 1000;
    public const int MaxCount = 1000;

    /// <summary>Upper bound of the default series length, which is <c>min(n, 100)</c>.</summary>
    public const int DefaultCountCap = 100;
}

/// <summary>A <see cref="SmaQuery"/> that passed validation.</summary>
public sealed record ValidSmaQuery(SymbolName Symbol, int N, TimePeriod Period, DateTimeOffset? Start, int? SeriesCount, bool IncludePoints);

public static class AnalysisValidator
{
    public static IReadOnlyDictionary<string, string[]> ValidateSymbol(string? symbol, out SymbolName name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddSymbolError(errors, symbol, out name);
        return errors;
    }

    public static IReadOnlyDictionary<string, string[]> Validate(SmaQuery query, DateTimeOffset now, out ValidSmaQuery? valid)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddSymbolError(errors, query.Symbol, out var symbol);

        if (query.N is < 1 or > AnalysisLimits.MaxN)
        {
            errors["n"] = [$"Must be between 1 and {AnalysisLimits.MaxN}."];
        }

        if (!TimePeriod.TryParse(query.Period, out var period))
        {
            errors["p"] = [$"Must be one of: {string.Join(", ", TimePeriod.All)}."];
        }

        if (query.Start > now)
        {
            errors["s"] = ["Must not be in the future."];
        }

        int? count = null;
        if (!query.Series)
        {
            if (query.Count is not null)
            {
                errors["count"] = ["Only valid together with series=true."];
            }
        }
        else if (query.Count is < 1 or > AnalysisLimits.MaxCount)
        {
            errors["count"] = [$"Must be between 1 and {AnalysisLimits.MaxCount}."];
        }
        else
        {
            count = query.Count ?? Math.Clamp(query.N, 1, AnalysisLimits.DefaultCountCap);
        }

        valid = errors.Count == 0
            ? new ValidSmaQuery(symbol, query.N, period, query.Start, count, query.IncludePoints)
            : null;
        return errors;
    }

    private static void AddSymbolError(Dictionary<string, string[]> errors, string? symbol, out SymbolName name)
    {
        if (!SymbolName.TryCreate(symbol, out name))
        {
            errors["symbol"] = ["Must be 5-20 letters or digits, e.g. BTCUSDT."];
        }
    }
}
