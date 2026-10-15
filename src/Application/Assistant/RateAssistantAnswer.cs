using AiFramework.Application.Abstractions;
using FluentValidation;

namespace AiFramework.Application.Assistant;

/// <summary>The owner rates one answer, or clears its rating with a null.</summary>
public sealed record RateAssistantAnswer(Guid ConversationId, int Turn, AnswerRating? Rating) : ICommand<bool>;

public sealed class RateAssistantAnswerValidator : AbstractValidator<RateAssistantAnswer>
{
    public RateAssistantAnswerValidator()
    {
        RuleFor(c => c.Turn).GreaterThanOrEqualTo(0);
        RuleFor(c => c.Rating).IsInEnum().When(c => c.Rating is not null);
    }
}

public sealed class RateAssistantAnswerHandler(IAssistantRatings ratings, ICurrentUser currentUser, IClock clock)
    : ICommandHandler<RateAssistantAnswer, bool>
{
    public async Task<Result<bool>> HandleAsync(RateAssistantAnswer command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } ownerId)
        {
            return Result.Failure<bool>(AssistantHistoryErrors.NoCaller);
        }

        var rated = await ratings.RateAsync(command.ConversationId, ownerId, command.Turn, command.Rating, clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return rated switch
        {
            RateResult.Rated => Result.Success(true),
            RateResult.NotAnAnswer => Result.Failure<bool>(new Error(
                ErrorKind.Validation, "assistant.not_an_answer", "Only an answer can be rated.")),
            _ => Result.Failure<bool>(AssistantHistoryErrors.NotFound),
        };
    }
}
