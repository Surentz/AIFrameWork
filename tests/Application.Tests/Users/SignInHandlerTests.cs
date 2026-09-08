using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Users;

public sealed class SignInHandlerTests
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public SignInHandlerTests() => _clock.UtcNow.Returns(Now);

    private static User AnAda() =>
        User.Register(Guid.NewGuid(), "Ada", "stored-hash", "Ada Lovelace", RegisteredAt);

    /// <summary>An Ada who has just used up her last attempt, so she is locked at <see cref="Now"/>.</summary>
    private static User ALockedOutAda()
    {
        var ada = AnAda();
        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            ada.RegisterFailedSignIn(Now);
        }

        return ada;
    }

    [Fact]
    public async Task HandleAsync_WithTheRightPassword_ReturnsTheSession()
    {
        var ada = AnAda();
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "correct horse").Returns(true);
        var handler = new SignInHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(new SignIn("Ada", "correct horse"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new SessionView(ada.Id, "Ada", "Ada Lovelace"));
    }

    [Fact]
    public async Task HandleAsync_LooksTheUserUpByTheNormalisedUsername()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(AnAda());
        _hasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var handler = new SignInHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(new SignIn("  aDa  ", "correct horse"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("case and surrounding space must not make a second account");
    }

    [Fact]
    public async Task HandleAsync_WithTheWrongPassword_FailsAsUnauthorized()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(AnAda());
        _hasher.Verify("stored-hash", "wrong").Returns(false);
        var handler = new SignInHandler(_users, _hasher, _clock);

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
        var handler = new SignInHandler(_users, _hasher, _clock);

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
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("nobody", "guess"), CancellationToken.None);

        // Without this the miss returns immediately and response time answers "does this account
        // exist?" for free, however identical the error body is.
        _hasher.Received(1).Hash("guess");
    }

    [Fact]
    public async Task HandleAsync_WhenLockedOut_FailsWithoutCheckingThePassword()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ALockedOutAda());
        var handler = new SignInHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(new SignIn("Ada", "correct horse"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        // The assertion that proves the lockout short-circuited, rather than merely coinciding
        // with a wrong password: a correct password must not get in either.
        _hasher.DidNotReceive().Verify(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleAsync_WhenLockedOut_StillHashesThePassword()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ALockedOutAda());
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "guess"), CancellationToken.None);

        // Without this a locked account answers measurably faster than a wrong password, and the
        // timing difference is the account-existence oracle the unknown-username hash closes.
        _hasher.Received(1).Hash("guess");
    }

    [Fact]
    public async Task HandleAsync_WhenLockedOut_DoesNotExtendTheLockout()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ALockedOutAda());
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "guess"), CancellationToken.None);

        // A fixed window: attempts made during a lockout must not touch the counter at all,
        // or an attacker could hold the account locked forever by continuing to guess.
        await _users.DidNotReceiveWithAnyArgs().RecordSignInOutcomeAsync(
            Guid.Empty, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_WhenLockedOut_FailsWithTheSameErrorAsAWrongPassword()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ALockedOutAda());
        var handler = new SignInHandler(_users, _hasher, _clock);
        var lockedOut = await handler.HandleAsync(new SignIn("Ada", "guess"), CancellationToken.None);

        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(AnAda());
        _hasher.Verify("stored-hash", "guess").Returns(false);
        var wrongPassword = await handler.HandleAsync(new SignIn("Ada", "guess"), CancellationToken.None);

        lockedOut.Error.Should().Be(wrongPassword.Error);
    }

    [Fact]
    public async Task HandleAsync_WithTheWrongPassword_RecordsTheFailure()
    {
        var ada = AnAda();
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "wrong").Returns(false);
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "wrong"), CancellationToken.None);

        await _users.Received(1).RecordSignInOutcomeAsync(
            ada.Id, 1, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_OnTheThresholdFailure_RecordsALockout()
    {
        var ada = AnAda();
        for (var i = 0; i < User.MaxFailedSignInAttempts - 1; i++)
        {
            ada.RegisterFailedSignIn(Now);
        }

        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "wrong").Returns(false);
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "wrong"), CancellationToken.None);

        await _users.Received(1).RecordSignInOutcomeAsync(
            ada.Id,
            User.MaxFailedSignInAttempts,
            Now + User.LockoutDuration,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithTheRightPasswordAfterFailures_ClearsTheCounter()
    {
        var ada = AnAda();
        ada.RegisterFailedSignIn(Now);
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "correct horse").Returns(true);
        var handler = new SignInHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(new SignIn("Ada", "correct horse"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _users.Received(1).RecordSignInOutcomeAsync(ada.Id, 0, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithTheRightPasswordAndACleanRecord_WritesNothing()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(AnAda());
        _hasher.Verify("stored-hash", "correct horse").Returns(true);
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "correct horse"), CancellationToken.None);

        // An ordinary sign-in is the common case and must not cost a pointless UPDATE.
        await _users.DidNotReceiveWithAnyArgs().RecordSignInOutcomeAsync(
            Guid.Empty, default, default, default);
    }
}
