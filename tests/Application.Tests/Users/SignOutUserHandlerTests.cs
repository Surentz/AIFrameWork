using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Users;

public sealed class SignOutUserHandlerTests
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAdminAudit _audit = Substitute.For<IAdminAudit>();

    private static User AUser(string username) =>
        User.Register(Guid.NewGuid(), username, "hash", "Display Name", RegisteredAt);

    public SignOutUserHandlerTests() => _currentUser.Id.Returns(Guid.NewGuid());

    [Fact]
    public async Task HandleAsync_RotatesTheTargetsSecurityStamp()
    {
        var ada = AUser("ada");
        var before = ada.SecurityStamp;
        _users.GetAsync(ada.Id, Arg.Any<CancellationToken>()).Returns(ada);

        var result = await new SignOutUserHandler(_users, _currentUser, _audit)
            .HandleAsync(new SignOutUser(ada.Id), CancellationToken.None);

        // The deliberate act that a role change deliberately is not. Rotating invalidates every
        // cookie already issued for this account (ADR 0011).
        result.IsSuccess.Should().BeTrue();
        ada.SecurityStamp.Should().NotBe(before);
        await _audit.Received(1).RecordAsync(
            AdminActionKind.SignedOutEverywhere,
            Arg.Is<AdministeredUser>(u => u.Id == ada.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AimedAtYourself_IsRefused()
    {
        var me = AUser("me");
        _currentUser.Id.Returns(me.Id);
        _users.GetAsync(me.Id, Arg.Any<CancellationToken>()).Returns(me);
        var before = me.SecurityStamp;

        var result = await new SignOutUserHandler(_users, _currentUser, _audit)
            .HandleAsync(new SignOutUser(me.Id), CancellationToken.None);

        // Refused here, not forbidden outright: sign-out-everywhere on your own account is a
        // real thing a user may do, through the endpoint that exists for it and warns them.
        // Doing it by misclick from a table of every account is not.
        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("user.cannot_sign_out_self_here");
        me.SecurityStamp.Should().Be(before);
    }

    [Fact]
    public async Task HandleAsync_WithAnUnknownAccount_IsNotFound()
    {
        _users.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await new SignOutUserHandler(_users, _currentUser, _audit)
            .HandleAsync(new SignOutUser(Guid.NewGuid()), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }
}
