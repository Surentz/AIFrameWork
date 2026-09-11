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

    public SignInHandlerTests()
    {
        _clock.UtcNow.Returns(Now);

        // An unconfigured Task<bool> substitute returns false, which is "another request beat you
        // to the row" — so every test that does not care about the race would otherwise exercise
        // the retry path. The uncontended write succeeding is the default here; the one test that
        // cares overrides it.
        _users.TryRecordFailedSignInAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
    }

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
        result.Value.Should().Be(new SessionView(ada.Id, "Ada", "Ada Lovelace", ada.SecurityStamp));
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
        await _users.DidNotReceiveWithAnyArgs().TryRecordFailedSignInAsync(
            Guid.Empty, default, default, default, default);
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

        await _users.Received(1).TryRecordFailedSignInAsync(
            ada.Id, expectedAttempts: 0, attempts: 1, null, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The concurrency case the conditional write exists for. The handler reads the counter, then
    /// spends a PBKDF2 verify deciding what to write, so a competing attempt on the same account
    /// routinely lands in between. Without the re-read, N simultaneous guesses would all read k
    /// and all write k+1 — advancing the counter by one instead of N, and pushing the lockout out
    /// to roughly 5xN guesses.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenAConcurrentAttemptWinsTheRace_RereadsAndRecordsAgain()
    {
        var ada = AnAda();
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "wrong").Returns(false);
        _users.TryRecordFailedSignInAsync(
                ada.Id,
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(false, true);
        var handler = new SignInHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(new SignIn("Ada", "wrong"), CancellationToken.None);

        // Two reads: the handler's own load, plus exactly one more to pick up whatever the winner
        // wrote. One retry, not a loop — a second lost race means the counter is moving anyway.
        await _users.Received(2).GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>());
        await _users.Received(2).TryRecordFailedSignInAsync(
            ada.Id,
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
        result.Error.Should().Be(
            new Error(ErrorKind.Unauthorized, "auth.failed", "That username and password do not match."),
            "a lost race is invisible to the caller, or it becomes an oracle of its own");
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

        await _users.Received(1).TryRecordFailedSignInAsync(
            ada.Id,
            User.MaxFailedSignInAttempts - 1,
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
        await _users.Received(1).ClearSignInFailuresAsync(ada.Id, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The path a legitimate locked-out user actually takes back in, and the only test that pins
    /// the clock being consulted on the read side. Without it, swapping IsLockedOut(now) for a
    /// bare null check passes every other test in this class while locking such an account for
    /// good — in a codebase with no password reset, that is permanent.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AfterTheLockoutExpires_LetsTheRightPasswordIn()
    {
        var ada = ALockedOutAda();
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "correct horse").Returns(true);
        _clock.UtcNow.Returns(Now + User.LockoutDuration + TimeSpan.FromMinutes(1));
        var handler = new SignInHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(
            new SignIn("Ada", "correct horse"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a lockout that has expired must not keep anyone out");
        await _users.Received(1).ClearSignInFailuresAsync(ada.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithTheRightPasswordAndACleanRecord_WritesNothing()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(AnAda());
        _hasher.Verify("stored-hash", "correct horse").Returns(true);
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "correct horse"), CancellationToken.None);

        // An ordinary sign-in is the common case and must not cost a pointless UPDATE.
        await _users.DidNotReceiveWithAnyArgs().ClearSignInFailuresAsync(Guid.Empty, default);
        await _users.DidNotReceiveWithAnyArgs().TryRecordFailedSignInAsync(
            Guid.Empty, default, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_OnSuccess_ReturnsTheUsersCurrentSecurityStamp()
    {
        var ada = AnAda();
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "the right password").Returns(true);
        var handler = new SignInHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(
            new SignIn("Ada", "the right password"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SecurityStamp.Should().Be(ada.SecurityStamp);
    }
}
