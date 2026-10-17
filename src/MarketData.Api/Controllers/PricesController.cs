using MarketData.Api.Binding;
using MarketData.Contracts;
using MarketData.Core.Analysis;
using Microsoft.AspNetCore.Mvc;

namespace MarketData.Api.Controllers;

/// <summary>Price analysis, anchored at the newest stored candle (ADR-006). Public, read-only.</summary>
[ApiController]
[Route("api/{symbol}")]
[ProducesResponseType(StatusCodes.Status406NotAcceptable)]
public sealed class PricesController(IPriceAnalysisService analysis) : ControllerBase
{
    /// <summary>Average close over the 24 hours that end at the newest stored candle.</summary>
    /// <param name="symbol">Trading pair, e.g. <c>BTCUSDT</c> (case-insensitive).</param>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpGet("24hAvgPrice")]
    [ProducesResponseType<AvgPriceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AvgPriceResponse>> Get24hAverage(string symbol, CancellationToken cancellationToken) =>
        this.ToActionResult(await analysis.Get24hAverageAsync(symbol, cancellationToken));

    /// <summary>Simple moving average of the last <paramref name="n"/> complete <paramref name="p"/> buckets.</summary>
    /// <param name="symbol">Trading pair, e.g. <c>BTCUSDT</c> (case-insensitive).</param>
    /// <param name="n">Number of buckets, 1 to 1000.</param>
    /// <param name="p">Bucket size: <c>1m</c>, <c>5m</c>, <c>30m</c>, <c>1d</c> or <c>1w</c> (case-sensitive).</param>
    /// <param name="s">Reference time (ISO-8601, UTC when no offset is given); defaults to the newest stored candle.</param>
    /// <param name="series">Return a rolling series instead of a single value.</param>
    /// <param name="count">Series length, 1 to 1000; only with <c>series=true</c>. Defaults to <c>min(n, 100)</c>.</param>
    /// <param name="includePoints">Include the bucket closes in a single-value response.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpGet("SimpleMovingAverage")]
    [ProducesResponseType<SmaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SmaResponse>> GetSimpleMovingAverage(
        string symbol,
        [FromQuery] int n,
        [FromQuery] string? p,
        [FromQuery, ModelBinder(typeof(UtcDateTimeOffsetModelBinder))] DateTimeOffset? s,
        [FromQuery] bool series = false,
        [FromQuery] int? count = null,
        [FromQuery] bool includePoints = true,
        CancellationToken cancellationToken = default) =>
        this.ToActionResult(await analysis.GetSmaAsync(new SmaQuery(symbol, n, p, s, series, count, includePoints), cancellationToken));
}
