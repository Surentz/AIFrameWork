using AiFramework.Application.Abstractions;
using AiFramework.Application.Rates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Rates;

/// <summary>
/// ADR 0014's reference resilience integration, exposed over HTTP: a live foreign-exchange rate
/// behind a third-party call, wrapped in the standard retry/timeout/circuit-breaker pipeline.
/// [Authorize] for the same mechanical reason <c>ProductsController</c> is — <c>GetExchangeRate</c>
/// is <c>ICacheable</c>, and the caching behavior throws when one is dispatched with no caller to
/// scope its key to.
/// </summary>
[ApiController]
[Route("api/rates")]
[Authorize]
public sealed class RatesController(IQueryDispatcher queries) : ControllerBase
{
    /// <summary>
    /// The current rate to convert one unit of <paramref name="from"/> into
    /// <paramref name="to"/>. 503 is this endpoint's distinctive failure: the provider's own
    /// retry budget (ADR 0014) has already been exhausted by the time one reaches a caller here.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ExchangeRateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Get(
        [FromQuery] string from, [FromQuery] string to, CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetExchangeRate(from, to), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new ExchangeRateResponse
            {
                From = result.Value.From,
                To = result.Value.To,
                Rate = result.Value.Rate,
                AsOf = result.Value.AsOf,
            })
            : result.Problem(HttpContext);
    }
}
