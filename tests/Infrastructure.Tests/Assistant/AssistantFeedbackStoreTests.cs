using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Assistant;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Assistant;

[Collection(nameof(PostgresCollection))]
public sealed class AssistantFeedbackStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    // question 0, tool_use 1, tool results 2, final model 3: the answer is turn 3.
    private static readonly AssistantTurn[] ToolRound =
    [
        new UserQuestionTurn("Did jobs fail?", Now),
        new ModelTurn("""[{"type":"tool_use","id":"toolu_1","name":"job_health","input":{"window_hours":24}}]"""),
        new ToolResultsTurn([new AssistantToolResult("toolu_1", """{"failed":3}""", false)]),
        new ModelTurn("""[{"type":"text","text":"Three failed."}]"""),
    ];

    private async Task<Guid> SeedAsync(Guid owner)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var store = new AssistantConversations(context);
        store.Start(id, owner, "Did jobs fail?", Now);
        store.Append(id, ToolRound, Now);
        await context.SaveChangesAsync();
        return id;
    }

    private async Task<RateResult> RateAsync(Guid id, Guid owner, int turn, AnswerRating? rating)
    {
        await using var context = fixture.CreateContext();
        var result = await new AssistantRatings(context).RateAsync(id, owner, turn, rating, Now, CancellationToken.None);
        await context.SaveChangesAsync();
        return result;
    }

    private async Task<AnswerRating?> StoredAsync(Guid id, int turn)
    {
        await using var context = fixture.CreateContext();
        return (await context.AssistantRatings.AsNoTracking().SingleOrDefaultAsync(r => r.ConversationId == id && r.Turn == turn))?.Rating;
    }

    [Fact]
    public async Task RateAsync_TheAnswerTurn_StoresTheRating()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner);

        (await RateAsync(id, owner, 3, AnswerRating.Down)).Should().Be(RateResult.Rated);

        (await StoredAsync(id, 3)).Should().Be(AnswerRating.Down);
    }

    [Fact]
    public async Task RateAsync_Again_ReplacesTheRating_AndNullClearsIt()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner);
        await RateAsync(id, owner, 3, AnswerRating.Down);

        await RateAsync(id, owner, 3, AnswerRating.Up);
        (await StoredAsync(id, 3)).Should().Be(AnswerRating.Up);

        await RateAsync(id, owner, 3, null);
        (await StoredAsync(id, 3)).Should().BeNull();
    }

    [Theory]
    [InlineData(0)] // the question
    [InlineData(1)] // a tool-round model turn
    [InlineData(2)] // tool results
    public async Task RateAsync_ATurnThatIsNotAnAnswer_IsRefused_AndStoresNothing(int turn)
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner);

        (await RateAsync(id, owner, turn, AnswerRating.Up)).Should().Be(RateResult.NotAnAnswer);

        (await StoredAsync(id, turn)).Should().BeNull();
    }

    [Fact]
    public async Task RateAsync_ATurnPastTheEnd_IsNotFound()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner);

        (await RateAsync(id, owner, 9, AnswerRating.Up)).Should().Be(RateResult.NotFound);
    }

    [Fact]
    public async Task RateAsync_AnotherOwnersConversation_IsNotFound_AndStoresNothing()
    {
        var id = await SeedAsync(Guid.NewGuid());

        (await RateAsync(id, Guid.NewGuid(), 3, AnswerRating.Up)).Should().Be(RateResult.NotFound);

        (await StoredAsync(id, 3)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingTheConversation_RemovesItsRatingsAndOutcomes()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner);
        await RateAsync(id, owner, 3, AnswerRating.Down);
        await using (var context = fixture.CreateContext())
        {
            new AssistantOutcomeLog(context).Record(new AnsweredQuestion(
                id, 0, 3, Now, AnswerPath.Ai, null, null, null, false, ToolScope.Operator));
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            (await new AssistantHistoryStore(context).DeleteAsync(id, owner, CancellationToken.None)).Should().BeTrue();
            await context.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        (await check.AssistantRatings.AnyAsync(r => r.ConversationId == id)).Should().BeFalse();
        (await check.AssistantOutcomes.AnyAsync(o => o.ConversationId == id)).Should().BeFalse();
    }
}
