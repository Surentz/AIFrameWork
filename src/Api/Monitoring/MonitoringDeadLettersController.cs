using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// Messages that exhausted their retries, and the action that puts one back in play.
/// </summary>
[ApiController]
[Route("api/monitoring/jobs/dead-letters")]
[Authorize(Policy = AuthorizationPolicies.Monitoring)]
// S6960: the rule sees the read and the retry as disjoint groups. Declined on the same grounds
// NotificationsController records — api/monitoring/jobs/dead-letters is one REST resource, and
// its read and its one write belong together rather than in two files organised by verb.
#pragma warning disable S6960
public sealed class MonitoringDeadLettersController(
    ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
#pragma warning restore S6960
{
    /// <summary>The dead-letter queue.</summary>
    [HttpGet]
    [ProducesResponseType<DeadLetterPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await queries.SendAsync(new GetDeadLetters(page, pageSize), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new DeadLetterPageResponse
            {
                Items =
                [
                    .. result.Value.Items.Select(letter => new DeadLetterResponse
                    {
                        Id = letter.Id,
                        MessageType = letter.MessageType,
                        ExceptionType = letter.ExceptionType,
                        ExceptionMessage = letter.ExceptionMessage,
                        SentAt = letter.SentAt,
                        ReceivedAt = letter.ReceivedAt,
                        Replayable = letter.Replayable,
                    }),
                ],
                TotalCount = result.Value.TotalCount,
                Page = result.Value.Page,
            })
            : result.Problem(HttpContext);
    }

    /// <summary>Puts one dead-lettered message back in play.</summary>
    /// <remarks>
    /// The API marks the stored envelope replayable and stops there; the worker picks it up from
    /// the shared PostgreSQL store on its own. The two hosts never talk, which is what makes this
    /// page possible without exposing the worker through the ingress. See ADR 0016.
    /// </remarks>
    [HttpPost("{messageId:guid}/retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Retry(Guid messageId, CancellationToken cancellationToken)
    {
        var result = await commands
            .SendAsync(new RetryDeadLetter(messageId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : result.Problem(HttpContext);
    }
}
