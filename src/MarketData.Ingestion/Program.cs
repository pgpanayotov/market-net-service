using MarketData.Infrastructure;
using MarketData.Ingestion;
using MarketData.Ingestion.Health;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

// The worker is a web host only for its health endpoints on the internal port (Kestrel:Endpoints:Health).
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMarketDataIngestion(builder.Configuration);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("marketdata-ingestion"))
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(MarketDataMetrics.MeterName).AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            metrics.AddOtlpExporter();
        }

        if (builder.Configuration.GetValue<bool>("MarketData:Telemetry:ConsoleExporter"))
        {
            metrics.AddConsoleExporter();
        }
    });

var app = builder.Build();

app.MapIngestionHealthEndpoints();
await app.RunAsync();

/// <summary>Entry point; public for <c>WebApplicationFactory</c> in the integration tests.</summary>
public partial class Program;
