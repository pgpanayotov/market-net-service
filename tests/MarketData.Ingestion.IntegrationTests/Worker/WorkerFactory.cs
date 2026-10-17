using System.Net.Http.Json;
using MarketData.Infrastructure.Binance;
using MarketData.Ingestion.Health;
using MarketData.Ingestion.IntegrationTests.Fakes;
using MarketData.Ingestion.Leadership;
using MarketData.Ingestion.Symbols;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarketData.Ingestion.IntegrationTests.Worker;

/// <summary>
/// One real worker host (the app's own <c>Program</c>) on the test database, with Binance replaced by
/// <see cref="FakeMarketDataStream"/> and <see cref="StubKlineRestClient"/>. Several factories can run
/// side by side as competing workers.
/// </summary>
public sealed class WorkerFactory(string connectionString, IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
{
    /// <summary>Short intervals so tests finish quickly; periodic work is slow so tests see the event-driven paths.</summary>
    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["MarketData:BackfillOnAdd"] = "00:30:00",
        ["MarketData:GapScanInterval"] = "01:00:00",
        ["MarketData:ReconcileInterval"] = "01:00:00",
        ["MarketData:LeaderRetryInterval"] = "00:00:00.300",
        ["MarketData:LeaderCheckInterval"] = "00:00:00.200",
        ["MarketData:WriteBatchWindow"] = "00:00:00.100",
        ["Logging:LogLevel:Default"] = "Warning",
    };

    public FakeMarketDataStream Stream { get; } = new();

    public StubKlineRestClient Rest { get; } = new(TimeProvider.System);

    public LeadershipState Leadership => Services.GetRequiredService<LeadershipState>();

    public SymbolChangeListener Listener => Services.GetRequiredService<SymbolChangeListener>();

    /// <summary>Starts the host (WebApplicationFactory starts it lazily).</summary>
    public WorkerFactory Start()
    {
        _ = Services;
        return this;
    }

    public async Task<(int StatusCode, HealthResponse Body)> GetReadinessAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken);
        return ((int)response.StatusCode, body!);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:MarketData", connectionString);
        foreach (var (key, value) in Defaults.Concat(settings ?? new Dictionary<string, string>()))
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMarketDataStream>();
            services.AddSingleton<IMarketDataStream>(Stream);
            services.RemoveAll<IKlineRestClient>();
            services.AddSingleton<IKlineRestClient>(Rest);
        });
    }
}
