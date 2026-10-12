using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Infrastructure.Assistant;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Assistant;

public sealed class AssistantDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_ShowsAQuickAnswerAsAnAssistantMessageCarryingTheCard()
    {
        var conversation = Guid.NewGuid();
        var answer = new QuickAnswer(QuickAnswerKind.Instant, null, "Shipped.", [], null, new QuickLink("Open order", "/orders"), null);
        var rows = new[]
        {
            AssistantTurnCodec.Encode(conversation, 0, new UserQuestionTurn("Has it shipped?", Now), Now),
            AssistantTurnCodec.Encode(conversation, 1, new QuickAnswerTurn(answer, "Shipped."), Now),
            AssistantTurnCodec.Encode(conversation, 2, new UserQuestionTurn("why", Now), Now),
            AssistantTurnCodec.Encode(conversation, 3, new SuggestionsTurn(new QuickSuggestions("Not sure.", ["Is my export ready?"]), "Not sure."), Now),
        };

        var messages = AssistantDisplay.Build(rows);

        messages.Should().HaveCount(4);
        messages[1].Role.Should().Be(AssistantDisplayMessage.AssistantRole);
        messages[1].Text.Should().Be("Shipped.");
        messages[1].Answer!.Link!.To.Should().Be("/orders");
        messages[3].Suggestions!.Options.Should().Equal("Is my export ready?");
    }

    [Fact]
    public void Build_SkipsAQuickRowWithAnUnreadablePayload_AndStillBuildsTheRest()
    {
        var conversation = Guid.NewGuid();
        var broken = Row(conversation, 1, AssistantTurnCodec.Quick, "{ not json");
        var brokenSuggestions = Row(conversation, 2, AssistantTurnCodec.Suggested, "null");
        var rows = new[]
        {
            AssistantTurnCodec.Encode(conversation, 0, new UserQuestionTurn("Has it shipped?", Now), Now),
            broken,
            brokenSuggestions,
            AssistantTurnCodec.Encode(conversation, 3, new QuickAnswerTurn(new QuickAnswer(QuickAnswerKind.Instant, null, "Fine.", [], null, null, null), "Fine."), Now),
        };

        var messages = AssistantDisplay.Build(rows);

        messages.Select(m => m.Text).Should().Equal("Has it shipped?", "Fine.");
    }

    private static AssistantTurnRow Row(Guid conversation, int seq, string kind, string payload) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = conversation,
        Seq = seq,
        Kind = kind,
        Payload = payload,
        CreatedAt = Now,
    };
}
