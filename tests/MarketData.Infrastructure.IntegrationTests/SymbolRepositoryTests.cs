using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Core.Enums;
using MarketData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace MarketData.Infrastructure.IntegrationTests;

public class SymbolRepositoryTests(PostgresFixture postgres) : PostgresTest(postgres)
{
    private static readonly DateTimeOffset Now = At("2026-10-18T15:00:00Z");

    [Fact]
    public async Task Migration_SeedsInitialSymbols()
    {
        await using var db = Postgres.CreateContext();

        var active = await Repository(db).GetActiveAsync(Ct);

        Assert.Equal(
            [(Ada, "ADAUSDT"), (Btc, "BTCUSDT"), (Eth, "ETHUSDT")],
            active.Select(s => (s.Id, s.Name.Value)));
    }

    [Fact]
    public async Task Activate_Lifecycle()
    {
        await using var db = Postgres.CreateContext();
        var repository = Repository(db);
        var sol = Name("SOLUSDT");

        var (created, symbol) = await repository.ActivateAsync(sol, Ct);
        Assert.Equal(SymbolActivation.Created, created);
        Assert.True(symbol.Id >= MarketDataDbContext.FirstGeneratedSymbolId, "generated ids must not collide with seeded ones");

        Assert.Equal(SymbolActivation.AlreadyActive, (await repository.ActivateAsync(sol, Ct)).Outcome);
        Assert.Equal(SymbolDeactivation.Deactivated, await repository.DeactivateAsync(sol, Ct));
        Assert.Equal(SymbolDeactivation.AlreadyInactive, await repository.DeactivateAsync(sol, Ct));
        Assert.Equal(new SymbolInfo(symbol.Id, sol, IsActive: false), await repository.FindAsync(sol, Ct));

        var (reactivated, again) = await repository.ActivateAsync(sol, Ct);
        Assert.Equal(SymbolActivation.Reactivated, reactivated);
        Assert.Equal(symbol.Id, again.Id);
    }

    [Fact]
    public async Task Deactivate_UnknownSymbol_IsNotFound()
    {
        await using var db = Postgres.CreateContext();

        Assert.Equal(SymbolDeactivation.NotFound, await Repository(db).DeactivateAsync(Name("XRPUSDT"), Ct));
    }

    [Fact]
    public async Task Deactivate_KeepsCandleHistory_AndExcludesFromActive()
    {
        await SeedAsync(Minutes(Ada, Now, 1m));
        await using var db = Postgres.CreateContext();
        var repository = Repository(db);

        await repository.DeactivateAsync(Name("ADAUSDT"), Ct);

        Assert.DoesNotContain(await repository.GetActiveAsync(Ct), s => s.Id == Ada);
        Assert.Equal(1, await db.Candles.CountAsync(c => c.SymbolId == Ada, Ct));
    }

    [Fact]
    public async Task Writes_JoinTheCallersTransaction()
    {
        await using (var db = Postgres.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await Repository(db).ActivateAsync(Name("DOGEUSDT"), Ct);
            await Repository(db).DeactivateAsync(Name("BTCUSDT"), Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using var check = Postgres.CreateContext();
        Assert.Null(await Repository(check).FindAsync(Name("DOGEUSDT"), Ct));
        Assert.True((await Repository(check).FindAsync(Name("BTCUSDT"), Ct))!.IsActive);
    }

    [Fact]
    public async Task List_IncludesInactiveSymbolsAndLastCandle()
    {
        await SeedAsync(Minutes(Btc, Now, 1m, 2m));
        await using var db = Postgres.CreateContext();
        var repository = Repository(db);
        await repository.DeactivateAsync(Name("ETHUSDT"), Ct);

        var list = await repository.ListAsync(Ct);

        Assert.Equal(["ADAUSDT", "BTCUSDT", "ETHUSDT"], list.Select(s => s.Name.Value));
        var btc = list.Single(s => s.Id == Btc);
        Assert.Equal(Now.AddMinutes(1), btc.LastCandleAt);
        Assert.Equal(At("2026-10-18T00:00:00Z"), btc.CreatedAt);
        Assert.Null(list.Single(s => s.Id == Ada).LastCandleAt);
        Assert.False(list.Single(s => s.Id == Eth).IsActive);
    }

    [Fact]
    public async Task Find_IsCaseNormalizedThroughSymbolName()
    {
        await using var db = Postgres.CreateContext();

        Assert.Equal(Btc, (await Repository(db).FindAsync(Name("btcusdt"), Ct))!.Id);
    }

    private static SymbolRepository Repository(MarketDataDbContext db) => new(db, new FakeTimeProvider(Now));

    private static SymbolName Name(string value)
    {
        Assert.True(SymbolName.TryCreate(value, out var name));
        return name;
    }
}
