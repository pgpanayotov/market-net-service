using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Xml;
using MarketData.Contracts;

namespace MarketData.Client.Api;

public enum ApiFormat
{
    Json,
    Xml,
}

/// <summary>A successful response: the typed body plus the raw text exactly as the server sent it.</summary>
public sealed record ApiResponse<T>(T Value, HttpStatusCode Status, string RawBody);

/// <summary>
/// Typed client for the MarketData HTTP API. Every request sets <c>Accept</c> to the chosen
/// <see cref="ApiFormat"/> and deserializes the shared Contracts DTOs with the matching serializer,
/// so the client also exercises the API's content negotiation end to end.
/// </summary>
public sealed class MarketDataApiClient(HttpClient http, ApiFormat format, string? apiKey = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ApiFormat Format => format;

    public Task<ApiResponse<AvgPriceResponse>> GetAverageAsync(string symbol, CancellationToken cancellationToken) =>
        SendAsync<AvgPriceResponse>(HttpMethod.Get, $"api/{Escape(symbol)}/24hAvgPrice", body: null, admin: false, cancellationToken);

    public Task<ApiResponse<SmaResponse>> GetSmaAsync(
        string symbol,
        int n,
        string period,
        string? start,
        bool series,
        int? count,
        bool includePoints,
        CancellationToken cancellationToken)
    {
        var query = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"n={n}&p={Escape(period)}"));
        if (start is not null)
        {
            query.Append(CultureInfo.InvariantCulture, $"&s={Escape(start)}");
        }

        if (series)
        {
            query.Append("&series=true");
            if (count is not null)
            {
                query.Append(CultureInfo.InvariantCulture, $"&count={count}");
            }
        }
        else
        {
            if (count is not null)
            {
                // Forward it anyway so the server reports the misuse exactly as for any other caller.
                query.Append(CultureInfo.InvariantCulture, $"&count={count}");
            }

            query.Append(includePoints ? "&includePoints=true" : "&includePoints=false");
        }

        return SendAsync<SmaResponse>(HttpMethod.Get, $"api/{Escape(symbol)}/SimpleMovingAverage?{query}", body: null, admin: false, cancellationToken);
    }

    public Task<ApiResponse<SymbolList>> ListSymbolsAsync(CancellationToken cancellationToken) =>
        SendAsync<SymbolList>(HttpMethod.Get, "api/admin/symbols", body: null, admin: true, cancellationToken);

    /// <summary>Adds or reactivates a symbol. <see cref="ApiResponse{T}.Status"/> is 201 when created and 200 when reactivated.</summary>
    public Task<ApiResponse<SymbolDto>> AddSymbolAsync(string symbol, CancellationToken cancellationToken) =>
        SendAsync<SymbolDto>(HttpMethod.Post, "api/admin/symbols", new AddSymbolRequest { Symbol = symbol }, admin: true, cancellationToken);

    public async Task RemoveSymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"api/admin/symbols/{Escape(symbol)}", body: null, admin: true);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await ProblemReader.ReadAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ApiResponse<T>> SendAsync<T>(HttpMethod method, string path, object? body, bool admin, CancellationToken cancellationToken)
        where T : class
    {
        using var request = CreateRequest(method, path, body, admin);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await ProblemReader.ReadAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new ApiResponse<T>(Deserialize<T>(raw), response.StatusCode, raw);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body, bool admin)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaType));
        if (admin && !string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        if (body is not null)
        {
            request.Content = new StringContent(Serialize(body), Encoding.UTF8, MediaType);
        }

        return request;
    }

    private string MediaType => format == ApiFormat.Xml ? "application/xml" : "application/json";

    private T Deserialize<T>(string raw)
        where T : class
    {
        try
        {
            if (format == ApiFormat.Json)
            {
                return JsonSerializer.Deserialize<T>(raw, Json) ?? throw new InvalidDataException("The response body was empty.");
            }

            using var reader = XmlReader.Create(new StringReader(raw));
            return (T)(new DataContractSerializer(typeof(T)).ReadObject(reader) ?? throw new InvalidDataException("The response body was empty."));
        }
        catch (Exception ex) when (ex is JsonException or SerializationException or XmlException)
        {
            throw new InvalidDataException($"The response could not be read as {format}: {ex.Message}", ex);
        }
    }

    private string Serialize(object body)
    {
        if (format == ApiFormat.Json)
        {
            return JsonSerializer.Serialize(body, body.GetType(), Json);
        }

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            new DataContractSerializer(body.GetType()).WriteObject(writer, body);
        }

        return sb.ToString();
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
