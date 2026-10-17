using System.Text.Json;
using MarketData.Infrastructure.Persistence;
using MarketData.Ingestion.Leadership;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MarketData.Ingestion.Health;

/// <summary><c>/health/live</c> (process up) and <c>/health/ready</c> (database, schema, role and ingestion) on the internal port.</summary>
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

    /// <summary>JSON report: overall status, this worker's role, and each check's outcome.</summary>
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var role = context.RequestServices.GetRequiredService<LeadershipState>().Role == WorkerRole.Leader ? "leader" : "standby";
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            new HealthResponse(
                report.Status.ToString(),
                role,
                [.. report.Entries.Select(e => new HealthEntry(e.Key, e.Value.Status.ToString(), e.Value.Description))]),
            HealthJsonContext.Default.HealthResponse,
            context.RequestAborted);
    }
}

public sealed record HealthResponse(string Status, string Role, IReadOnlyList<HealthEntry> Checks);

public sealed record HealthEntry(string Name, string Status, string? Description);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(HealthResponse))]
internal sealed partial class HealthJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
