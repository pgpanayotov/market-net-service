using System.Net;

namespace MarketData.Api.IntegrationTests;

public class HealthTests(ApiFixture fixture) : ApiTest(fixture)
{
    [Fact]
    public async Task Live_And_Ready_AreHealthy_OnMigratedDatabase()
    {
        using var client = CreateClient();

        using var live = await client.GetAsync("/health/live", Ct);
        using var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Ready_IsUnhealthy_WithPendingMigrations()
    {
        var emptyDb = await Fixture.CreateEmptyDatabaseAsync("api_pending_migrations");
        await using var factory = new ApiFactory(emptyDb, Fixture.Exchange);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live", Ct);
        using var ready = await client.GetAsync("/health/ready", Ct);

        // The API never migrates (ADR-008): it stays alive but not ready until the migrator has run.
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync(Ct));
    }
}
