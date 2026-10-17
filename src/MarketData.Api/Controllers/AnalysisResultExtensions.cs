using MarketData.Core.Analysis;
using Microsoft.AspNetCore.Mvc;

namespace MarketData.Api.Controllers;

internal static class AnalysisResultExtensions
{
    public const string UnknownSymbolDetail = "Unknown symbol";
    public const string NoDataDetail = "No data available";

    /// <summary>Maps an analysis result to 200, 400 (ValidationProblem keyed by query parameter) or 404 (ADR-006).</summary>
    public static ActionResult<T> ToActionResult<T>(this ControllerBase controller, AnalysisResult<T> result)
        where T : class
    {
        switch (result.Error)
        {
            case null:
                return controller.Ok(result.Value);

            case AnalysisError.Invalid invalid:
                foreach (var (key, messages) in invalid.Errors)
                {
                    foreach (var message in messages)
                    {
                        controller.ModelState.AddModelError(key, message);
                    }
                }

                return controller.ValidationProblem();

            case AnalysisError.UnknownSymbol:
                return controller.Problem(statusCode: StatusCodes.Status404NotFound, detail: UnknownSymbolDetail);

            case AnalysisError.NoData:
                return controller.Problem(statusCode: StatusCodes.Status404NotFound, detail: NoDataDetail);

            default:
                throw new InvalidOperationException($"Unhandled analysis error {result.Error.GetType().Name}.");
        }
    }
}
