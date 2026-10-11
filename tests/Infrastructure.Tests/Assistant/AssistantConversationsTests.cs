using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Assistant;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Assistant;

[Collection(nameof(PostgresCollection))]
public sealed class AssistantConversationsTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    // Thinking text and a signature with characters a JSON round-trip could re-escape: the stored
    // blocks must come back byte-for-byte, or the provider rejects the history (preserved thinking).
    private const string Blocks = """"[{"type":"thinking","thinking":"","signature":"EqQB/+x=="},{"type":"text","text":"Ünïcode \u2028 \"quoted\""}]"""";

    private static readonly AssistantTurn[] FirstQuestion =
    [
        new UserQuestionTurn("Why?", Now),
        new ModelTurn(Blocks),
        new ToolResultsTurn([new AssistantToolResult("toolu_1", """{"failed":3}""", false), new AssistantToolResult("toolu_2", "Bad input.", true)]),
        new ModelTurn("""[{"type":"text","text":"Three failed."}]"""),
    ];

    private async Task<Guid> StartWithAsync(IReadOnlyList<AssistantTurn> turns)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var store = new AssistantConversations(context);
        store.Start(id, Guid.NewGuid(), Now);
        store.Append(id, turns, Now);
        await context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task FindAsync_AfterAppend_ReturnsEveryTurnUnchangedAndInOrder()
    {
        var id = await StartWithAsync(FirstQuestion);

        await using var context = fixture.CreateContext();
        var found = await new AssistantConversations(context).FindAsync(id, CancellationToken.None);

        found!.QuestionCount.Should().Be(1);
        found.Turns.Should().HaveCount(4);
        found.Turns[0].Should().Be(FirstQuestion[0]);
        ((ModelTurn)found.Turns[1]).Blocks.Should().Be(Blocks);
        ((ToolResultsTurn)found.Turns[2]).Results.Should().Equal(((ToolResultsTurn)FirstQuestion[2]).Results);
        found.Turns[3].Should().Be(FirstQuestion[3]);
    }

    [Fact]
    public async Task Append_SecondQuestion_ContinuesTheSequenceAndCountsIt()
    {
        var id = await StartWithAsync(FirstQuestion);
        await using (var context = fixture.CreateContext())
        {
            var store = new AssistantConversations(context);
            await store.FindAsync(id, CancellationToken.None);
            store.Append(id, [new UserQuestionTurn("And now?", Now.AddMinutes(1)), new ModelTurn("[]")], Now.AddMinutes(1));
            await context.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var found = await new AssistantConversations(read).FindAsync(id, CancellationToken.None);
        found!.QuestionCount.Should().Be(2);
        found.Turns.Should().HaveCount(6);
        found.Turns[4].Should().Be(new UserQuestionTurn("And now?", Now.AddMinutes(1)));
    }

    [Fact]
    public async Task FindAsync_UnknownId_ReturnsNull()
    {
        await using var context = fixture.CreateContext();

        (await new AssistantConversations(context).FindAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Append_TwoConcurrentQuestions_TheSecondSaveIsAConcurrencyConflict()
    {
        var id = await StartWithAsync(FirstQuestion);
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var a = new AssistantConversations(first);
        var b = new AssistantConversations(second);
        await a.FindAsync(id, CancellationToken.None);
        await b.FindAsync(id, CancellationToken.None);
        a.Append(id, [new UserQuestionTurn("A", Now)], Now);
        b.Append(id, [new UserQuestionTurn("B", Now)], Now);

        await first.SaveChangesAsync();
        var act = () => second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public void Append_WithoutFindOrStart_Throws()
    {
        using var context = fixture.CreateContext();

        var act = () => new AssistantConversations(context).Append(Guid.NewGuid(), [], Now);

        act.Should().Throw<InvalidOperationException>();
    }
}
