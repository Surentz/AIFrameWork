using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Infrastructure.Assistant;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Assistant;

public sealed class AssistantTurnCodecTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QuickAnswerTurn_RoundTrips_WithItsReplayUnchanged()
    {
        var answer = new QuickAnswer(
            QuickAnswerKind.Help, "Exporting your orders", "Open Exports.", [new QuickFact("A", "B")],
            new QuickTable(["C"], [["D"]]), new QuickLink("Go to Exports", "/orders/exports"), "how do I export");
        var turn = new QuickAnswerTurn(answer, "replay, byte for byte");

        var row = AssistantTurnCodec.Encode(Guid.NewGuid(), 1, turn, Now);
        var decoded = AssistantTurnCodec.Decode(row).Should().BeOfType<QuickAnswerTurn>().Subject;

        row.Kind.Should().Be(AssistantTurnCodec.Quick);
        decoded.Replay.Should().Be("replay, byte for byte");
        decoded.Answer.Text.Should().Be("Open Exports.");
        decoded.Answer.Kind.Should().Be(QuickAnswerKind.Help);
        decoded.Answer.Facts.Should().Equal(new QuickFact("A", "B"));
        decoded.Answer.Table!.Rows[0].Should().Equal("D");
        decoded.Answer.Link.Should().Be(new QuickLink("Go to Exports", "/orders/exports"));
        decoded.Answer.AnsweredAs.Should().Be("how do I export");
    }

    [Fact]
    public void SuggestionsTurn_RoundTrips()
    {
        var turn = new SuggestionsTurn(new QuickSuggestions("Not sure.", ["Is my export ready?"]), "Not sure. Suggested: Is my export ready?");

        var decoded = AssistantTurnCodec.Decode(AssistantTurnCodec.Encode(Guid.NewGuid(), 1, turn, Now))
            .Should().BeOfType<SuggestionsTurn>().Subject;

        decoded.Suggestions.Options.Should().Equal("Is my export ready?");
        decoded.Replay.Should().Be("Not sure. Suggested: Is my export ready?");
    }

    [Theory]
    [InlineData(AssistantTurnCodec.Quick, "{}")]
    [InlineData(AssistantTurnCodec.Quick, "{\"replay\":\"x\"}")]
    [InlineData(AssistantTurnCodec.Suggested, "{}")]
    [InlineData(AssistantTurnCodec.Suggested, "{\"suggestions\":{\"text\":\"t\",\"options\":[]}}")]
    public void Decode_AQuickRowMissingAField_ThrowsInvalidOperation(string kind, string payload)
    {
        var row = new AssistantTurnRow
        {
            Id = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            Seq = 1,
            Kind = kind,
            Payload = payload,
            CreatedAt = Now,
        };

        var act = () => AssistantTurnCodec.Decode(row);

        act.Should().Throw<InvalidOperationException>();
    }
}
