using MarketData.Infrastructure.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

namespace MarketData.Infrastructure.Persistence;

/// <summary>
/// Schema owner for <c>symbols</c> and <c>candles_1m</c> (system-design §4). Apps never migrate at
/// startup. The schema is applied only by the EF migration bundle run as an init container (ADR-008).
/// Entity mappings live in <c>Persistence/Configurations</c>.
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

    public DbSet<SymbolRow> Symbols => Set<SymbolRow>();

    public DbSet<CandleRow> Candles => Set<CandleRow>();

    /// <summary>Provider configuration shared by DI and the design-time factory (and therefore the bundle).</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MarketDataDbContext).Assembly);
}
