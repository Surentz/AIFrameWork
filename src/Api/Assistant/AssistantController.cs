using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AiFramework.Api.Assistant;

/// <summary>
/// The ops assistant (ADR 0033). Read-only questions about monitoring data, answered as a stream.
/// Same policy as the Monitoring pages: the assistant can see exactly what they show.
/// </summary>
[ApiController]
[Route("api/assistant")]
[Authorize(Policy = AuthorizationPolicies.Monitoring.Read)]
[TypeFilter<AssistantStreamExceptionFilter>]
public sealed class AssistantController(
    ICommandDispatcher commands, AssistantAvailability availability, SseAssistantOutput output) : ControllerBase
{
    /// <summary>Whether the assistant is switched on, so the UI can hide itself.</summary>
    [HttpGet]
    [ProducesResponseType<AssistantStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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

        Result<AssistantAnswer> result;
        using var stopKeepAlive = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepAlive = output.KeepAliveAsync(availability.HeartbeatInterval, stopKeepAlive.Token);
        try
        {
            result = await commands
                .SendAsync(new AskAssistant(request.ConversationId, request.Question), cancellationToken)
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
