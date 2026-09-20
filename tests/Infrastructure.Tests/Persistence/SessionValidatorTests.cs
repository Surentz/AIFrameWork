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
    public async Task ValidateAsync_WithTheStoredStamp_ReturnsTheAuthority()
    {
        var ada = await AnAdaAsync();
        await using var context = _fixture.CreateContext();
        var validator = new SessionValidator(context);

        var authority = await validator.ValidateAsync(
            ada.Id, ada.SecurityStamp, CancellationToken.None);

        authority.Should().NotBeNull();
        authority.Role.Should().Be(UserRole.Member);
    }

    [Fact]
    public async Task ValidateAsync_ForAnAdministrator_ReportsTheRole()
    {
        var ada = await AnAdaAsync();

        await using (var writing = _fixture.CreateContext())
        {
            var tracked = await new UserRepository(writing).GetAsync(ada.Id, CancellationToken.None);
            tracked!.ChangeRole(UserRole.Admin);
            await writing.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = _fixture.CreateContext();
        var validator = new SessionValidator(context);

        var authority = await validator.ValidateAsync(
            ada.Id, ada.SecurityStamp, CancellationToken.None);

        // The stamp is untouched by the promotion - the session stays valid - and the role rides
        // back on the same read. That pairing is the whole of ADR 0020: authority is read per
        // request rather than minted into the cookie, so it costs no extra round trip and cannot
        // go stale.
        authority.Should().NotBeNull();
        authority.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task ValidateAsync_AfterARotation_IsRejected()
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

        var authority = await validator.ValidateAsync(ada.Id, stale, CancellationToken.None);

        authority.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_ForAUserThatDoesNotExist_IsRejected()
    {
        await using var context = _fixture.CreateContext();
        var validator = new SessionValidator(context);

        var authority = await validator.ValidateAsync(
            Guid.NewGuid(), "anything", CancellationToken.None);

        authority.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_WithAnEmptyStamp_IsRejected()
    {
        var ada = await AnAdaAsync();
        await using var context = _fixture.CreateContext();
        var validator = new SessionValidator(context);

        // A cookie minted before ADR 0011 carries no stamp claim at all, which arrives here as
        // empty. Failing closed is what retires those sessions rather than trusting them.
        var authority = await validator.ValidateAsync(ada.Id, string.Empty, CancellationToken.None);

        authority.Should().BeNull();
    }
}
