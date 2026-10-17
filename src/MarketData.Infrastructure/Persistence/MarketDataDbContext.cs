using Microsoft.EntityFrameworkCore;

namespace MarketData.Infrastructure.Persistence;

/// <summary>
/// Schema owner for <c>symbols</c> and <c>candles_1m</c> (system-design §4). Apps never migrate at
/// startup. The schema is applied only by the EF migration bundle run as an init container (ADR-008).
/// </summary>
public sealed class MarketDataDbContext(DbContextOptions<MarketDataDbContext> options) : DbContext(options)
{
    public const string SymbolsTable = "symbols";
    public const string CandlesTable = "candles_1m";

    /// <summary>
    /// Identity values below this are reserved for seed rows. Seeded rows set their ids explicitly,
    /// which doesn't advance the identity sequence.
    /// </summary>
    public const int FirstGeneratedSymbolId = 100;

    /// <summary>Symbols seeded by the initial migration. After that they are managed through the admin API.</summary>
    public static IReadOnlyList<string> InitialSymbols { get; } = ["BTCUSDT", "ADAUSDT", "ETHUSDT"];

    private static readonly DateTimeOffset SeedTime = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    public DbSet<SymbolRow> Symbols => Set<SymbolRow>();

    public DbSet<CandleRow> Candles => Set<CandleRow>();

    /// <summary>Provider configuration shared by DI and the design-time factory (and therefore the bundle).</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SymbolRow>(symbol =>
        {
            symbol.ToTable(SymbolsTable);
            symbol.HasKey(s => s.Id);
            symbol.Property(s => s.Id)
                .UseIdentityByDefaultColumn()
                .HasIdentityOptions(startValue: FirstGeneratedSymbolId);
            symbol.Property(s => s.Name).HasMaxLength(20);
            symbol.HasIndex(s => s.Name).IsUnique();
            symbol.HasData(InitialSymbols.Select((name, i) => new SymbolRow
            {
                Id = i + 1,
                Name = name,
                IsActive = true,
                CreatedAt = SeedTime,
                UpdatedAt = SeedTime,
            }));
        });

        modelBuilder.Entity<CandleRow>(candle =>
        {
            candle.ToTable(CandlesTable);

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
        });
    }
}

public sealed class SymbolRow
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CandleRow
{
    public int SymbolId { get; set; }

    public DateTimeOffset OpenTime { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public decimal Volume { get; set; }

    public decimal QuoteVolume { get; set; }

    public int TradeCount { get; set; }

    /// <summary>When the service received the candle; used for the ingest-lag metric.</summary>
    public DateTimeOffset ReceivedAt { get; set; }
}
