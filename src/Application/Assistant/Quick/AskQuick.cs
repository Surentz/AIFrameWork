using AiFramework.Application.Abstractions;
using FluentValidation;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>One question for the instant path. A null conversation id starts a new conversation. ADR 0036.</summary>
public sealed record AskQuick(Guid? ConversationId, string Question, ToolScope Scope) : ICommand<AssistantAnswer>;

public sealed class AskQuickValidator : AbstractValidator<AskQuick>
{
    public AskQuickValidator()
    {
        RuleFor(c => c.Question)
            .NotEmpty()
            .Must(q => q.Trim().Length <= AskAssistantValidator.MaxQuestionLength)
            .WithMessage($"A question cannot be longer than {AskAssistantValidator.MaxQuestionLength} characters.");
    }
}

/// <summary>
/// Routes the question, answers it from the caller's own data or suggests, writes the reply, and
/// appends the question and the reply to the conversation as one transaction (the unit of work
/// commits after the command). Never calls a model and never dispatches a command. When the router
/// is unavailable (the index did not build, or the model failed) the question fails Unavailable and
/// nothing is stored: a stored "not available" would count against the conversation and be replayed
/// to Claude as something the assistant said. ADR 0036.
/// </summary>
public sealed class AskQuickHandler(
    IntentRouter router,
    QuickAnswerComposer composer,
    IAssistantConversations conversations,
    IAssistantOutput output,
    ICurrentUser currentUser,
    IClock clock) : ICommandHandler<AskQuick, AssistantAnswer>
{
    public async Task<Result<AssistantAnswer>> HandleAsync(AskQuick command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } ownerId)
        {
            return Result.Failure<AssistantAnswer>(new Error(ErrorKind.Unauthorized, "assistant.no_caller", "Sign in to use the assistant."));
        }

        var question = command.Question.Trim();
        var routed = router.Route(question, command.Scope);
        if (!routed.IsSuccess)
        {
            return Result.Failure<AssistantAnswer>(routed.Error);
        }

        var now = clock.UtcNow;
        var opened = await AssistantConversationOpener.OpenAsync(
            conversations, command.ConversationId, ownerId, question, now, forAi: false, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result.Failure<AssistantAnswer>(opened.Error);
        }

        var reply = await ReplyAsync(routed.Value, question, cancellationToken).ConfigureAwait(false);
        // The reply is stored on the turn after its question.
        var answerTurn = opened.Value.Turns.Count + 1;
        if (reply is QuickAnswer answer)
        {
            await output.AnswerAsync(answer, answerTurn, cancellationToken).ConfigureAwait(false);
        }
        else if (reply is QuickSuggestions suggestions)
        {
            await output.SuggestionsAsync(suggestions, answerTurn, cancellationToken).ConfigureAwait(false);
        }

        conversations.Append(opened.Value.Id, [new UserQuestionTurn(question, now), QuickReplay.Turn(reply)], now);
        return Result.Success(new AssistantAnswer(opened.Value.Id, AssistantOutcome.Answered, answerTurn));
    }

    private async Task<QuickReply> ReplyAsync(RouteDecision routed, string question, CancellationToken cancellationToken) =>
        routed switch
        {
            RouteToIntent intent => await composer.ComposeAsync(intent.Intent, question, intent.AnsweredAs, cancellationToken).ConfigureAwait(false),
            RouteToHelp help => QuickAnswerComposer.Help(help.Section, help.AnsweredAs),
            RouteUnsure { Suggestions.Count: > 0 } unsure => new QuickSuggestions("I'm not sure what you mean. Did you mean one of these?", unsure.Suggestions),
            _ => new QuickSuggestions("I'm not sure what you mean.", []),
        };
}
