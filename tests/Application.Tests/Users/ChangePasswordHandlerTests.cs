using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Users;

public sealed class ChangePasswordHandlerTests
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();

    private static User AnAda() =>
        User.Register(Guid.NewGuid(), "ada", "old-hash", "Ada Lovelace", RegisteredAt);

    [Fact]
    public async Task HandleAsync_WithTheRightCurrentPassword_ReplacesTheHash()
    {
        var ada = AnAda();
        _users.GetAsync(ada.Id, Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("old-hash", "old password here").Returns(true);
        _hasher.Hash("new password here").Returns("new-hash");
        var handler = new ChangePasswordHandler(_users, _hasher);

        var result = await handler.HandleAsync(
            new ChangePassword(ada.Id, "old password here", "new password here"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ada.PasswordHash.Should().Be("new-hash");
    }

    [Fact]
    public async Task HandleAsync_OnSuccess_ReturnsTheRotatedStamp()
    {
        var ada = AnAda();
        var before = ada.SecurityStamp;
        _users.GetAsync(ada.Id, Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("old-hash", "old password here").Returns(true);
        _hasher.Hash("new password here").Returns("new-hash");
        var handler = new ChangePasswordHandler(_users, _hasher);

        var result = await handler.HandleAsync(
            new ChangePassword(ada.Id, "old password here", "new password here"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SecurityStamp.Should().Be(ada.SecurityStamp).And.NotBe(before);
        result.Value.UserId.Should().Be(ada.Id);
    }

    [Fact]
    public async Task HandleAsync_WithTheWrongCurrentPassword_LeavesTheHashAlone()
    {
        var ada = AnAda();
        _users.GetAsync(ada.Id, Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("old-hash", "not it").Returns(false);
        var handler = new ChangePasswordHandler(_users, _hasher);

        var result = await handler.HandleAsync(
            new ChangePassword(ada.Id, "not it", "new password here"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        ada.PasswordHash.Should().Be("old-hash");
    }

    [Fact]
    public async Task HandleAsync_WhenTheUserIsGone_FailsAsUnauthorizedRatherThanNotFound()
    {
        _users.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        var handler = new ChangePasswordHandler(_users, _hasher);

        var result = await handler.HandleAsync(
            new ChangePassword(Guid.NewGuid(), "old password here", "new password here"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(
            ErrorKind.Unauthorized,
            "the id came from the caller's own session, so a missing row means that session is stale");
    }
}
