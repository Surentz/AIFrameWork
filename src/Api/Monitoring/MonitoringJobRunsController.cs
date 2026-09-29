using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>What every job attempt did. See <c>MonitoringJobsController</c> for the area's rules.</summary>
[ApiController]
[Route("api/monitoring/jobs/runs")]
[Authorize(Policy = AuthorizationPolicies.Monitoring.Read)]
public sealed class MonitoringJobRunsController(IQueryDispatcher queries) : ControllerBase
{
    /// <summary>The job-run table, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<JobRunPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> List(
        [FromQuery] JobRunStatus? status = null,
        [FromQuery] string? jobName = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await queries
            .SendAsync(new GetJobRuns(status, jobName, page, pageSize), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new JobRunPageResponse
            {
                Items = [.. result.Value.Items.Select(ToResponse)],
                TotalCount = result.Value.TotalCount,
                Page = result.Value.Page,
            })
            : result.Problem(HttpContext);
    }

    /// <summary>One attempt, with its full error text.</summary>
    [HttpGet("{envelopeId:guid}/{attempt:int}")]
    [ProducesResponseType<JobRunResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Get(
        Guid envelopeId, int attempt, CancellationToken cancellationToken)
    {
        var result = await queries
            .SendAsync(new GetJobRun(envelopeId, attempt), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Ok(ToResponse(result.Value)) : result.Problem(HttpContext);
    }

    private static JobRunResponse ToResponse(JobRunView run) => new()
    {
        EnvelopeId = run.EnvelopeId,
        Attempt = run.Attempt,
        JobName = run.JobName,
        Lane = run.Lane,
        Status = run.Status,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        DurationMs = run.DurationMs,
        OwnerId = run.OwnerId,
        Error = run.Error,
        TraceId = run.TraceId,
        InstanceId = run.InstanceId,
    };
}
