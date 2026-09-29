using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// The jobs area's own summary, and the one action that starts work rather than inspecting it.
/// </summary>
/// <remarks>
/// One controller per resource beneath <c>api/monitoring</c>, rather than one that accumulates
/// every operator concern: runs and dead letters have their own. The policy sits on each
/// CONTROLLER, not on each action, so an endpoint added later is gated by default. A member gets
/// 403 rather than 404 — this route leaks nothing by admitting it exists. See ADR 0020.
/// </remarks>
[ApiController]
[Route("api/monitoring/jobs")]
[Authorize(Policy = AuthorizationPolicies.Monitoring.Read)]
// S6960: the rule reads "counts by outcome" and "start a job now" as disjoint groups and
// proposes a controller each. Declined for the reason NotificationsController already records:
// api/monitoring/jobs is ONE REST resource, and one endpoint per controller would organise this
// area by verb rather than by resource — the opposite of how the rest of the solution reads. The
// area IS already split by resource: runs and dead letters have controllers of their own.
#pragma warning disable S6960
public sealed class MonitoringJobsController(
    ICommandDispatcher commands,
    IQueryDispatcher queries,
    ITriggerableJobs triggerable) : ControllerBase
#pragma warning restore S6960
{
    /// <summary>Counts by outcome over a trailing window, for the overview tiles.</summary>
    [HttpGet("health")]
    [ProducesResponseType<JobHealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Health(
        [FromQuery] int windowHours = 24, CancellationToken cancellationToken = default)
    {
        var result = await queries
            .SendAsync(new GetJobHealth(TimeSpan.FromHours(windowHours)), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new JobHealthResponse
            {
                Running = result.Value.Running,
                Succeeded = result.Value.Succeeded,
                Failed = result.Value.Failed,
                DeadLettered = result.Value.DeadLettered,
                Since = result.Value.Since,
                TriggerableJobs = triggerable.Names,
            })
            : result.Problem(HttpContext);
    }

    /// <summary>Runs a scheduled job now, without waiting for its cron.</summary>
    [HttpPost("trigger")]
    [Authorize(Policy = AuthorizationPolicies.Monitoring.Operate)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Trigger(
        TriggerJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await commands
            .SendAsync(new TriggerJob(request.JobName), cancellationToken)
            .ConfigureAwait(false);

        // 202, not 200: the job is queued, and whether it succeeds is a later job_runs row.
        return result.IsSuccess ? Accepted() : result.Problem(HttpContext);
    }
}
