using MarketData.Client;
using MarketData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketData.Api.IntegrationTests;

/// <summary>
/// P6: the <c>marketdata</c> console client driven end to end against the in-memory API host, in both
/// response formats, so the client's JSON and XML paths are exercised against real server output.
/// </summary>
public class ConsoleClientTests(ApiFixture fixture) : ApiTest(fixture)
{
    private static readonly DateTimeOffset T0 = At("2026-10-18T12:00:00Z");

    public static TheoryData<string> Formats() => ["json", "xml"];

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Avg_PrintsAverageAndWindow(string format)
    {
        await SeedAsync(Minutes(Btc, T0, 10m, 20m, 30m));

        var (exit, output, error) = await RunAsync("avg", "btcusdt", "--format", format);

        Assert.Equal(Cli.Success, exit);
        Assert.Empty(error);
        Assert.Contains("BTCUSDT  24h average price", output, StringComparison.Ordinal);
        Assert.Contains("Average price  20", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-18 12:00 UTC -> 2026-10-18 12:03 UTC", output, StringComparison.Ordinal);
        Assert.Contains("3 (less than 24h of data available)", output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Sma_SingleValueWithPoints(string format)
    {
        // 12:00..12:09 → 5m buckets close at 4 and 9; data ends 12:10 → reference 12:10.
        await SeedAsync(Minutes(Btc, T0, 0m, 1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 9m));

        var (exit, output, _) = await RunAsync("sma", "BTCUSDT", "-n", "2", "-p", "5m", "--points", "--format", format);

        Assert.Equal(Cli.Success, exit);
        Assert.Contains("BTCUSDT  SMA(2, 5m) as of 2026-10-18 12:10 UTC", output, StringComparison.Ordinal);
        Assert.Contains("SMA          6.5", output, StringComparison.Ordinal);
        Assert.Contains("Data points  2 of 2", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-18 12:00 UTC      4", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-18 12:05 UTC      9", output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Sma_SeriesTable(string format)
    {
        await SeedAsync(Minutes(Btc, T0, 1m, 2m, 3m, 4m));

        var (exit, output, _) = await RunAsync("sma", "BTCUSDT", "-n", "2", "-p", "1m", "--series", "--count", "3", "--format", format);

        Assert.Equal(Cli.Success, exit);
        Assert.Contains("rolling SMA(2, 1m), 3 value(s) up to 2026-10-18 12:04 UTC", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-18 12:02 UTC  1.5       2", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-18 12:04 UTC  3.5       2", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sma_SeriesTable_ShowsDashForEmptyWindows()
    {
        await SeedAsync(Minutes(Btc, T0, 1m, 2m));

        var (exit, output, _) = await RunAsync("sma", "BTCUSDT", "-n", "1", "-p", "1m", "--series", "--count", "3");

        Assert.Equal(Cli.Success, exit);
        Assert.Contains("3 value(s)", output, StringComparison.Ordinal);
        Assert.Matches(@"2026-10-18 12:00 UTC\s+-\s+0", output);
    }

    [Fact]
    public async Task Sma_StartWithOffset_IsSentEscaped()
    {
        await SeedAsync(Minutes(Btc, T0, 1m, 2m, 3m, 4m));

        // 12:03+02:00 is 10:03 UTC, before any data → 404 instead of a mis-parsed '+'.
        var (exit, _, error) = await RunAsync("sma", "BTCUSDT", "-n", "1", "-p", "1m", "-s", "2026-10-18T12:03:00+02:00");

        Assert.Equal(Cli.ApiError, exit);
        Assert.Contains("No data available", error, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task ValidationErrors_GoToStderrWithKeys(string format)
    {
        var (exit, output, error) = await RunAsync("sma", "BTCUSDT", "-n", "0", "-p", "2h", "--format", format);

        Assert.Equal(Cli.ApiError, exit);
        Assert.Empty(output);
        Assert.StartsWith("Error 400", error, StringComparison.Ordinal);
        Assert.Contains("  n: ", error, StringComparison.Ordinal);
        Assert.Contains("  p: ", error, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task UnknownSymbol_Is404OnStderr(string format)
    {
        var (exit, _, error) = await RunAsync("avg", "DOGEUSDT", "--format", format);

        Assert.Equal(Cli.ApiError, exit);
        Assert.Contains("Error 404 Not Found: Unknown symbol", error, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Symbols_ListAddRemove(string format)
    {
        await SeedAsync(Minutes(Btc, T0, 1m));

        var (listExit, list, _) = await RunAsync("symbols", "list", "--api-key", ApiFixture.AdminKey, "--format", format);
        Assert.Equal(Cli.Success, listExit);
        Assert.Contains("BTCUSDT  yes     2026-10-18 00:00 UTC  2026-10-18 12:00 UTC", list, StringComparison.Ordinal);
        Assert.Contains("ADAUSDT  yes     2026-10-18 00:00 UTC  -", list, StringComparison.Ordinal);

        var (addExit, added, _) = await RunAsync("symbols", "add", "solusdt", "--api-key", ApiFixture.AdminKey, "--format", format);
        Assert.Equal(Cli.Success, addExit);
        Assert.StartsWith("Added SOLUSDT.", added, StringComparison.Ordinal);

        var (againExit, _, conflict) = await RunAsync("symbols", "add", "SOLUSDT", "--api-key", ApiFixture.AdminKey, "--format", format);
        Assert.Equal(Cli.ApiError, againExit);
        Assert.StartsWith("Error 409", conflict, StringComparison.Ordinal);

        var (removeExit, removed, _) = await RunAsync("symbols", "remove", "solusdt", "--api-key", ApiFixture.AdminKey, "--format", format);
        Assert.Equal(Cli.Success, removeExit);
        Assert.StartsWith("Removed SOLUSDT.", removed, StringComparison.Ordinal);

        var (reExit, reactivated, _) = await RunAsync("symbols", "add", "SOLUSDT", "--api-key", ApiFixture.AdminKey, "--format", format);
        Assert.Equal(Cli.Success, reExit);
        Assert.StartsWith("Reactivated SOLUSDT.", reactivated, StringComparison.Ordinal);

        await using var db = Fixture.CreateContext();
        Assert.True((await db.Symbols.SingleAsync(s => s.Name == "SOLUSDT", Ct)).IsActive);
    }

    [Fact]
    public async Task Symbols_WithoutKey_Is401WithHint()
    {
        var (exit, _, error) = await RunAsync("symbols", "list", "--format", "xml");

        Assert.Equal(Cli.ApiError, exit);
        Assert.StartsWith("Error 401", error, StringComparison.Ordinal);
        Assert.Contains($"set {Cli.AdminKeyVariable}", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Raw_PrintsTheNegotiatedBody()
    {
        await SeedAsync(Minutes(Btc, T0, 1m));

        var (xmlExit, xml, _) = await RunAsync("avg", "BTCUSDT", "--format", "xml", "--raw");
        var (jsonExit, json, _) = await RunAsync("avg", "BTCUSDT", "--raw");

        Assert.Equal(Cli.Success, xmlExit);
        Assert.StartsWith("<avgPrice", xml, StringComparison.Ordinal);
        Assert.Equal(Cli.Success, jsonExit);
        Assert.StartsWith("{\"symbol\":\"BTCUSDT\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableApi_IsConnectionError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        // Default HttpClient factory, nothing listening on port 1.
        var exit = await Cli.RunAsync(["avg", "BTCUSDT", "--url", "http://127.0.0.1:1"], output, error, cancellationToken: Ct);

        Assert.Equal(Cli.ConnectionError, exit);
        Assert.StartsWith("Cannot reach the API at http://127.0.0.1:1/", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRequiredOption_IsUsageError()
    {
        var (exit, _, error) = await RunAsync("sma", "BTCUSDT", "-p", "5m");

        Assert.NotEqual(Cli.Success, exit);
        Assert.Contains("-n", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidUrl_IsUsageError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await Cli.RunAsync(["avg", "BTCUSDT", "--url", "not-a-url"], output, error, cancellationToken: Ct);

        Assert.NotEqual(Cli.Success, exit);
        Assert.Contains("'not-a-url' is not an absolute http(s) URL.", error.ToString(), StringComparison.Ordinal);
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await Cli.RunAsync([.. args, "--url", "http://localhost"], output, error, _ => Fixture.Factory.CreateClient(), Ct);
        return (exit, output.ToString(), error.ToString());
    }
}
