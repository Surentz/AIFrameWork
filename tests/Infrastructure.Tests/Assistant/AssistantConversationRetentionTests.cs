using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Assistant;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Assistant;

/// <summary>
/// The 24-hour sweep. Questions are free text an administrator typed, so this bounds how long the
/// application keeps them — a requirement, not housekeeping. Dated far from other tests' rows
/// because the sweep has no owner filter (see OrderExportRetentionTests).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AssistantConversationRetentionTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2300, 2, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<Guid> SeedAsync(DateTimeOffset lastActive)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var store = new AssistantConversations(context);
        store.Start(id, Guid.NewGuid(), lastActive);
        store.Append(id, [new UserQuestionTurn("q", lastActive), new ModelTurn("[]")], lastActive);
        await context.SaveChangesAsync();
        return id;
    }

    private async Task PruneAsync()
    {
        await using var context = fixture.CreateContext();
        await new AssistantConversationRetention(context, new TestClock(Now)).PruneAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PruneAsync_IdleLongerThanTheRetention_DeletesTheConversationAndItsTurns()
    {
        var old = await SeedAsync(Now - AssistantConversationRetention.Retention - TimeSpan.FromMinutes(1));

        await PruneAsync();

        await using var context = fixture.CreateContext();
        (await context.AssistantConversations.AnyAsync(c => c.Id == old)).Should().BeFalse();
        (await context.AssistantTurns.AnyAsync(t => t.ConversationId == old)).Should().BeFalse();
    }

    [Fact]
    public async Task PruneAsync_ActiveInsideTheRetention_KeepsIt()
    {
        var recent = await SeedAsync(Now - AssistantConversationRetention.Retention + TimeSpan.FromMinutes(1));

        await PruneAsync();

        await using var context = fixture.CreateContext();
        (await context.AssistantConversations.AnyAsync(c => c.Id == recent)).Should().BeTrue();
    }
}
