using AiFramework.Domain.Users;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// The throttle that keeps a write on the application's hottest path affordable. See ADR 0021.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class LastSeenTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(1);

    private readonly PostgresFixture _fixture = fixture;

    private async Task<User> AnAdaAsync()
    {
        await using var context = _fixture.CreateContext();
        var ada = User.Register(
            Guid.NewGuid(), $"ada{Guid.NewGuid():N}"[..32], "hash", "Ada Lovelace", Noon);
        context.Users.Add(ada);
        await context.SaveChangesAsync(CancellationToken.None);

        return ada;
    }

    private async Task<DateTimeOffset?> ReadLastSeenAsync(Guid id)
    {
        await using var context = _fixture.CreateContext();

        return await context.Users
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => u.LastSeenAt)
            .SingleAsync();
    }

    private async Task TouchAsync(Guid id, DateTimeOffset now)
    {
        await using var context = _fixture.CreateContext();
        await new UserRepository(context)
            .TouchLastSeenAsync(id, now, now - Throttle, CancellationToken.None);
    }

    [Fact]
    public async Task TouchLastSeenAsync_OnAUserNeverSeen_StampsIt()
    {
        var ada = await AnAdaAsync();

        await TouchAsync(ada.Id, Noon);

        (await ReadLastSeenAsync(ada.Id)).Should().Be(Noon);
    }

    [Fact]
    public async Task TouchLastSeenAsync_InsideTheThrottleWindow_WritesNothing()
    {
        var ada = await AnAdaAsync();
        await TouchAsync(ada.Id, Noon);

        // Thirty seconds later: within the one-minute window, so this request must not write.
        // The whole point — this runs on every authenticated request, and without the throttle a
        // busy user would cost one UPDATE per request rather than one per minute.
        await TouchAsync(ada.Id, Noon.AddSeconds(30));

        (await ReadLastSeenAsync(ada.Id)).Should().Be(Noon);
    }

    [Fact]
    public async Task TouchLastSeenAsync_OnceTheWindowHasLapsed_StampsItAgain()
    {
        var ada = await AnAdaAsync();
        await TouchAsync(ada.Id, Noon);

        var later = Noon.AddSeconds(90);
        await TouchAsync(ada.Id, later);

        (await ReadLastSeenAsync(ada.Id)).Should().Be(later);
    }

    [Fact]
    public Task TouchLastSeenAsync_ForAUserThatDoesNotExist_IsHarmless()
    {
        // The session-validation path stamps before anything has confirmed the row still exists,
        // so a deleted account must be a no-op rather than an exception on every request.
        var act = () => TouchAsync(Guid.NewGuid(), Noon);

        return act.Should().NotThrowAsync();
    }
}
