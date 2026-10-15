using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Assistant;

/// <summary>
/// The caller's own conversations, for the sidebar (ADR 0034). Another user's id is a 404, the
/// same as a missing one. Every action is a 404 while the assistant is switched off.
/// </summary>
[ApiController]
[Route("api/assistant/conversations")]
[Authorize]
public sealed class AssistantConversationsController(
    IQueryDispatcher queries, ICommandDispatcher commands, AssistantAvailability availability) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AssistantConversationSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!availability.AnyEnabled)
        {
            return Disabled();
        }

        var result = await queries.SendAsync(new GetAssistantConversations(), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value.Select(static c => new AssistantConversationSummaryResponse
            {
                Id = c.Id,
                Title = c.Title,
                LastActiveAt = c.LastActiveAt,
            }))
            : result.Problem(HttpContext);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<AssistantConversationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!availability.AnyEnabled)
        {
            return Disabled();
        }

        var result = await queries.SendAsync(new GetAssistantConversation(id), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new AssistantConversationResponse
            {
                Id = result.Value.Id,
                Title = result.Value.Title,
                Messages = [.. result.Value.Messages.Select(m => new AssistantMessageResponse
                {
                    Role = m.Role,
                    Text = m.Text,
                    Tools = m.Tools,
                    Answer = m.Answer is { } answer ? AssistantCards.ToResponse(answer) : null,
                    Suggestions = m.Suggestions is { } suggestions ? AssistantCards.ToResponse(suggestions, availability.AiEnabled) : null,
                })],
            })
            : result.Problem(HttpContext);
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Rename(Guid id, RenameAssistantConversationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!availability.AnyEnabled)
        {
            return Disabled();
        }

        var result = await commands.SendAsync(new RenameAssistantConversation(id, request.Title), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.Problem(HttpContext);
    }

    /// <summary>The owner rates one answer (<paramref name="turn"/> is the turn the answer's events and messages carry), or clears it with a null.</summary>
    [HttpPut("{id:guid}/turns/{turn:int}/rating")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rate(Guid id, int turn, RateAnswerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!availability.AnyEnabled)
        {
            return Disabled();
        }

        var result = await commands.SendAsync(new RateAssistantAnswer(id, turn, request.Rating), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.Problem(HttpContext);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!availability.AnyEnabled)
        {
            return Disabled();
        }

        var result = await commands.SendAsync(new DeleteAssistantConversation(id), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.Problem(HttpContext);
    }

    private ObjectResult Disabled() =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "assistant.disabled", detail: "The assistant is switched off.");
}
