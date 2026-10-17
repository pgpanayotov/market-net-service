using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Binance.Net.Clients;
using Binance.Net.Objects.Models.Spot.Socket;
using Binance.Net.Objects.Options;
using CryptoExchange.Net.Converters.SystemTextJson;
using MarketData.Infrastructure.Binance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketData.Ingestion.IntegrationTests.Fakes;

/// <summary>Real Binance.Net parsing of recorded Binance payloads, without a network.</summary>
public static class BinanceFixtures
{
    private static readonly Lazy<JsonSerializerOptions> SocketJson = new(() =>
    {
        // Binance.Net's socket messages go through its internal source-generated context; reflection reaches
        // it so the golden WS payload is parsed exactly as the library parses a live message.
        var contextType = typeof(BinanceStreamKlineData).Assembly.GetTypes()
            .Single(t => typeof(JsonSerializerContext).IsAssignableFrom(t) && t.Name == "BinanceSourceGenerationContext");
        var context = (JsonSerializerContext)contextType.GetProperty("Default", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return SerializerOptions.WithConverters(context);
    });

    public static string Golden(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    /// <summary>Parses the <c>data</c> of a combined-stream message (<c>{"stream":…,"data":…}</c>).</summary>
    public static BinanceStreamKlineData ParseStreamKline(string combinedStreamMessage)
    {
        using var document = JsonDocument.Parse(combinedStreamMessage);
        var data = document.RootElement.GetProperty("data").GetRawText();
        return JsonSerializer.Deserialize<BinanceStreamKlineData>(data, SocketJson.Value)!;
    }

    /// <summary>A real <see cref="BinanceRestClient"/> whose HTTP calls are answered by <paramref name="handler"/>.</summary>
    public static BinanceRestClient RestClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(new BinanceRestOptions
            {
                Environment = new BinanceEndpointOptions().ToEnvironment(),

                // The library would also honour Retry-After, in real time; the tests check the adapter on fake time.
                RateLimiterEnabled = false,
            }));
}

/// <summary>Answers each request with the next queued response, or with <see cref="Fallback"/>; records requests.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public ConcurrentQueue<Uri> Requests { get; } = new();

    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    /// <summary>Invoked when a request arrives, e.g. to record the (fake) time.</summary>
    public Action<HttpRequestMessage>? OnRequest { get; set; }

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) => _responses.Enqueue(response);

    public void EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK) => Enqueue(_ => Json(json, status));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        OnRequest?.Invoke(request);
        var respond = _responses.TryDequeue(out var next) ? next : Fallback ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(respond(request));
    }
}
