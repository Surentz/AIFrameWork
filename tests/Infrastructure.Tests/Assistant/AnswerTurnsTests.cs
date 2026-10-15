using AiFramework.Infrastructure.Assistant;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Assistant;

public sealed class AnswerTurnsTests
{
    private const string Q = AssistantTurnCodec.Question;
    private const string M = AssistantTurnCodec.Model;
    private const string R = AssistantTurnCodec.ToolResults;

    [Fact]
    public void Of_AnAiAnswerAfterAToolRound_IsItsFinalModelTurn()
    {
        AnswerTurns.Of([(0, Q), (1, M), (2, R), (3, M)]).Should().Equal(3);
    }

    [Fact]
    public void Of_QuickRepliesAreAnswersOnTheirOwnTurn()
    {
        AnswerTurns.Of([(0, Q), (1, AssistantTurnCodec.Quick), (2, Q), (3, AssistantTurnCodec.Suggested)]).Should().Equal(1, 3);
    }

    [Fact]
    public void Of_AModelTurnFollowedByToolResultsIsNotAnAnswer()
    {
        AnswerTurns.Of([(0, Q), (1, M), (2, R), (3, M), (4, Q), (5, M)]).Should().Equal(3, 5);
    }
}
