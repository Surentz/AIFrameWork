using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Users;

/// <summary>
/// The two rails, and the rule that a role change never touches the target's sessions.
/// See ADR 0022.
/// </summary>
public sealed class ChangeUserRoleHandlerTests
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAdminAudit _audit = Substitute.For<IAdminAudit>();

    private static User AUser(string username) =>
        User.Register(Guid.NewGuid(), username, "hash", "Display Name", RegisteredAt);

    private ChangeUserRoleHandler Handler() =>
        new(_users, _currentUser, _audit);

    private void Existing(User user) =>
        _users.GetAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

    public ChangeUserRoleHandlerTests() =>
        _currentUser.Id.Returns(Guid.NewGuid());

    [Fact]
    public async Task HandleAsync_PromotesAMember()
    {
        var ada = AUser("ada");
        Existing(ada);

        var result = await Handler().HandleAsync(
            new ChangeUserRole(ada.Id, UserRole.Admin), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ada.Role.Should().Be(UserRole.Admin);
        await _audit.Received(1).RecordAsync(
            AdminActionKind.Promoted, Arg.Any<AdministeredUser>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_DemotingYourself_IsRefused()
    {
        var me = AUser("me");
        me.ChangeRole(UserRole.Admin);
        _currentUser.Id.Returns(me.Id);
        Existing(me);

        var result = await Handler().HandleAsync(
            new ChangeUserRole(me.Id, UserRole.Member), CancellationToken.None);

        // The first rail. A server-side rule, not a disabled button: the screen hides the action
        // on your own row, and this is what makes that an explanation rather than the mechanism.
        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("user.cannot_change_own_role");
        me.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task HandleAsync_DemotingYourself_NeverReachesTheRepository()
    {
        var me = AUser("me");
        _currentUser.Id.Returns(me.Id);
        Existing(me);

        await Handler().HandleAsync(
            new ChangeUserRole(me.Id, UserRole.Member), CancellationToken.None);

        // Checked before the read, so the cheapest refusal costs nothing at all.
        await _users.DidNotReceive().TryDemoteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenTheRepositoryRefusesTheLastAdministrator_ReportsAConflict()
    {
        var grace = AUser("grace");
        grace.ChangeRole(UserRole.Admin);
        Existing(grace);

        // Zero rows affected IS the refusal - the condition lives in the UPDATE's WHERE clause so
        // two concurrent demotions cannot both pass it. See IUserRepository.TryDemoteAsync.
        _users.TryDemoteAsync(grace.Id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Handler().HandleAsync(
            new ChangeUserRole(grace.Id, UserRole.Member), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("user.last_administrator");
    }

    [Fact]
    public async Task HandleAsync_WhenTheLastAdministratorIsRefused_WritesNoAuditRow()
    {
        var grace = AUser("grace");
        grace.ChangeRole(UserRole.Admin);
        Existing(grace);
        _users.TryDemoteAsync(grace.Id, Arg.Any<CancellationToken>()).Returns(false);

        await Handler().HandleAsync(
            new ChangeUserRole(grace.Id, UserRole.Member), CancellationToken.None);

        // An audit that records changes which did not happen is worse than no audit.
        await _audit.DidNotReceive().RecordAsync(
            Arg.Any<AdminActionKind>(), Arg.Any<AdministeredUser>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_DemotingThroughTheRail_RecordsTheDemotion()
    {
        var grace = AUser("grace");
        grace.ChangeRole(UserRole.Admin);
        Existing(grace);
        _users.TryDemoteAsync(grace.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await Handler().HandleAsync(
            new ChangeUserRole(grace.Id, UserRole.Member), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync(
            AdminActionKind.Demoted,
            Arg.Is<AdministeredUser>(u => u.Id == grace.Id && u.Username == "grace"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LeavesTheTargetsSecurityStampAlone()
    {
        var ada = AUser("ada");
        ada.ChangeRole(UserRole.Admin);
        var before = ada.SecurityStamp;
        Existing(ada);
        _users.TryDemoteAsync(ada.Id, Arg.Any<CancellationToken>()).Returns(true);

        await Handler().HandleAsync(
            new ChangeUserRole(ada.Id, UserRole.Member), CancellationToken.None);

        // ADR 0020 and ADR 0022 both turn on this. The role is read from the database on every
        // request, so a demotion lands on the target's next one with no window - while rotating
        // would sign them out of a session they are still entitled to hold. Ending sessions is
        // SignOutUser, a separate and deliberate act.
        ada.SecurityStamp.Should().Be(before);
    }

    [Fact]
    public async Task HandleAsync_WhenTheRoleIsAlreadyWhatWasAsked_ChangesNothingAndAuditsNothing()
    {
        var ada = AUser("ada");
        ada.ChangeRole(UserRole.Admin);
        Existing(ada);

        var result = await Handler().HandleAsync(
            new ChangeUserRole(ada.Id, UserRole.Admin), CancellationToken.None);

        // Success, not a conflict: the caller asked for a state the system is already in. A
        // retried click must not pile up audit rows claiming a role changed when it did not.
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse();
        await _audit.DidNotReceive().RecordAsync(
            Arg.Any<AdminActionKind>(), Arg.Any<AdministeredUser>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithAnUnknownAccount_IsNotFound()
    {
        _users.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await Handler().HandleAsync(
            new ChangeUserRole(Guid.NewGuid(), UserRole.Admin), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }
}
