using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// How the assistant is doing: answers by path per day, the most-asked targets, and the questions
/// it missed, scrubbed. Administrators only (ADR 0037).
/// </summary>
[ApiController]
[Route("api/monitoring/assistant")]
[Authorize(Policy = AuthorizationPolicies.Monitoring.Read)]
public sealed class MonitoringAssistantController(IQueryDispatcher queries) : ControllerBase
{
    [HttpGet("insights")]
    [ProducesResponseType<AssistantInsightsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Insights([FromQuery] int days = 7, CancellationToken cancellationToken = default)
    {
        var result = await queries.SendAsync(new GetAssistantInsights(days), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new AssistantInsightsResponse
            {
                Since = result.Value.Since,
                Days = [.. result.Value.Days.Select(static d => new AssistantInsightsDayResponse
                {
                    Day = d.Day, Instant = d.Instant, Help = d.Help, Suggested = d.Suggested, Ai = d.Ai,
                })],
                Targets = [.. result.Value.Targets.Select(static t => new AssistantInsightsTargetResponse
                {
                    Path = t.Path, TargetId = t.TargetId, Questions = t.Questions, Up = t.Up, Down = t.Down,
                })],
                Misses = [.. result.Value.Misses.Select(static m => new AssistantInsightsMissResponse
                {
                    At = m.At, Path = m.Path, TargetId = m.TargetId, BestScore = m.BestScore, Rating = m.Rating, Question = m.Question,
                })],
            })
            : result.Problem(HttpContext);
    }
}
