using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Assistant;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Assistant;

[Collection(nameof(PostgresCollection))]
public sealed class AssistantHistoryStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    // What a real tool round looks like on disk: thinking + tool_use, the tool's result, then text.
    private static readonly AssistantTurn[] ToolRound =
    [
        new UserQuestionTurn("Did jobs fail?", Now),
        new ModelTurn("""[{"type":"thinking","thinking":"","signature":"sig"},{"type":"tool_use","id":"toolu_1","name":"job_health","input":{"window_hours":24}}]"""),
        new ToolResultsTurn([new AssistantToolResult("toolu_1", """{"failed":3}""", false)]),
        new ModelTurn("""[{"type":"text","text":"Three failed."}]"""),
    ];

    private async Task<Guid> SeedAsync(Guid owner, string title, DateTimeOffset at, IReadOnlyList<AssistantTurn> turns)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var store = new AssistantConversations(context);
        store.Start(id, owner, title, at);
        store.Append(id, turns, at);
        await context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyTheOwnersNewestFirst()
    {
        var owner = Guid.NewGuid();
        var older = await SeedAsync(owner, "Older", Now.AddHours(-2), ToolRound);
        var newer = await SeedAsync(owner, "Newer", Now, ToolRound);
        await SeedAsync(Guid.NewGuid(), "Someone else's", Now.AddHours(1), ToolRound);

        await using var context = fixture.CreateContext();
        var list = await new AssistantHistoryStore(context).ListAsync(owner, CancellationToken.None);

        list.Select(c => c.Id).Should().Equal(newer, older);
        list[0].Title.Should().Be("Newer");
    }

    [Fact]
    public async Task ReadAsync_FoldsAToolRoundIntoOneAnswerWithItsTools()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner, "Did jobs fail?", Now, ToolRound);

        await using var context = fixture.CreateContext();
        var view = await new AssistantHistoryStore(context).ReadAsync(id, owner, CancellationToken.None);

        view!.Messages.Should().BeEquivalentTo(
            [new AssistantDisplayMessage("user", "Did jobs fail?", []), new AssistantDisplayMessage("assistant", "Three failed.", ["job_health"], Turn: 3)],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task ReadAsync_ReportsTheOwnersRatingOnTheAnswer()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner, "Did jobs fail?", Now, ToolRound);
        await using (var context = fixture.CreateContext())
        {
            await new AssistantRatings(context).RateAsync(id, owner, 3, AnswerRating.Up, Now, CancellationToken.None);
            await context.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var view = await new AssistantHistoryStore(read).ReadAsync(id, owner, CancellationToken.None);

        view!.Messages[1].Turn.Should().Be(3);
        view.Messages[1].Rating.Should().Be(AnswerRating.Up);
    }

    [Fact]
    public async Task ReadAsync_AnotherOwner_IsNull()
    {
        var id = await SeedAsync(Guid.NewGuid(), "Mine", Now, ToolRound);

        await using var context = fixture.CreateContext();
        (await new AssistantHistoryStore(context).ReadAsync(id, Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RenameAsync_ChangesTheTitleAndNoStoredTurn()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner, "Before", Now, ToolRound);
        List<string> before;
        await using (var read = fixture.CreateContext())
        {
            before = await read.AssistantTurns.Where(t => t.ConversationId == id).OrderBy(t => t.Seq).Select(t => t.Payload).ToListAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            (await new AssistantHistoryStore(context).RenameAsync(id, owner, "After", CancellationToken.None)).Should().BeTrue();
            await context.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        (await check.AssistantConversations.SingleAsync(c => c.Id == id)).Title.Should().Be("After");
        (await check.AssistantTurns.Where(t => t.ConversationId == id).OrderBy(t => t.Seq).Select(t => t.Payload).ToListAsync())
            .Should().Equal(before);
    }

    [Fact]
    public async Task RenameAsync_ThenAQuestionWithAStaleRow_Conflicts()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner, "Before", Now, ToolRound);
        await using var question = fixture.CreateContext();
        var store = new AssistantConversations(question);
        await store.FindAsync(id, CancellationToken.None);

        await using (var rename = fixture.CreateContext())
        {
            await new AssistantHistoryStore(rename).RenameAsync(id, owner, "Renamed", CancellationToken.None);
            await rename.SaveChangesAsync();
        }

        store.Append(id, [new UserQuestionTurn("Again?", Now), new ModelTurn("[]")], Now);
        var commit = () => question.SaveChangesAsync();

        await commit.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheConversationAndItsTurns()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner, "Bye", Now, ToolRound);

        await using (var context = fixture.CreateContext())
        {
            (await new AssistantHistoryStore(context).DeleteAsync(id, owner, CancellationToken.None)).Should().BeTrue();
            await context.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        (await check.AssistantConversations.AnyAsync(c => c.Id == id)).Should().BeFalse();
        (await check.AssistantTurns.AnyAsync(t => t.ConversationId == id)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_AnotherOwner_IsFalseAndKeepsIt()
    {
        var id = await SeedAsync(Guid.NewGuid(), "Mine", Now, ToolRound);

        await using (var context = fixture.CreateContext())
        {
            (await new AssistantHistoryStore(context).DeleteAsync(id, Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
            await context.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        (await check.AssistantConversations.AnyAsync(c => c.Id == id)).Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_ToolTheQuestionWasNotOffered_ShowsNoToolChip()
    {
        var owner = Guid.NewGuid();
        AssistantTurn[] refused =
        [
            new UserQuestionTurn("Did jobs fail?", Now),
            new ModelTurn("""[{"type":"tool_use","id":"toolu_9","name":"job_health","input":{"window_hours":24}}]"""),
            new ToolResultsTurn([new AssistantToolResult("toolu_9", "The tool 'job_health' is not available here.", true)]),
            new ModelTurn("""[{"type":"text","text":"I can't check that."}]"""),
        ];
        var id = await SeedAsync(owner, "Did jobs fail?", Now, refused);

        await using var context = fixture.CreateContext();
        var view = await new AssistantHistoryStore(context).ReadAsync(id, owner, CancellationToken.None);

        view!.Messages[1].Tools.Should().BeEmpty();
        view.Messages[1].Text.Should().Be("I can't check that.");
    }
}
