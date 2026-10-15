using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Persistence;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>Tracked only: the unit of work commits the outcome with the question's turns.</summary>
internal sealed class AssistantOutcomeLog(AiFrameworkDbContext context) : IAssistantOutcomeLog
{
    public void Record(AnsweredQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        context.AssistantOutcomes.Add(new AssistantOutcomeRow
        {
            Id = Guid.NewGuid(),
            ConversationId = question.ConversationId,
            QuestionTurn = question.QuestionTurn,
            AnswerTurn = question.AnswerTurn,
            At = question.At,
            Path = question.Path,
            TargetId = question.TargetId,
            BestScore = question.BestScore,
            Margin = question.Margin,
            Corrected = question.Corrected,
            Scope = question.Scope,
        });
    }
}
