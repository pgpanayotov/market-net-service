using System.Net;
using System.Net.Http.Json;
using System.Runtime.Serialization;
using System.Text;
using MarketData.Contracts;

namespace MarketData.Api.IntegrationTests;

/// <summary>F6 / ADR-004: every endpoint answers in JSON or XML per <c>Accept</c>, and 406 otherwise.</summary>
public class ContentNegotiationTests(ApiFixture fixture) : ApiTest(fixture)
{
    public static TheoryData<string> GetEndpoints =>
    [
        "/api/BTCUSDT/24hAvgPrice",
        "/api/BTCUSDT/SimpleMovingAverage?n=3&p=1m",
        "/api/BTCUSDT/SimpleMovingAverage?n=3&p=1m&series=true&count=2",
        "/api/admin/symbols",
    ];

    [Theory]
    [MemberData(nameof(GetEndpoints))]
    public async Task Get_ReturnsJson(string url)
    {
        await SeedAsync(Minutes(Btc, At("2026-10-01T00:00Z"), 10m, 11m, 12m, 13m));
        using var client = CreateClient("application/json", admin: true);

        using var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith(url.Contains("admin", StringComparison.Ordinal) ? "[" : "{", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(GetEndpoints))]
    public async Task Get_ReturnsXml(string url)
    {
        await SeedAsync(Minutes(Btc, At("2026-10-01T00:00Z"), 10m, 11m, 12m, 13m));
        using var client = CreateClient("application/xml", admin: true);

        using var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("<", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(GetEndpoints))]
    public async Task Get_Csv_Is406(string url)
    {
        await SeedAsync(Minutes(Btc, At("2026-10-01T00:00Z"), 10m, 11m, 12m, 13m));
        using var client = CreateClient("text/csv", admin: true);

        using var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task NoAcceptHeader_DefaultsToJson()
    {
        await SeedAsync(Minutes(Btc, At("2026-10-01T00:00Z"), 10m, 11m));
        using var client = CreateClient();

        var body = await client.GetFromJsonAsync<AvgPriceResponse>("/api/btcusdt/24hAvgPrice", Ct);

        Assert.Equal("BTCUSDT", body!.Symbol);
        Assert.Equal(10.5m, body.AveragePrice);
    }

    [Fact]
    public async Task XmlBody_DeserializesIntoSharedContracts()
    {
        await SeedAsync(Minutes(Btc, At("2026-10-01T00:00Z"), 10m, 11m, 12m, 13m));
        using var client = CreateClient("application/xml");

        var avg = await ReadXmlAsync<AvgPriceResponse>(client, "/api/BTCUSDT/24hAvgPrice");
        var sma = await ReadXmlAsync<SmaResponse>(client, "/api/BTCUSDT/SimpleMovingAverage?n=2&p=1m");

        Assert.Equal(11.5m, avg.AveragePrice);
        Assert.Equal(At("2026-10-01T00:04Z").UtcDateTime, avg.To);
        Assert.Equal(12.5m, sma.SimpleMovingAverage);
        Assert.Equal(2, sma.Points!.Count);
    }

    [Fact]
    public async Task AcceptQuality_PicksPreferredFormat()
    {
        await SeedAsync(Minutes(Btc, At("2026-10-01T00:00Z"), 10m));
        using var client = CreateClient("application/json;q=0.5, application/xml");

        using var response = await client.GetAsync("/api/BTCUSDT/24hAvgPrice", Ct);

        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("application/json", HttpStatusCode.Created, "application/json")]
    [InlineData("application/xml", HttpStatusCode.Created, "application/xml")]
    [InlineData("text/csv", HttpStatusCode.NotAcceptable, null)]
    public async Task PostSymbol_IsNegotiated(string accept, HttpStatusCode status, string? contentType)
    {
        using var client = CreateClient(accept, admin: true);

        using var response = await client.PostAsJsonAsync("/api/admin/symbols", new AddSymbolRequest { Symbol = "SOLUSDT" }, Ct);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PostSymbol_WithCsvAccept_DoesNotWrite()
    {
        using (var csv = CreateClient("text/csv", admin: true))
        {
            using var rejected = await csv.PostAsJsonAsync("/api/admin/symbols", new AddSymbolRequest { Symbol = "SOLUSDT" }, Ct);
            Assert.Equal(HttpStatusCode.NotAcceptable, rejected.StatusCode);
        }

        using var client = CreateClient(admin: true);
        var symbols = await client.GetFromJsonAsync<List<SymbolDto>>("/api/admin/symbols", Ct);
        Assert.DoesNotContain(symbols!, s => s.Name == "SOLUSDT");
    }

    [Fact]
    public async Task PostSymbol_AcceptsXmlBody()
    {
        using var client = CreateClient("application/xml", admin: true);
        using var content = new StringContent("<addSymbol><symbol>SOLUSDT</symbol></addSymbol>", Encoding.UTF8, "application/xml");

        using var response = await client.PostAsync("/api/admin/symbols", content, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (SymbolDto)new DataContractSerializer(typeof(SymbolDto)).ReadObject(await response.Content.ReadAsStreamAsync(Ct))!;
        Assert.Equal("SOLUSDT", dto.Name);
        Assert.True(dto.IsActive);
    }

    [Theory]
    [InlineData("application/json", HttpStatusCode.NoContent)]
    [InlineData("application/xml", HttpStatusCode.NoContent)]
    [InlineData("text/csv", HttpStatusCode.NotAcceptable)]
    public async Task DeleteSymbol_IsNegotiated(string accept, HttpStatusCode status)
    {
        using var client = CreateClient(accept, admin: true);

        using var response = await client.DeleteAsync("/api/admin/symbols/ETHUSDT", Ct);

        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiDocument_DescribesEndpoints()
    {
        using var client = CreateClient();

        var document = await client.GetStringAsync("/openapi/v1.json", Ct);

        Assert.Contains("/api/{symbol}/24hAvgPrice", document, StringComparison.Ordinal);
        Assert.Contains("/api/{symbol}/SimpleMovingAverage", document, StringComparison.Ordinal);
        Assert.Contains("/api/admin/symbols/{symbol}", document, StringComparison.Ordinal);
        Assert.Contains("application/xml", document, StringComparison.Ordinal);
    }

    private static async Task<T> ReadXmlAsync<T>(HttpClient client, string url)
    {
        await using var stream = await client.GetStreamAsync(url, Ct);
        return (T)new DataContractSerializer(typeof(T)).ReadObject(stream)!;
    }
}
