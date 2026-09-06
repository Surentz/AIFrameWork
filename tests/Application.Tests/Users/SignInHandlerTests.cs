using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Users;

public sealed class SignInHandlerTests
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();

    private static User AnAda() =>
        User.Register(Guid.NewGuid(), "Ada", "stored-hash", "Ada Lovelace", RegisteredAt);

    [Fact]
    public async Task HandleAsync_WithTheRightPassword_ReturnsTheSession()
    {
        var ada = AnAda();
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "correct horse").Returns(true);
        var handler = new SignInHandler(_users, _hasher);

        var result = await handler.HandleAsync(new SignIn("Ada", "correct horse"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new SessionView(ada.Id, "Ada", "Ada Lovelace"));
    }

    [Fact]
    public async Task HandleAsync_LooksTheUserUpByTheNormalisedUsername()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(AnAda());
        _hasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var handler = new SignInHandler(_users, _hasher);

        var result = await handler.HandleAsync(new SignIn("  aDa  ", "correct horse"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("case and surrounding space must not make a second account");
    }

    [Fact]
    public async Task HandleAsync_WithTheWrongPassword_FailsAsUnauthorized()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(AnAda());
        _hasher.Verify("stored-hash", "wrong").Returns(false);
        var handler = new SignInHandler(_users, _hasher);

        var result = await handler.HandleAsync(new SignIn("Ada", "wrong"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        result.Error.Code.Should().Be("auth.failed");
    }

    [Fact]
    public async Task HandleAsync_WithAnUnknownUsername_FailsWithTheSameErrorAsAWrongPassword()
    {
        _users.GetByNormalizedUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);
        var handler = new SignInHandler(_users, _hasher);

        var unknown = await handler.HandleAsync(new SignIn("nobody", "guess"), CancellationToken.None);

        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(AnAda());
        _hasher.Verify("stored-hash", "guess").Returns(false);
        var wrongPassword = await handler.HandleAsync(new SignIn("Ada", "guess"), CancellationToken.None);

        // Byte-identical, deliberately: any difference between these two turns the endpoint into
        // a way to find out which usernames exist.
        unknown.Error.Should().Be(wrongPassword.Error);
    }

    [Fact]
    public async Task HandleAsync_WithAnUnknownUsername_StillHashesThePassword()
    {
        _users.GetByNormalizedUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);
        var handler = new SignInHandler(_users, _hasher);

        await handler.HandleAsync(new SignIn("nobody", "guess"), CancellationToken.None);

        // Without this the miss returns immediately and response time answers "does this account
        // exist?" for free, however identical the error body is.
        _hasher.Received(1).Hash("guess");
    }
}
