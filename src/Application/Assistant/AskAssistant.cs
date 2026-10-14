using AiFramework.Application.Abstractions;
using FluentValidation;

namespace AiFramework.Application.Assistant;

/// <summary>
/// Which tools a question may use. Application cannot see roles, so the API sets it from the
/// caller's capabilities on every question (ADR 0034): a demoted administrator is a Member on
/// their next question.
/// </summary>
public enum ToolScope
{
    /// <summary>The caller's own orders and exports, the catalogue, population figures.</summary>
    Member,

    /// <summary>Everything a member gets, plus the monitoring tools.</summary>
    Operator,
}

/// <summary>One question to the assistant. A null conversation id starts a new conversation.</summary>
public sealed record AskAssistant(Guid? ConversationId, string Question, ToolScope Scope) : ICommand<AssistantAnswer>;

public enum AssistantOutcome
{
    /// <summary>The model finished its answer.</summary>
    Answered,

    /// <summary>The tool-round cap was reached; the model answered with what it had.</summary>
    Capped,

    /// <summary>The answer hit the output token limit and is incomplete.</summary>
    Truncated,
}

public sealed record AssistantAnswer(Guid ConversationId, AssistantOutcome Outcome);

public sealed class AskAssistantValidator : AbstractValidator<AskAssistant>
{
    public const int MaxQuestionLength = 2000;

    public AskAssistantValidator()
    {
        RuleFor(c => c.Question)
            .NotEmpty()
            .Must(q => q.Trim().Length <= MaxQuestionLength)
            .WithMessage($"A question cannot be longer than {MaxQuestionLength} characters.");
    }
}

/// <summary>
/// The tool-use loop. Collects the whole question — the question, every model turn, every tool
/// result — and appends it once, at the end: the unit of work commits it with the command, so a
/// failed, refused or cancelled question stores nothing and the conversation stays valid.
/// ADR 0033.
/// </summary>
public sealed class AskAssistantHandler(
    IAssistantModel model,
    IAssistantConversations conversations,
    IAssistantOutput output,
    AssistantToolbox toolbox,
    ICurrentUser currentUser,
    IClock clock) : ICommandHandler<AskAssistant, AssistantAnswer>
{
    public const int MaxToolRounds = 8;

    public async Task<Result<AssistantAnswer>> HandleAsync(AskAssistant command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } ownerId)
        {
            return Result.Failure<AssistantAnswer>(new Error(ErrorKind.Unauthorized, "assistant.no_caller", "Sign in to use the assistant."));
        }

        var now = clock.UtcNow;
        var opened = await AssistantConversationOpener.OpenAsync(
            conversations, command.ConversationId, ownerId, command.Question, now, forAi: true, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result.Failure<AssistantAnswer>(opened.Error);
        }

        var added = new List<AssistantTurn> { new UserQuestionTurn(command.Question.Trim(), now) };
        var outcome = await RunAsync(opened.Value.Turns, added, command.Scope, cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccess)
        {
            return Result.Failure<AssistantAnswer>(outcome.Error);
        }

        conversations.Append(opened.Value.Id, added, now);
        return Result.Success(new AssistantAnswer(opened.Value.Id, outcome.Value));
    }

    /// <summary>Null means "run the tools and go round again"; anything else ends the question.</summary>
    private static Result<AssistantOutcome>? Settle(AssistantModelTurn turn, bool allowTools)
    {
        if (turn.StopReason == AssistantStopReason.Refusal)
        {
            return Result.Failure<AssistantOutcome>(new Error(ErrorKind.Validation, "assistant.refused", "The assistant can't answer that."));
        }

        if (turn.ToolCalls.Count > 0)
        {
            // A tool call is only safe to store with its result. Without a tool_use stop (a
            // truncated call) or with tools off, there is no result to pair it with, and a stored
            // unpaired call makes every later question in the conversation fail.
            return turn.StopReason == AssistantStopReason.ToolUse && allowTools ? null : Incomplete();
        }

        return Result.Success(turn.StopReason switch
        {
            AssistantStopReason.MaxTokens => AssistantOutcome.Truncated,
            _ when !allowTools => AssistantOutcome.Capped,
            _ => AssistantOutcome.Answered,
        });
    }

    private static Result<AssistantOutcome> Incomplete() =>
        Result.Failure<AssistantOutcome>(new Error(
            ErrorKind.Unavailable, "assistant.incomplete", "The assistant stopped part-way through. Try again."));

    private async Task<Result<AssistantOutcome>> RunAsync(
        IReadOnlyList<AssistantTurn> history, List<AssistantTurn> added, ToolScope scope, CancellationToken cancellationToken)
    {
        for (var round = 0; round <= MaxToolRounds; round++)
        {
            var allowTools = round < MaxToolRounds;
            var request = new AssistantRequest(AssistantPrompt.For(scope), AssistantToolbox.For(scope), [.. history, .. added], allowTools);

            var next = await model.NextTurnAsync(request, output, cancellationToken).ConfigureAwait(false);
            if (!next.IsSuccess)
            {
                return Result.Failure<AssistantOutcome>(next.Error);
            }

            var turn = next.Value;
            added.Add(turn.Turn);

            var settled = Settle(turn, allowTools);
            if (settled is not null)
            {
                return settled;
            }

            added.Add(await RunToolsAsync(turn.ToolCalls, scope, cancellationToken).ConfigureAwait(false));
        }

        // Unreachable: the last round sends tool_choice none, and Settle ends every tool-free turn.
        return Incomplete();
    }

    private async Task<ToolResultsTurn> RunToolsAsync(
        IReadOnlyList<AssistantToolCall> calls, ToolScope scope, CancellationToken cancellationToken)
    {
        var results = new List<AssistantToolResult>(calls.Count);
        foreach (var call in calls)
        {
            // A tool the question was not offered — a guess, or text in a tool result steering the
            // model — is answered, never run and never shown. Every call still gets its result, so
            // the stored history stays well-formed.
            if (!AssistantToolbox.Offers(scope, call.Name))
            {
                results.Add(new AssistantToolResult(call.Id, $"The tool '{call.Name}' is not available here.", IsError: true));
                continue;
            }

            await output.ToolAsync(call.Name, cancellationToken).ConfigureAwait(false);
            results.Add(await toolbox.RunAsync(call, cancellationToken).ConfigureAwait(false));
        }

        return new ToolResultsTurn(results);
    }
}
