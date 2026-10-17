using MarketData.Api.Auth;
using MarketData.Api.Caching;
using MarketData.Api.Errors;
using MarketData.Api.Health;
using MarketData.Api.Symbols;
using MarketData.Core.Analysis;
using MarketData.Infrastructure;
using MarketData.Infrastructure.Binance;
using MarketData.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// Persistence. The API never migrates; readiness reports pending migrations instead (ADR-008).
var connectionString = configuration.GetConnectionString("MarketData")
    ?? throw new InvalidOperationException("Connection string 'MarketData' is not configured.");
builder.Services.AddMarketDataPersistence(connectionString);
builder.Services.AddExchangeInfoSymbolValidator(
    configuration.GetSection(ExchangeInfoOptions.SectionName).Get<ExchangeInfoOptions>() ?? new ExchangeInfoOptions());

// Analysis, cached on the data version (ADR-006).
var cacheOptions = configuration.GetSection(AnalysisCacheOptions.SectionName).Get<AnalysisCacheOptions>() ?? new AnalysisCacheOptions();
builder.Services.AddSingleton(cacheOptions);
builder.Services.AddSingleton<AnalysisCache>();
builder.Services.AddScoped<IPriceAnalysisService, CachingPriceAnalysisService>();
builder.Services.AddScoped<SymbolAdminService>();

// Content negotiation for every response, errors included (ADR-004). The XML ProblemDetails writer is
// registered before AddProblemDetails, so it is asked before the JSON writer.
builder.Services.AddSingleton<IProblemDetailsWriter, XmlProblemDetailsWriter>();
builder.Services.AddProblemDetails();
builder.Services
    .AddControllers(options =>
    {
        options.RespectBrowserAcceptHeader = true;
        options.ReturnHttpNotAcceptable = true;
        options.Filters.Add<NotAcceptableFilter>();
    })
    .AddXmlDataContractSerializerFormatters();

// Admin auth: X-Api-Key (ADR-004).
builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName,
        options => options.ApiKey = configuration["Admin:ApiKey"]);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ApiKeyAuthenticationHandler.AdminPolicy, policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireRole(ApiKeyAuthenticationHandler.AdminRole));

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<PendingMigrationsHealthCheck>("migrations", tags: ["ready"]);

builder.Services.AddOpenApi();

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("marketdata-api"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter(MarketDataMetrics.MeterName))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());
if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapOpenApi();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

/// <summary>Entry point; public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
