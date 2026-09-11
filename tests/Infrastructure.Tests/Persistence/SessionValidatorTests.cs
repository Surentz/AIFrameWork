using AiFramework.Domain.Users;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Persistence;

[Collection(nameof(PostgresCollection))]
public sealed class SessionValidatorTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private async Task<User> AnAdaAsync()
    {
        await using var context = _fixture.CreateContext();
        var ada = User.Register(
            Guid.NewGuid(), $"ada{Guid.NewGuid():N}"[..32], "hash", "Ada Lovelace", DateTimeOffset.UtcNow);
        context.Users.Add(ada);
        await context.SaveChangesAsync(CancellationToken.None);
        return ada;
    }

    [Fact]
    public async Task IsStampCurrentAsync_WithTheStoredStamp_IsTrue()
    {
        var ada = await AnAdaAsync();
        await using var context = _fixture.CreateContext();
        var validator = new SessionValidator(context);

        var current = await validator.IsStampCurrentAsync(
            ada.Id, ada.SecurityStamp, CancellationToken.None);

        current.Should().BeTrue();
    }

    [Fact]
    public async Task IsStampCurrentAsync_AfterARotation_IsFalse()
    {
        var ada = await AnAdaAsync();
        var stale = ada.SecurityStamp;

        await using (var writing = _fixture.CreateContext())
        {
            var tracked = await new UserRepository(writing).GetAsync(ada.Id, CancellationToken.None);
            tracked!.RotateSecurityStamp();
            await writing.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = _fixture.CreateContext();
        var validator = new SessionValidator(context);

        var current = await validator.IsStampCurrentAsync(ada.Id, stale, CancellationToken.None);

        current.Should().BeFalse();
    }

    [Fact]
    public async Task IsStampCurrentAsync_ForAUserThatDoesNotExist_IsFalse()
    {
        await using var context = _fixture.CreateContext();
        var validator = new SessionValidator(context);

        var current = await validator.IsStampCurrentAsync(
            Guid.NewGuid(), "anything", CancellationToken.None);

        current.Should().BeFalse();
    }

    [Fact]
    public async Task IsStampCurrentAsync_WithAnEmptyStamp_IsFalse()
    {
        var ada = await AnAdaAsync();
        await using var context = _fixture.CreateContext();
        var validator = new SessionValidator(context);

        // A cookie minted before ADR 0011 carries no stamp claim at all, which arrives here as
        // empty. Failing closed is what retires those sessions rather than trusting them.
        var current = await validator.IsStampCurrentAsync(ada.Id, string.Empty, CancellationToken.None);

        current.Should().BeFalse();
    }
}
