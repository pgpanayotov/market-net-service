using MarketData.Infrastructure.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MarketData.Infrastructure.Persistence.Configurations;

internal sealed class CandleRowConfiguration : IEntityTypeConfiguration<CandleRow>
{
    public void Configure(EntityTypeBuilder<CandleRow> candle)
    {
        candle.ToTable(MarketDataDbContext.CandlesTable);

        // The only index needed: every query is one symbol over one time range (ADR-002),
        // and it makes upserts idempotent (ADR-005).
        candle.HasKey(c => new { c.SymbolId, c.OpenTime });

        candle.Property(c => c.Open).HasPrecision(28, 10);
        candle.Property(c => c.High).HasPrecision(28, 10);
        candle.Property(c => c.Low).HasPrecision(28, 10);
        candle.Property(c => c.Close).HasPrecision(28, 10);
        candle.Property(c => c.Volume).HasPrecision(38, 10);
        candle.Property(c => c.QuoteVolume).HasPrecision(38, 10);

        candle.HasOne<SymbolRow>()
            .WithMany()
            .HasForeignKey(c => c.SymbolId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
