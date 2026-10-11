using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AiFramework.Api.Assistant;

/// <summary>
/// The assistant (ADRs 0033, 0034): questions answered as a stream, for every signed-in user. Which
/// tools a question gets is decided per question from the caller's capabilities, never from this
/// attribute.
/// </summary>
[ApiController]
[Route("api/assistant")]
[Authorize]
[TypeFilter<AssistantStreamExceptionFilter>]
public sealed class AssistantController(
    ICommandDispatcher commands,
    IAuthorizationService authorization,
    AssistantAvailability availability,
    SseAssistantOutput output) : ControllerBase
{
    /// <summary>Whether the assistant is switched on, so the UI can hide itself.</summary>
    [HttpGet]
    [ProducesResponseType<AssistantStatusResponse>(StatusCodes.Status200OK)]
    public ActionResult<AssistantStatusResponse> Status() => new AssistantStatusResponse { Enabled = availability.Enabled };

    /// <summary>
    /// Asks one question. The response is text/event-stream: text and tool events while the answer
    /// is written, then done or error. A failure before the first event is an ordinary problem.
    /// </summary>
    /// <remarks>
    /// done is written only after the command returned, which is after the unit of work committed:
    /// done means saved.
    /// </remarks>
    [HttpPost("messages")]
    [EnableRateLimiting(AssistantRateLimiting.PolicyName)]
    [ProducesResponseType<AssistantEventResponse>(StatusCodes.Status200OK, "text/event-stream")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ask(AskAssistantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!availability.Enabled)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "assistant.disabled", detail: "The assistant is switched off.");
        }

        // The same policy the Monitoring pages use, evaluated now: it reads the role from the
        // database, so a demoted administrator loses the monitoring tools on this very question.
        var scope = (await authorization.AuthorizeAsync(User, AuthorizationPolicies.Monitoring.Read).ConfigureAwait(false)).Succeeded
            ? ToolScope.Operator
            : ToolScope.Member;

        Result<AssistantAnswer> result;
        using var stopKeepAlive = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepAlive = output.KeepAliveAsync(availability.HeartbeatInterval, stopKeepAlive.Token);
        try
        {
            result = await commands
                .SendAsync(new AskAssistant(request.ConversationId, request.Question, scope), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Stopped before the final event, success or not, so a comment never follows it.
            await stopKeepAlive.CancelAsync().ConfigureAwait(false);
            await keepAlive.ConfigureAwait(false);
        }

        if (!result.IsSuccess && !output.Started)
        {
            return result.Problem(HttpContext);
        }

        await output.WriteAsync(
            result.IsSuccess
                ? AssistantEventResponse.Done(result.Value)
                : AssistantEventResponse.Error(result.Error.ToProblemDetails(HttpContext)),
            cancellationToken).ConfigureAwait(false);

        return new EmptyResult();
    }
}
