using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace MarketData.Client.Api;

/// <summary>An error response from the API, read from its RFC 7807 ProblemDetails body (JSON or XML).</summary>
public sealed class ApiProblemException(
    HttpStatusCode status,
    string title,
    string? detail,
    IReadOnlyDictionary<string, string[]> errors) : Exception(detail is null ? title : $"{title}: {detail}")
{
    public HttpStatusCode Status { get; } = status;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;

    /// <summary>Validation errors keyed by query parameter (<c>n</c>, <c>p</c>, <c>s</c>, <c>count</c>, <c>symbol</c>).</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

internal static class ProblemReader
{
    private static readonly XNamespace Rfc7807 = "urn:ietf:rfc:7807";

    public static async Task<ApiProblemException> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var fallbackTitle = response.ReasonPhrase is { Length: > 0 } reason ? reason : response.StatusCode.ToString();

        try
        {
            if (mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                return FromJson(response.StatusCode, body, fallbackTitle);
            }

            if (mediaType.EndsWith("xml", StringComparison.OrdinalIgnoreCase))
            {
                return FromXml(response.StatusCode, body, fallbackTitle);
            }
        }
        catch (Exception ex) when (ex is JsonException or XmlException)
        {
            // Not a readable ProblemDetails body; fall through to the status line.
        }

        return new ApiProblemException(response.StatusCode, fallbackTitle, string.IsNullOrWhiteSpace(body) ? null : body.Trim(), new Dictionary<string, string[]>());
    }

    private static ApiProblemException FromJson(HttpStatusCode status, string body, string fallbackTitle)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in errorsElement.EnumerateObject())
            {
                errors[property.Name] = property.Value.ValueKind == JsonValueKind.Array
                    ? [.. property.Value.EnumerateArray().Select(e => e.ToString())]
                    : [property.Value.ToString()];
            }
        }

        return new ApiProblemException(status, String(root, "title") ?? fallbackTitle, String(root, "detail"), errors);
    }

    /// <summary>
    /// MVC's XML ProblemDetails: <c>&lt;problem xmlns="urn:ietf:rfc:7807"&gt;</c> with <c>title</c>, <c>detail</c>
    /// and validation errors as child elements of <c>MVC-Errors</c>.
    /// </summary>
    private static ApiProblemException FromXml(HttpStatusCode status, string body, string fallbackTitle)
    {
        var root = XDocument.Parse(body).Root ?? throw new XmlException("Empty problem document.");
        var errors = root.Element(Rfc7807 + "MVC-Errors")?.Elements()
            .GroupBy(e => e.Name.LocalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Value).ToArray(), StringComparer.Ordinal)
            ?? new Dictionary<string, string[]>(StringComparer.Ordinal);

        return new ApiProblemException(
            status,
            root.Element(Rfc7807 + "title")?.Value ?? fallbackTitle,
            root.Element(Rfc7807 + "detail")?.Value,
            errors);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
