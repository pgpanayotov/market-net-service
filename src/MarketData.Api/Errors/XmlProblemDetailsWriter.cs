using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters.Xml;
using Microsoft.Net.Http.Headers;

namespace MarketData.Api.Errors;

/// <summary>
/// Writes ProblemDetails as XML for errors raised outside MVC (401 challenges, unmatched routes, the
/// exception handler) when the client prefers XML (ADR-004). The default writer is JSON only. It is
/// registered ahead of the JSON writer and declines unless XML wins the <c>Accept</c> header.
/// </summary>
public sealed class XmlProblemDetailsWriter : IProblemDetailsWriter
{
    public const string ContentType = "application/problem+xml";

    private static readonly DataContractSerializer ProblemSerializer = new(typeof(ProblemDetailsWrapper));
    private static readonly DataContractSerializer ValidationSerializer = new(typeof(ValidationProblemDetailsWrapper));

    public bool CanWrite(ProblemDetailsContext context) => PrefersXml(context.HttpContext.Request);

    public async ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var (serializer, wrapper) = context.ProblemDetails is ValidationProblemDetails validation
            ? (ValidationSerializer, (object)new ValidationProblemDetailsWrapper(validation))
            : (ProblemSerializer, new ProblemDetailsWrapper(context.ProblemDetails));

        using var buffer = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), OmitXmlDeclaration = true };
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            serializer.WriteObject(writer, wrapper);
        }

        var response = context.HttpContext.Response;
        response.ContentType = ContentType;
        response.ContentLength = buffer.Length;
        await response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), context.HttpContext.RequestAborted);
    }

    /// <summary>
    /// <c>true</c> when the most preferred <c>Accept</c> entry that this API can serve is XML. Entries
    /// are ranked by quality and, for equal quality, by their order in the header. Wildcards and JSON
    /// mean JSON, the default format.
    /// </summary>
    public static bool PrefersXml(HttpRequest request)
    {
        var accept = request.GetTypedHeaders().Accept;
        foreach (var value in accept.Select((v, i) => (v, i)).OrderByDescending(x => x.v.Quality ?? 1.0).ThenBy(x => x.i).Select(x => x.v))
        {
            if (value.Quality == 0)
            {
                continue;
            }

            var subtype = value.SubTypeWithoutSuffix.Value ?? string.Empty;
            var suffix = value.Suffix.Value ?? string.Empty;
            if (subtype.Equals("xml", StringComparison.OrdinalIgnoreCase) || suffix.Equals("xml", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.MatchesAllTypes || value.MatchesAllSubTypes
                || subtype.Equals("json", StringComparison.OrdinalIgnoreCase) || suffix.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return false;
    }
}
