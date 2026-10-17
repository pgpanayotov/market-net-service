namespace MarketData.Core.Domain;

/// <summary>A symbol with its admin metadata, as listed by the admin API.</summary>
public sealed record SymbolDetails(int Id, SymbolName Name, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastCandleAt);
