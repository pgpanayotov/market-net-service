using System.Net.Http.Json;
using MarketData.Contracts;
using Npgsql;

namespace MarketData.Api.IntegrationTests;

/// <summary>
/// The cache key includes the newest stored open time (ADR-006), so a new candle is visible on the
/// next request, while unchanged data is served from the cache.
/// </summary>
public class AnalysisCachingTests(ApiFixture fixture) : ApiTest(fixture)
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-01T00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task NewCandle_ChangesAverageImmediately()
    {
        await SeedAsync(Minutes(Btc, Start, 10m, 10m, 10m));
        using var client = CreateClient();
        var before = await GetAverageAsync(client);

        await SeedAsync([C(Btc, Start.AddMinutes(3), 50m)]);
        var after = await GetAverageAsync(client);

        Assert.Equal(10m, before.AveragePrice);
        Assert.Equal(20m, after.AveragePrice);
        Assert.Equal(Start.AddMinutes(4).UtcDateTime, after.To);
    }

    [Fact]
    public async Task NewCandle_ChangesSmaImmediately()
    {
        await SeedAsync(Minutes(Btc, Start, 10m, 20m));
        using var client = CreateClient();
        var before = await GetSmaAsync(client, "n=2&p=1m");

        await SeedAsync([C(Btc, Start.AddMinutes(2), 40m)]);
        var after = await GetSmaAsync(client, "n=2&p=1m");

        Assert.Equal(15m, before.SimpleMovingAverage);
        Assert.Equal(30m, after.SimpleMovingAverage);
        Assert.Equal(Start.AddMinutes(3).UtcDateTime, after.ReferenceTime);
    }

    [Fact]
    public async Task UnchangedLatestCandle_IsServedFromCache()
    {
        await SeedAsync(Minutes(Btc, Start, 10m, 10m, 10m));
        using var client = CreateClient();
        var first = await GetAverageAsync(client);

        // A repair behind the newest candle doesn't change the key, so the cached value is reused
        // until it expires (60s) or a newer candle arrives.
        await SetCloseAsync(Btc, Start, 40m);
        var cached = await GetAverageAsync(client);
        await SeedAsync([C(Btc, Start.AddMinutes(3), 10m)]);
        var refreshed = await GetAverageAsync(client);

        Assert.Equal(10m, first.AveragePrice);
        Assert.Equal(10m, cached.AveragePrice);
        Assert.Equal(17.5m, refreshed.AveragePrice);
    }

    [Fact]
    public async Task StartTimesInOneBucket_ShareTheResult()
    {
        await SeedAsync(Minutes(Btc, Start, 10m, 20m, 30m, 40m, 50m, 60m));
        using var client = CreateClient();

        var a = await GetSmaAsync(client, "n=1&p=5m&s=2026-10-01T00:05:00Z");
        var b = await GetSmaAsync(client, "n=1&p=5m&s=2026-10-01T00:05:59");

        Assert.Equal(50m, a.SimpleMovingAverage);
        Assert.Equal(a.ReferenceTime, b.ReferenceTime);
        Assert.Equal(a.SimpleMovingAverage, b.SimpleMovingAverage);
    }

    [Fact]
    public async Task StartWithoutOffset_IsUtc()
    {
        await SeedAsync(Minutes(Btc, Start, 10m, 20m, 30m, 40m));
        using var client = CreateClient();

        var noOffset = await GetSmaAsync(client, "n=1&p=1m&s=2026-10-01T00:02:00");
        var withOffset = await GetSmaAsync(client, "n=1&p=1m&s=2026-10-01T02:02:00%2B02:00");
        var unencodedPlus = await GetSmaAsync(client, "n=1&p=1m&s=2026-10-01T02:02:00+02:00");

        Assert.Equal(Start.AddMinutes(2).UtcDateTime, noOffset.ReferenceTime);
        Assert.Equal(DateTimeKind.Utc, noOffset.ReferenceTime.Kind);
        Assert.Equal(20m, noOffset.SimpleMovingAverage);
        Assert.Equal(noOffset.ReferenceTime, withOffset.ReferenceTime);
        Assert.Equal(noOffset.ReferenceTime, unencodedPlus.ReferenceTime);
    }

    [Fact]
    public async Task SettledWindow_SurvivesNewCandles()
    {
        // A window ending more than 48h before the newest candle is outside the gap scan and is cached
        // without the data version, so later candles don't evict it.
        await SeedAsync(Minutes(Btc, Start, 10m, 20m));
        await SeedAsync([C(Btc, Start.AddDays(3), 99m)]);
        using var client = CreateClient();
        var first = await GetSmaAsync(client, "n=2&p=1m&s=2026-10-01T00:02:00Z");

        await SetCloseAsync(Btc, Start, 30m);
        await SeedAsync([C(Btc, Start.AddDays(3).AddMinutes(1), 99m)]);
        var second = await GetSmaAsync(client, "n=2&p=1m&s=2026-10-01T00:02:00Z");

        Assert.Equal(15m, first.SimpleMovingAverage);
        Assert.Equal(15m, second.SimpleMovingAverage);
    }

    [Fact]
    public async Task Series_LastElementEqualsSingleValue()
    {
        await SeedAsync(Minutes(Btc, Start, 10m, 20m, 30m, 40m, 50m));
        using var client = CreateClient();

        var single = await GetSmaAsync(client, "n=3&p=1m");
        var series = await GetSmaAsync(client, "n=3&p=1m&series=true&count=3");

        Assert.Equal(40m, single.SimpleMovingAverage);
        Assert.Null(series.SimpleMovingAverage);
        Assert.Null(series.Points);
        Assert.Equal<decimal?>([20m, 30m, 40m], series.Series!.Select(i => i.SimpleMovingAverage));
        Assert.Equal(single.ReferenceTime, series.Series![^1].Time);
    }

    [Fact]
    public async Task Series_WithLittleHistory_StillReturnsCountItems()
    {
        await SeedAsync(Minutes(Btc, Start, 10m, 20m));
        using var client = CreateClient();

        var series = await GetSmaAsync(client, "n=1&p=1m&series=true&count=4");

        Assert.Equal([0, 0, 1, 1], series.Series!.Select(i => i.DataPointsUsed));
        Assert.Equal<decimal?>([null, null, 10m, 20m], series.Series!.Select(i => i.SimpleMovingAverage));
    }

    [Fact]
    public async Task IncludePointsFalse_OmitsPoints()
    {
        await SeedAsync(Minutes(Btc, Start, 10m, 20m));
        using var client = CreateClient();

        var withPoints = await GetSmaAsync(client, "n=2&p=1m");
        var withoutPoints = await GetSmaAsync(client, "n=2&p=1m&includePoints=false");

        Assert.Equal(2, withPoints.Points!.Count);
        Assert.Null(withoutPoints.Points);
    }

    private static async Task<AvgPriceResponse> GetAverageAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<AvgPriceResponse>("/api/BTCUSDT/24hAvgPrice", Ct))!;

    private static async Task<SmaResponse> GetSmaAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<SmaResponse>($"/api/BTCUSDT/SimpleMovingAverage?{query}", Ct))!;

    private async Task SetCloseAsync(int symbolId, DateTimeOffset openTime, decimal close)
    {
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("UPDATE candles_1m SET close = @c WHERE symbol_id = @s AND open_time = @t", connection);
        command.Parameters.AddWithValue("c", close);
        command.Parameters.AddWithValue("s", symbolId);
        command.Parameters.AddWithValue("t", openTime.UtcDateTime);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }
}
