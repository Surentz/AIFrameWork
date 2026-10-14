using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant;

/// <summary>
/// How many questions a conversation holds. The whole conversation is replayed to Claude whenever
/// the AI is asked, so the total is bounded too, not only the AI's share. ADR 0036.
/// </summary>
public static class AssistantLimits
{
    public const int MaxQuestionsPerConversation = 50;

    public const int MaxAiQuestionsPerConversation = 20;

    /// <summary>Questions the model answered: a question whose next turn is a model turn.</summary>
    public static int AiQuestions(IReadOnlyList<AssistantTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var count = 0;
        for (var i = 0; i + 1 < turns.Count; i++)
        {
            if (turns[i] is UserQuestionTurn && turns[i + 1] is ModelTurn)
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>Opens the caller's conversation for one more question, or starts one. Shared by AskAssistant and AskQuick.</summary>
public static class AssistantConversationOpener
{
    public static async Task<Result<AssistantConversation>> OpenAsync(
        IAssistantConversations conversations,
        Guid? conversationId,
        Guid ownerId,
        string question,
        DateTimeOffset now,
        bool forAi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        if (conversationId is not { } id)
        {
            var started = new AssistantConversation(Guid.NewGuid(), ownerId, 0, []);
            conversations.Start(started.Id, ownerId, AssistantTitle.From(question), now);
            return Result.Success(started);
        }

        var existing = await conversations.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.OwnerId != ownerId)
        {
            return Result.Failure<AssistantConversation>(new Error(
                ErrorKind.NotFound, "assistant.conversation_not_found", "That conversation does not exist or has expired. Start a new one."));
        }

        if (existing.QuestionCount >= AssistantLimits.MaxQuestionsPerConversation)
        {
            return Result.Failure<AssistantConversation>(new Error(
                ErrorKind.Validation, "assistant.conversation_full", $"This conversation has reached {AssistantLimits.MaxQuestionsPerConversation} questions. Start a new one."));
        }

        if (forAi && AssistantLimits.AiQuestions(existing.Turns) >= AssistantLimits.MaxAiQuestionsPerConversation)
        {
            return Result.Failure<AssistantConversation>(new Error(
                ErrorKind.Validation, "assistant.conversation_full", $"This conversation has reached {AssistantLimits.MaxAiQuestionsPerConversation} questions for the AI. Start a new one to ask it more."));
        }

        return Result.Success(existing);
    }
}
