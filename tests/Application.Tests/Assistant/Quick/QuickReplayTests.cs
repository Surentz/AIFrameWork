using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class QuickReplayTests
{
    private static readonly QuickAnswer Answer = new(
        QuickAnswerKind.Instant, null, "Your last order shipped on 3 Oct 2026, 14:12 UTC.",
        [new QuickFact("Product", "Widget Pro (SKU-1042)")],
        new QuickTable(["Order", "Status"], [["3f2a1b9c", "Shipped"]]),
        new QuickLink("Open order", "/orders/3f2a1b9c-0000-4000-8000-000000000001"),
        null);

    [Fact]
    public void For_AnAnswer_SpellsOutEveryFigureAndTheLink()
    {
        QuickReplay.For(Answer).Should().Be(
            "Your last order shipped on 3 Oct 2026, 14:12 UTC.\n" +
            "Product: Widget Pro (SKU-1042)\n" +
            "Order: 3f2a1b9c; Status: Shipped\n" +
            "(Linked: Open order, /orders/3f2a1b9c-0000-4000-8000-000000000001)");
    }

    [Fact]
    public void For_Suggestions_ListsThem()
    {
        QuickReplay.For(new QuickSuggestions("I'm not sure what you mean. Did you mean one of these?", ["Show my recent orders", "Is my export ready?"]))
            .Should().Be("I'm not sure what you mean. Did you mean one of these? Suggested: Show my recent orders; Is my export ready?");
    }

    [Fact]
    public void Turn_FixesTheReplayAtWriteTime()
    {
        var turn = QuickReplay.Turn(Answer).Should().BeOfType<QuickAnswerTurn>().Subject;

        turn.Replay.Should().Be(QuickReplay.For(Answer));
        turn.Answer.Should().BeSameAs(Answer);
    }
}
