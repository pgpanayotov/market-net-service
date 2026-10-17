using System.Text.Json;
using MarketData.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MarketData.Ingestion.Health;

/// <summary><c>/health/live</c> (process up) and <c>/health/ready</c> (database, schema and ingestion) on the internal port.</summary>
public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static IHealthChecksBuilder AddIngestionHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag])
            .AddCheck<PendingMigrationsHealthCheck>("schema", tags: [ReadyTag])
            .AddCheck<IngestionHealthCheck>("ingestion", tags: [ReadyTag]);

    public static void MapIngestionHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains(ReadyTag),
            ResponseWriter = WriteAsync,
        });
    }

    /// <summary>JSON report: overall status and each check's outcome.</summary>
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            new HealthResponse(
                report.Status.ToString(),
                [.. report.Entries.Select(e => new HealthEntry(e.Key, e.Value.Status.ToString(), e.Value.Description))]),
            HealthJsonContext.Default.HealthResponse,
            context.RequestAborted);
    }
}

public sealed record HealthResponse(string Status, IReadOnlyList<HealthEntry> Checks);

public sealed record HealthEntry(string Name, string Status, string? Description);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(HealthResponse))]
internal sealed partial class HealthJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
