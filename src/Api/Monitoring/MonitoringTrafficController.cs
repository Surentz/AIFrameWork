using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// How much work the application is doing, how much of it fails, and how slow it is.
/// </summary>
/// <remarks>
/// Every number here is summed across pods. A row is one pod's minute, and reading one pod's rows
/// would report whichever replica the operator's own session is pinned to — which under ADR
/// 0010's cookie affinity is stable enough to look like the truth. See ADR 0021.
/// </remarks>
[ApiController]
[Route("api/monitoring/traffic")]
[Authorize(Policy = AuthorizationPolicies.Monitoring.Read)]
public sealed class MonitoringTrafficController(IQueryDispatcher queries) : ControllerBase
{
    /// <summary>Traffic over a trailing window, whole and per endpoint or handler.</summary>
    [HttpGet]
    [ProducesResponseType<TrafficSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Summary(
        [FromQuery] int windowMinutes = 60, CancellationToken cancellationToken = default)
    {
        var result = await queries
            .SendAsync(new GetTrafficSummary(TimeSpan.FromMinutes(windowMinutes)), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new TrafficSummaryResponse
            {
                Since = result.Value.Since,
                RequestsPerMinute = result.Value.RequestsPerMinute,
                ErrorRate = result.Value.ErrorRate,
                Overall = ToResponse(result.Value.Overall),
                Rows = [.. result.Value.Rows.Select(ToResponse)],
            })
            : result.Problem(HttpContext);
    }

    /// <summary>The per-minute series behind the traffic chart.</summary>
    [HttpGet("series")]
    [ProducesResponseType<TrafficSeriesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Series(
        [FromQuery] int windowMinutes = 60, CancellationToken cancellationToken = default)
    {
        var result = await queries
            .SendAsync(new GetTrafficSeries(TimeSpan.FromMinutes(windowMinutes)), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new TrafficSeriesResponse
            {
                Since = result.Value.Since,
                Points =
                [
                    .. result.Value.Points.Select(point => new TrafficPointResponse
                    {
                        BucketStart = point.BucketStart,
                        Total = point.Total,
                        Failed = point.Failed,
                        Faulted = point.Faulted,
                        P95Ms = point.P95Ms,
                    }),
                ],
            })
            : result.Problem(HttpContext);
    }

    private static TrafficRowResponse ToResponse(TrafficRowView row) => new()
    {
        Kind = row.Kind,
        Name = row.Name,
        Total = row.Total,
        Failed = row.Failed,
        Faulted = row.Faulted,
        MeanMs = row.MeanMs,
        P50Ms = row.P50Ms,
        P95Ms = row.P95Ms,
        P99Ms = row.P99Ms,
    };
}
