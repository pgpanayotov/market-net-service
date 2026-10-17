namespace MarketData.Core.Analysis;

/// <summary>Why an analysis request failed. The API maps each case to a ProblemDetails status (ADR-006).</summary>
public abstract record AnalysisError
{
    private AnalysisError()
    {
    }

    /// <summary>400: request parameters are invalid. Keys are query parameter names.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : AnalysisError;

    /// <summary>404: the symbol was never configured.</summary>
    public sealed record UnknownSymbol(string Symbol) : AnalysisError;

    /// <summary>404: the symbol is configured but has no data for the request.</summary>
    public sealed record NoData(string Symbol) : AnalysisError;
}

public readonly struct AnalysisResult<T>
    where T : class
{
    private AnalysisResult(T? value, AnalysisError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public AnalysisError? Error { get; }

    public bool IsSuccess => Error is null;

    public static implicit operator AnalysisResult<T>(T value) => new(value, null);

    public static implicit operator AnalysisResult<T>(AnalysisError error) => new(null, error);
}
