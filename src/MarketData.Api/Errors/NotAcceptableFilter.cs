using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace MarketData.Api.Errors;

/// <summary>
/// Returns 406 before the action runs when the <c>Accept</c> header names no format this API writes.
/// <see cref="MvcOptions.ReturnHttpNotAcceptable"/> only applies when the result is formatted, which is
/// after an admin write has already committed, and never to bodiless results such as 204.
/// </summary>
public sealed class NotAcceptableFilter : IResourceFilter
{
    private static readonly MediaType[] Supported =
    [
        new("application/json"), new("text/json"), new("application/problem+json"),
        new("application/xml"), new("text/xml"), new("application/problem+xml"),
    ];

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var accept = context.HttpContext.Request.GetTypedHeaders().Accept;
        if (accept.Count == 0)
        {
            return;
        }

        var acceptable = accept.Any(value => value.Quality != 0
            && Supported.Any(supported => supported.IsSubsetOf(new MediaType(value.MediaType.Value ?? string.Empty))));
        if (!acceptable)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status406NotAcceptable);
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
