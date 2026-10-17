using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketData.Contracts;
using MarketData.Infrastructure.Notifications;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MarketData.Api.IntegrationTests;

/// <summary>Admin writes commit together with their <c>symbol_changes</c> notification (ADR-007).</summary>
public class AdminSymbolsTests(ApiFixture fixture) : ApiTest(fixture)
{
    [Fact]
    public async Task List_ShowsSymbolsWithLastCandle()
    {
        await SeedAsync(Minutes(Btc, At("2026-10-01T00:00Z"), 10m, 11m));
        using var client = CreateClient(admin: true);

        var symbols = await client.GetFromJsonAsync<List<SymbolDto>>("/api/admin/symbols", Ct);

        Assert.Equal(["ADAUSDT", "BTCUSDT", "ETHUSDT"], symbols!.Select(s => s.Name));
        Assert.All(symbols!, s => Assert.True(s.IsActive));
        Assert.Equal(At("2026-10-01T00:01Z").UtcDateTime, symbols!.Single(s => s.Name == "BTCUSDT").LastCandleAt);
        Assert.Null(symbols!.Single(s => s.Name == "ETHUSDT").LastCandleAt);
    }

    [Fact]
    public async Task Add_New_Is201AndNotifies()
    {
        await using var recorder = await SymbolChangesRecorder.StartAsync(Fixture.ConnectionString, Ct);
        using var client = CreateClient(admin: true);

        using var response = await client.PostAsJsonAsync("/api/admin/symbols", new AddSymbolRequest { Symbol = " solusdt " }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/admin/symbols/SOLUSDT", response.Headers.Location?.OriginalString);
        var dto = await response.Content.ReadFromJsonAsync<SymbolDto>(Ct);
        Assert.Equal("SOLUSDT", dto!.Name);
        Assert.True(dto.IsActive);

        var notification = await recorder.NextAsync(Ct);
        Assert.Equal(SymbolChangeOperation.Add, notification.Op);
        Assert.Equal("SOLUSDT", notification.Symbol);
        Assert.True(notification.SymbolId >= 100);
        await recorder.AssertNothingElseAsync(Ct);
    }

    [Fact]
    public async Task Add_AlreadyActive_Is409WithoutNotification()
    {
        await using var recorder = await SymbolChangesRecorder.StartAsync(Fixture.ConnectionString, Ct);
        using var client = CreateClient(admin: true);

        using var response = await client.PostAsJsonAsync("/api/admin/symbols", new AddSymbolRequest { Symbol = "BTCUSDT" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await recorder.AssertNothingElseAsync(Ct);
    }

    [Fact]
    public async Task Add_NotTradable_Is400WithoutWrite()
    {
        await using var recorder = await SymbolChangesRecorder.StartAsync(Fixture.ConnectionString, Ct);
        using var client = CreateClient(admin: true);

        using var response = await client.PostAsJsonAsync("/api/admin/symbols", new AddSymbolRequest { Symbol = "FAKECOIN" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("symbol", out _));
        await recorder.AssertNothingElseAsync(Ct);
        Assert.DoesNotContain(await ListAsync(client), s => s.Name == "FAKECOIN");
    }

    [Theory]
    [InlineData("")]
    [InlineData("BTC")]
    [InlineData("BTC-USDT")]
    public async Task Add_InvalidName_Is400(string symbol)
    {
        using var client = CreateClient(admin: true);

        using var response = await client.PostAsJsonAsync("/api/admin/symbols", new AddSymbolRequest { Symbol = symbol }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Add_WhenExchangeInfoUnavailable_Is503()
    {
        Fixture.Exchange.Unavailable = true;
        using var client = CreateClient(admin: true);

        using var response = await client.PostAsJsonAsync("/api/admin/symbols", new AddSymbolRequest { Symbol = "SOLUSDT" }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Remove_ThenReAdd_KeepsIdAndNotifiesEachChange()
    {
        await using var recorder = await SymbolChangesRecorder.StartAsync(Fixture.ConnectionString, Ct);
        using var client = CreateClient(admin: true);

        using (var removed = await client.DeleteAsync("/api/admin/symbols/ethusdt", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        var remove = await recorder.NextAsync(Ct);
        Assert.Equal((SymbolChangeOperation.Remove, Eth, "ETHUSDT"), (remove.Op, remove.SymbolId, remove.Symbol));
        Assert.False((await ListAsync(client)).Single(s => s.Name == "ETHUSDT").IsActive);

        using (var again = await client.DeleteAsync("/api/admin/symbols/ETHUSDT", Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        using (var readded = await client.PostAsJsonAsync("/api/admin/symbols", new AddSymbolRequest { Symbol = "ETHUSDT" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, readded.StatusCode);
            Assert.True((await readded.Content.ReadFromJsonAsync<SymbolDto>(Ct))!.IsActive);
        }

        // The repeated DELETE changed nothing, so the next notification is the reactivation.
        var add = await recorder.NextAsync(Ct);
        Assert.Equal((SymbolChangeOperation.Add, Eth, "ETHUSDT"), (add.Op, add.SymbolId, add.Symbol));
        await recorder.AssertNothingElseAsync(Ct);
    }

    [Fact]
    public async Task Remove_Unknown_Is404()
    {
        using var client = CreateClient(admin: true);

        using var response = await client.DeleteAsync("/api/admin/symbols/DOGEUSDT", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RemovedSymbol_StaysQueryable()
    {
        await SeedAsync(Minutes(Btc, At("2026-10-01T00:00Z"), 10m, 20m));
        using var client = CreateClient(admin: true);
        using (await client.DeleteAsync("/api/admin/symbols/BTCUSDT", Ct))
        {
        }

        var avg = await client.GetFromJsonAsync<AvgPriceResponse>("/api/BTCUSDT/24hAvgPrice", Ct);

        Assert.Equal(15m, avg!.AveragePrice);
    }

    [Fact]
    public async Task FailureAfterNotify_RollsBackWriteAndNotification()
    {
        await using var recorder = await SymbolChangesRecorder.StartAsync(Fixture.ConnectionString, Ct);
        await using var failing = Fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IPgNotificationPublisher>(sp => new FailingPublisher(ActivatorUtilities.CreateInstance<PgNotificationPublisher>(sp)))));
        using var client = failing.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFixture.AdminKey);
        client.DefaultRequestHeaders.Add("Accept", "application/xml");

        using var response = await client.PostAsJsonAsync("/api/admin/symbols", new AddSymbolRequest { Symbol = "SOLUSDT" }, Ct);

        // The exception handler answers outside MVC, still as negotiated XML.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+xml", response.Content.Headers.ContentType?.MediaType);
        await recorder.AssertNothingElseAsync(Ct);
        using var reader = CreateClient(admin: true);
        Assert.DoesNotContain(await ListAsync(reader), s => s.Name == "SOLUSDT");
    }

    private static async Task<List<SymbolDto>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<SymbolDto>>("/api/admin/symbols", Ct))!;

    /// <summary>Sends the notification inside the transaction, then fails before commit.</summary>
    private sealed class FailingPublisher(IPgNotificationPublisher inner) : IPgNotificationPublisher
    {
        public async Task NotifyAsync<T>(string channel, T payload, CancellationToken cancellationToken)
        {
            await inner.NotifyAsync(channel, payload, cancellationToken);
            throw new InvalidOperationException("Simulated failure before commit.");
        }
    }
}
