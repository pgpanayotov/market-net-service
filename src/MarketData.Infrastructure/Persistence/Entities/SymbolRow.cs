namespace MarketData.Infrastructure.Persistence.Entities;

/// <summary>A row of <c>symbols</c>. Mapping: <see cref="Configurations.SymbolRowConfiguration"/>.</summary>
public sealed class SymbolRow
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
