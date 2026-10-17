using MarketData.Infrastructure.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarketData.Infrastructure.Persistence.Configurations;

internal sealed class SymbolRowConfiguration : IEntityTypeConfiguration<SymbolRow>
{
    private static readonly DateTimeOffset SeedTime = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    public void Configure(EntityTypeBuilder<SymbolRow> symbol)
    {
        symbol.ToTable(MarketDataDbContext.SymbolsTable);
        symbol.HasKey(s => s.Id);
        symbol.Property(s => s.Id)
            .UseIdentityByDefaultColumn()
            .HasIdentityOptions(startValue: MarketDataDbContext.FirstGeneratedSymbolId);
        symbol.Property(s => s.Name).HasMaxLength(20);
        symbol.HasIndex(s => s.Name).IsUnique();
        symbol.HasData(MarketDataDbContext.InitialSymbols.Select((name, i) => new SymbolRow
        {
            Id = i + 1,
            Name = name,
            IsActive = true,
            CreatedAt = SeedTime,
            UpdatedAt = SeedTime,
        }));
    }
}
