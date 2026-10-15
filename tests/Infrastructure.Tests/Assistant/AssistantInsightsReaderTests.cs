using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Assistant;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Assistant;

/// <summary>
/// The database is shared by every test in the collection, so each test dates its rows in a year
/// of its own and reads only that window.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AssistantInsightsReaderTests(PostgresFixture fixture)
{
    private async Task<Guid> SeedAsync(DateTimeOffset at, string question, AnswerPath path, string? target, AnswerRating? rating = null)
    {
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var conversations = new AssistantConversations(context);
        conversations.Start(id, owner, question, at);
        conversations.Append(id, [new UserQuestionTurn(question, at), new ModelTurn("""[{"type":"text","text":"An answer."}]""")], at);
        new AssistantOutcomeLog(context).Record(new AnsweredQuestion(id, 0, 1, at, path, target, 0.9, 0.1, false, ToolScope.Member));
        await context.SaveChangesAsync();
        if (rating is { } value)
        {
            await new AssistantRatings(context).RateAsync(id, owner, 1, value, at, CancellationToken.None);
            await context.SaveChangesAsync();
        }

        return id;
    }

    [Fact]
    public async Task DaysAsync_CountsEachPathPerUtcDay()
    {
        var day = new DateTimeOffset(2031, 3, 4, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(day, "a", AnswerPath.Instant, "order_status");
        await SeedAsync(day.AddHours(1), "b", AnswerPath.Instant, "order_status");
        await SeedAsync(day.AddHours(2), "c", AnswerPath.Ai, null);
        await SeedAsync(day.AddDays(1), "d", AnswerPath.Suggested, null);

        await using var context = fixture.CreateContext();
        var days = await new AssistantInsightsReader(context).DaysAsync(day.AddHours(-10), CancellationToken.None);

        days.Where(d => d.Day.Year == 2031).Should().Equal(
            new InsightsDay(new DateOnly(2031, 3, 4), 2, 0, 0, 1),
            new InsightsDay(new DateOnly(2031, 3, 5), 0, 0, 1, 0));
    }

    [Fact]
    public async Task MissesAsync_ReturnsSuggestedAiAndThumbsDown_NewestFirst_WithTheQuestionText()
    {
        var at = new DateTimeOffset(2032, 5, 6, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(at, "answered fine", AnswerPath.Instant, "order_status");
        await SeedAsync(at.AddMinutes(1), "wanted a poem", AnswerPath.Suggested, null);
        await SeedAsync(at.AddMinutes(2), "asked the AI", AnswerPath.Ai, null);
        await SeedAsync(at.AddMinutes(3), "wrong instant answer", AnswerPath.Instant, "order_status", AnswerRating.Down);

        await using var context = fixture.CreateContext();
        var misses = await new AssistantInsightsReader(context).MissesAsync(at.AddMinutes(-1), 50, CancellationToken.None);

        misses.Where(m => m.At.Year == 2032).Select(m => m.Question).Should().Equal("wrong instant answer", "asked the AI", "wanted a poem");
        misses.Single(m => string.Equals(m.Question, "wrong instant answer", StringComparison.Ordinal)).Rating.Should().Be(AnswerRating.Down);
    }

    [Fact]
    public async Task MissesAsync_ARatedDownRow_SurvivesTheLimit_AheadOfNewerAiMisses()
    {
        var at = new DateTimeOffset(2035, 11, 12, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(at, "old but wrong", AnswerPath.Instant, "order_status", AnswerRating.Down);
        await SeedAsync(at.AddMinutes(1), "ai one", AnswerPath.Ai, null);
        await SeedAsync(at.AddMinutes(2), "ai two", AnswerPath.Ai, null);
        await SeedAsync(at.AddMinutes(3), "ai three", AnswerPath.Ai, null);

        await using var context = fixture.CreateContext();
        var misses = await new AssistantInsightsReader(context).MissesAsync(at.AddMinutes(-1), 2, CancellationToken.None);

        misses.Select(m => m.Question).Should().Equal("old but wrong", "ai three");
    }

    [Fact]
    public async Task MissesAsync_ADeletedConversation_IsGone()
    {
        var at = new DateTimeOffset(2033, 7, 8, 10, 0, 0, TimeSpan.Zero);
        var id = await SeedAsync(at, "soon deleted", AnswerPath.Ai, null);
        await using (var context = fixture.CreateContext())
        {
            context.AssistantConversations.Remove(context.AssistantConversations.Single(c => c.Id == id));
            await context.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        (await new AssistantInsightsReader(read).MissesAsync(at.AddMinutes(-1), 50, CancellationToken.None))
            .Should().NotContain(m => m.Question == "soon deleted");
    }

    [Fact]
    public async Task TargetsAsync_CountsQuestionsAndRatingsPerTarget()
    {
        var at = new DateTimeOffset(2034, 9, 10, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(at, "x", AnswerPath.Instant, "export_status", AnswerRating.Up);
        await SeedAsync(at.AddMinutes(1), "y", AnswerPath.Instant, "export_status", AnswerRating.Down);
        await SeedAsync(at.AddMinutes(2), "z", AnswerPath.Instant, "export_status");

        await using var context = fixture.CreateContext();
        var targets = await new AssistantInsightsReader(context).TargetsAsync(at.AddMinutes(-1), 20, CancellationToken.None);

        targets.Should().Contain(new InsightsTarget(AnswerPath.Instant, "export_status", 3, 1, 1));
    }
}
