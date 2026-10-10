using AiFramework.Application.Abstractions;
using AiFramework.Application.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Statistics;

/// <summary>
/// Official population figures from Statistics Denmark: the external systems pilot (ADR 0031).
/// [Authorize] for the reason <c>RatesController</c> is: both queries are <c>ICacheable</c>, and
/// the caching behavior needs a caller to scope its key to. 503 is the distinctive failure: the
/// source's own retry budget is spent before one reaches a caller.
/// </summary>
[ApiController]
[Route("api/statistics/population")]
[Authorize]
public sealed class StatisticsController(IQueryDispatcher queries) : ControllerBase
{
    /// <summary>The latest quarter's population for a three-digit area code (000 is all of Denmark).</summary>
    [HttpGet]
    [ProducesResponseType<PopulationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Get([FromQuery] string? area, CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetPopulation(area), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new PopulationResponse
            {
                AreaCode = result.Value.AreaCode,
                AreaName = result.Value.AreaName,
                Period = result.Value.Period,
                Population = result.Value.Population,
                Source = result.Value.Source,
            })
            : result.Problem(HttpContext);
    }

    /// <summary>Every area figures are published for: all of Denmark, the regions and the municipalities.</summary>
    [HttpGet("areas")]
    [ProducesResponseType<PopulationAreasResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetAreas(CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetPopulationAreas(), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new PopulationAreasResponse
            {
                Areas = [.. result.Value.Areas.Select(area => new PopulationAreaResponse { Code = area.Code, Name = area.Name })],
                Source = result.Value.Source,
            })
            : result.Problem(HttpContext);
    }
}
