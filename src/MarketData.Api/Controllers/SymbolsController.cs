using MarketData.Api.Auth;
using MarketData.Api.Symbols;
using MarketData.Contracts;
using MarketData.Core.Abstractions;
using MarketData.Core.Domain;
using MarketData.Core.Enums;
using MarketData.Infrastructure.Binance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketData.Api.Controllers;

/// <summary>
/// Runtime symbol management (requires <c>X-Api-Key</c>). Writes are asynchronous with respect to
/// ingestion: a 2xx means the change is persisted, and the worker applies it within about a second.
/// </summary>
[ApiController]
[Route("api/admin/symbols")]
[Authorize(Policy = ApiKeyAuthenticationHandler.AdminPolicy)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status406NotAcceptable)]
public sealed class SymbolsController(
    ISymbolRepository symbols,
    SymbolAdminService admin,
    IExchangeSymbolValidator exchange) : ControllerBase
{
    private const string SymbolKey = "symbol";

    /// <summary>All symbols, active and removed, with the open time of their newest stored candle.</summary>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpGet]
    [ProducesResponseType<SymbolList>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SymbolList>> List(CancellationToken cancellationToken) =>
        new SymbolList((await symbols.ListAsync(cancellationToken)).Select(ToDto));

    /// <summary>Adds a symbol, or reactivates a removed one. It must be a TRADING spot symbol on Binance.</summary>
    /// <param name="request">The symbol to add.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpPost]
    [ProducesResponseType<SymbolDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<SymbolDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SymbolDto>> Add(AddSymbolRequest request, CancellationToken cancellationToken)
    {
        if (!SymbolName.TryCreate(request.Symbol, out var name))
        {
            return InvalidSymbol("Must be 5-20 letters or digits, e.g. BTCUSDT.");
        }

        bool tradable;
        try
        {
            tradable = await exchange.IsTradableAsync(name, cancellationToken);
        }
        catch (ExchangeInfoUnavailableException)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Binance exchangeInfo is unavailable.",
                detail: "The symbol could not be validated; try again later.");
        }

        if (!tradable)
        {
            return InvalidSymbol($"{name} is not a TRADING spot symbol on Binance.");
        }

        var outcome = await admin.AddAsync(name, cancellationToken);
        if (outcome == SymbolActivation.AlreadyActive)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Symbol already active.", detail: $"{name} is already monitored.");
        }

        var dto = (await symbols.ListAsync(cancellationToken)).Where(s => s.Name == name).Select(ToDto).Single();
        return outcome == SymbolActivation.Created
            ? Created($"/api/admin/symbols/{name}", dto)
            : Ok(dto);
    }

    /// <summary>Stops monitoring a symbol. Its history is kept and stays queryable.</summary>
    /// <param name="symbol">The symbol to remove.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpDelete("{symbol}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(string symbol, CancellationToken cancellationToken)
    {
        if (!SymbolName.TryCreate(symbol, out var name))
        {
            return InvalidSymbol("Must be 5-20 letters or digits, e.g. BTCUSDT.");
        }

        return await admin.RemoveAsync(name, cancellationToken) switch
        {
            SymbolDeactivation.NotFound => Problem(statusCode: StatusCodes.Status404NotFound, detail: AnalysisResultExtensions.UnknownSymbolDetail),
            _ => NoContent(),
        };
    }

    private ActionResult InvalidSymbol(string message)
    {
        ModelState.AddModelError(SymbolKey, message);
        return ValidationProblem();
    }

    private static SymbolDto ToDto(SymbolDetails s) => new()
    {
        Name = s.Name.Value,
        IsActive = s.IsActive,
        CreatedAt = s.CreatedAt.UtcDateTime,
        LastCandleAt = s.LastCandleAt?.UtcDateTime,
    };
}
