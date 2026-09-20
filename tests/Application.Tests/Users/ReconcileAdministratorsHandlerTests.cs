using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Users;

public sealed class ReconcileAdministratorsHandlerTests
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    private static User AUser(string username)
        => User.Register(Guid.NewGuid(), username, "hash", "Display Name", RegisteredAt);

    private void Candidates(params User[] users) =>
        _users.ListForRoleReconciliationAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(users);

    [Fact]
    public async Task HandleAsync_PromotesAConfiguredUser()
    {
        var ada = AUser("ada");
        Candidates(ada);
        var handler = new ReconcileAdministratorsHandler(_users);

        var result = await handler.HandleAsync(
            new ReconcileAdministrators(["ada"]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ada.Role.Should().Be(UserRole.Admin);
        result.Value.Promoted.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_DemotesAnAdministratorNoLongerConfigured()
    {
        var grace = AUser("grace");
        grace.ChangeRole(UserRole.Admin);
        Candidates(grace);
        var handler = new ReconcileAdministratorsHandler(_users);

        var result = await handler.HandleAsync(
            new ReconcileAdministrators([]), CancellationToken.None);

        // The declarative half: removing a name revokes the role, rather than leaving a former
        // administrator privileged forever because nothing ever took it away.
        grace.Role.Should().Be(UserRole.Member);
        result.Value.Demoted.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_MatchesOnTheNormalisedUsername()
    {
        var ada = AUser("ada");
        Candidates(ada);
        var handler = new ReconcileAdministratorsHandler(_users);

        // Configured as typed, stored case-folded. User.Normalize is the only lookup key this
        // application has, so a configured "Ada" has to reach the row stored as "ADA".
        await handler.HandleAsync(new ReconcileAdministrators(["Ada"]), CancellationToken.None);

        ada.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task HandleAsync_AgainstAnUnchangedList_ChangesNothing()
    {
        var ada = AUser("ada");
        ada.ChangeRole(UserRole.Admin);
        Candidates(ada);
        var handler = new ReconcileAdministratorsHandler(_users);

        var result = await handler.HandleAsync(
            new ReconcileAdministrators(["ada"]), CancellationToken.None);

        // What makes this safe to run at every API start, in both replicas: nothing is dirtied,
        // so the unit of work has nothing to write.
        result.Value.Promoted.Should().Be(0);
        result.Value.Demoted.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_ReportsAConfiguredUsernameThatMatchesNoAccount()
    {
        Candidates();
        var handler = new ReconcileAdministratorsHandler(_users);

        var result = await handler.HandleAsync(
            new ReconcileAdministrators(["nobody"]), CancellationToken.None);

        // Not an error - an administrator may be configured before they register - but far more
        // often a typo, and a typo here fails silently towards nobody having access.
        result.Value.Unknown.Should().ContainSingle().Which.Should().Be("NOBODY");
    }

    [Fact]
    public async Task HandleAsync_LeavesTheSecurityStampAlone()
    {
        var ada = AUser("ada");
        var before = ada.SecurityStamp;
        Candidates(ada);
        var handler = new ReconcileAdministratorsHandler(_users);

        await handler.HandleAsync(new ReconcileAdministrators(["ada"]), CancellationToken.None);

        // See ADR 0020: rotating here would sign every administrator out on every API restart.
        ada.SecurityStamp.Should().Be(before);
    }
}
