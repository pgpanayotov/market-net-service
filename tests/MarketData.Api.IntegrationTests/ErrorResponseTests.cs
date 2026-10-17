using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace MarketData.Api.IntegrationTests;

/// <summary>Errors are RFC 7807 ProblemDetails in the negotiated format, whether MVC or middleware produced them.</summary>
public class ErrorResponseTests(ApiFixture fixture) : ApiTest(fixture)
{
    private static readonly XNamespace Rfc7807 = "urn:ietf:rfc:7807";

    [Theory]
    [InlineData("/api/BTCUSDT/SimpleMovingAverage?n=0&p=1m", "n")]
    [InlineData("/api/BTCUSDT/SimpleMovingAverage?n=5&p=1M", "p")]
    [InlineData("/api/BTCUSDT/SimpleMovingAverage?n=5&p=1m&s=2999-01-01T00:00:00Z", "s")]
    [InlineData("/api/BTCUSDT/SimpleMovingAverage?n=5&p=1m&s=yesterday", "s")]
    [InlineData("/api/BTCUSDT/SimpleMovingAverage?n=5&p=1m&count=3", "count")]
    [InlineData("/api/BTCUSDT/SimpleMovingAverage?n=abc&p=1m", "n")]
    [InlineData("/api/BTC/24hAvgPrice", "symbol")]
    public async Task BadRequest_IsXmlValidationProblem(string url, string key)
    {
        var problem = await GetXmlProblemAsync(CreateClient("application/xml"), url, HttpStatusCode.BadRequest);

        Assert.Equal("400", problem.Element(Rfc7807 + "status")?.Value);
        var errors = problem.Element(Rfc7807 + "MVC-Errors");
        Assert.NotNull(errors);
        Assert.Contains(errors.Elements(), e => e.Name.LocalName == key);
    }

    [Fact]
    public async Task BadRequest_IsJsonValidationProblem()
    {
        using var client = CreateClient("application/json");
        using var response = await client.GetAsync("/api/BTCUSDT/SimpleMovingAverage?n=2000&p=2h", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var errors = json.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("n", out _));
        Assert.True(errors.TryGetProperty("p", out _));
    }

    [Fact]
    public async Task UnknownSymbol_Is404Xml()
    {
        var problem = await GetXmlProblemAsync(CreateClient("application/xml"), "/api/DOGEUSDT/24hAvgPrice", HttpStatusCode.NotFound);

        Assert.Equal("Unknown symbol", problem.Element(Rfc7807 + "detail")?.Value);
    }

    [Fact]
    public async Task KnownSymbolWithoutData_Is404NoData()
    {
        var problem = await GetXmlProblemAsync(CreateClient("application/xml"), "/api/ETHUSDT/SimpleMovingAverage?n=3&p=5m", HttpStatusCode.NotFound);

        Assert.Equal("No data available", problem.Element(Rfc7807 + "detail")?.Value);
    }

    [Fact]
    public async Task UnmatchedRoute_Is404Xml()
    {
        var problem = await GetXmlProblemAsync(CreateClient("application/xml"), "/no/such/route", HttpStatusCode.NotFound);

        Assert.Equal("404", problem.Element(Rfc7807 + "status")?.Value);
    }

    [Fact]
    public async Task MissingApiKey_Is401Xml()
    {
        var problem = await GetXmlProblemAsync(CreateClient("application/xml"), "/api/admin/symbols", HttpStatusCode.Unauthorized);

        Assert.Equal("401", problem.Element(Rfc7807 + "status")?.Value);
    }

    [Fact]
    public async Task WrongApiKey_Is401Json()
    {
        using var client = CreateClient("application/json");
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong");

        using var response = await client.DeleteAsync("/api/admin/symbols/BTCUSDT", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("X-Api-Key", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnmatchedRoute_WithoutAccept_IsJsonProblem()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync("/no/such/route", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    internal static async Task<XElement> GetXmlProblemAsync(HttpClient client, string url, HttpStatusCode status, HttpMethod? method = null)
    {
        using (client)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+xml", response.Content.Headers.ContentType?.MediaType);
            var root = XElement.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(Rfc7807 + "problem", root.Name);
            return root;
        }
    }
}
