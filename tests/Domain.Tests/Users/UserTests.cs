using AiFramework.Domain.Users;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Users;

public sealed class UserTests
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_WithValidDetails_SetsTheProperties()
    {
        var id = Guid.NewGuid();

        var user = User.Register(id, "ada", "hash", "Ada Lovelace", RegisteredAt);

        user.Id.Should().Be(id);
        user.Username.Should().Be("ada");
        user.PasswordHash.Should().Be("hash");
        user.DisplayName.Should().Be("Ada Lovelace");
        user.RegisteredAt.Should().Be(RegisteredAt);
    }

    [Fact]
    public void Register_NormalisesTheUsername()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", RegisteredAt);

        user.Username.Should().Be("Ada", "the typed form is what gets shown back");
        user.UsernameNormalized.Should().Be("ADA", "the case-folded form is what the unique index is on");
    }

    [Fact]
    public void Register_TrimsTheUsername()
    {
        var user = User.Register(Guid.NewGuid(), "  ada  ", "hash", "Ada Lovelace", RegisteredAt);

        user.Username.Should().Be("ada");
        user.UsernameNormalized.Should().Be("ADA");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_WithBlankUsername_Throws(string username)
    {
        var act = () => User.Register(Guid.NewGuid(), username, "hash", "Ada Lovelace", RegisteredAt);

        act.Should().Throw<DomainException>().WithMessage("*username*");
    }

    [Fact]
    public void Register_WithAnOverLongUsername_Throws()
    {
        var username = new string('a', User.MaxUsernameLength + 1);

        var act = () => User.Register(Guid.NewGuid(), username, "hash", "Ada Lovelace", RegisteredAt);

        act.Should().Throw<DomainException>().WithMessage("*username*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_WithBlankPasswordHash_Throws(string passwordHash)
    {
        var act = () => User.Register(Guid.NewGuid(), "ada", passwordHash, "Ada Lovelace", RegisteredAt);

        act.Should().Throw<DomainException>().WithMessage("*password*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_WithBlankDisplayName_Throws(string displayName)
    {
        var act = () => User.Register(Guid.NewGuid(), "ada", "hash", displayName, RegisteredAt);

        act.Should().Throw<DomainException>().WithMessage("*display name*");
    }

    [Fact]
    public void Register_WithAnOverLongDisplayName_Throws()
    {
        var displayName = new string('a', User.MaxDisplayNameLength + 1);

        var act = () => User.Register(Guid.NewGuid(), "ada", "hash", displayName, RegisteredAt);

        act.Should().Throw<DomainException>().WithMessage("*display name*");
    }

    [Fact]
    public void Register_RaisesNoDomainEvent()
    {
        var user = User.Register(Guid.NewGuid(), "ada", "hash", "Ada Lovelace", RegisteredAt);

        user.DomainEvents.Should().BeEmpty(
            "nothing subscribes to a registration yet, and a registered event with no handlers " +
            "would only write outbox rows nobody reads");
    }

    [Fact]
    public void ChangePassword_ReplacesTheHash()
    {
        var user = User.Register(Guid.NewGuid(), "ada", "old-hash", "Ada Lovelace", RegisteredAt);

        user.ChangePassword("new-hash");

        user.PasswordHash.Should().Be("new-hash");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangePassword_WithABlankHash_Throws(string passwordHash)
    {
        var user = User.Register(Guid.NewGuid(), "ada", "old-hash", "Ada Lovelace", RegisteredAt);

        var act = () => user.ChangePassword(passwordHash);

        act.Should().Throw<DomainException>().WithMessage("*password*");
        user.PasswordHash.Should().Be("old-hash");
    }

    [Theory]
    [InlineData("ada", "ADA")]
    [InlineData("Ada", "ADA")]
    [InlineData("  AdA  ", "ADA")]
    public void Normalize_FoldsCaseAndTrims(string username, string expected) =>
        User.Normalize(username).Should().Be(expected);

    private static readonly DateTimeOffset SignInAt = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_StartsWithACleanSignInRecord()
    {
        var user = User.Register(
            Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);

        user.FailedSignInAttempts.Should().Be(0);
        user.LockedOutUntil.Should().BeNull();
        user.IsLockedOut(SignInAt).Should().BeFalse();
    }

    [Fact]
    public void RegisterFailedSignIn_BelowTheThreshold_CountsButDoesNotLock()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);

        for (var i = 0; i < User.MaxFailedSignInAttempts - 1; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        user.FailedSignInAttempts.Should().Be(User.MaxFailedSignInAttempts - 1);
        user.LockedOutUntil.Should().BeNull();
        user.IsLockedOut(SignInAt).Should().BeFalse();
    }

    [Fact]
    public void RegisterFailedSignIn_OnTheThresholdFailure_LocksForTheLockoutDuration()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);

        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        user.LockedOutUntil.Should().Be(SignInAt + User.LockoutDuration);
    }

    [Fact]
    public void IsLockedOut_IsTrueBeforeTheExpiryAndFalseAtIt()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);
        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        var expiry = SignInAt + User.LockoutDuration;

        user.IsLockedOut(expiry.AddTicks(-1)).Should().BeTrue();
        user.IsLockedOut(expiry).Should().BeFalse("the window is closed at the instant it expires");
        user.IsLockedOut(expiry.AddMinutes(1)).Should().BeFalse();
    }

    /// <summary>
    /// The other half of the fixed window, guarded inside the type that owns the fields rather
    /// than only in SignInHandler: an attempt made while the lockout is live must not count and
    /// must not push the expiry forward, or an attacker who knows one username holds that account
    /// locked indefinitely at one guess per window.
    /// </summary>
    [Fact]
    public void RegisterFailedSignIn_DuringAnActiveLockout_ChangesNothing()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);
        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        user.RegisterFailedSignIn(SignInAt + TimeSpan.FromMinutes(1));

        user.FailedSignInAttempts.Should().Be(User.MaxFailedSignInAttempts);
        user.LockedOutUntil.Should().Be(
            SignInAt + User.LockoutDuration, "the window is fixed, not sliding");
    }

    [Fact]
    public void RegisterSuccessfulSignIn_ClearsTheCounterAndTheLockout()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);
        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        user.RegisterSuccessfulSignIn();

        user.FailedSignInAttempts.Should().Be(0);
        user.LockedOutUntil.Should().BeNull();
    }

    /// <summary>
    /// The test that guards the fixed-window decision. Without the reset, the count would still
    /// stand at the threshold when the window lapsed, so the next single failure would re-lock —
    /// and every failure after it would too, which is a sliding window arriving through the back
    /// door and lets an attacker hold an account locked indefinitely at one guess per window.
    /// </summary>
    [Fact]
    public void RegisterFailedSignIn_AfterAnExpiredLockout_StartsCountingFromOne()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);
        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        var afterExpiry = SignInAt + User.LockoutDuration + TimeSpan.FromMinutes(1);
        user.RegisterFailedSignIn(afterExpiry);

        user.FailedSignInAttempts.Should().Be(1, "serving out a lockout earns a fresh set of attempts");
        user.LockedOutUntil.Should().BeNull();
        user.IsLockedOut(afterExpiry).Should().BeFalse();
    }

    [Fact]
    public void Register_GivesTheUserASecurityStamp()
    {
        var ada = User.Register(Guid.NewGuid(), "ada", "hash", "Ada Lovelace", RegisteredAt);

        ada.SecurityStamp.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Register_GivesEachUserADifferentSecurityStamp()
    {
        var ada = User.Register(Guid.NewGuid(), "ada", "hash", "Ada Lovelace", RegisteredAt);
        var grace = User.Register(Guid.NewGuid(), "grace", "hash", "Grace Hopper", RegisteredAt);

        ada.SecurityStamp.Should().NotBe(grace.SecurityStamp);
    }

    [Fact]
    public void ChangePassword_RotatesTheSecurityStamp()
    {
        var ada = User.Register(Guid.NewGuid(), "ada", "hash", "Ada Lovelace", RegisteredAt);
        var before = ada.SecurityStamp;

        ada.ChangePassword("new-hash");

        ada.SecurityStamp.Should().NotBe(before);
    }

    [Fact]
    public void RotateSecurityStamp_ChangesTheStamp()
    {
        var ada = User.Register(Guid.NewGuid(), "ada", "hash", "Ada Lovelace", RegisteredAt);
        var before = ada.SecurityStamp;

        ada.RotateSecurityStamp();

        ada.SecurityStamp.Should().NotBe(before);
    }

    [Fact]
    public void RegisterFailedSignIn_OnTheLockingFailure_RotatesTheSecurityStamp()
    {
        var ada = User.Register(Guid.NewGuid(), "ada", "hash", "Ada Lovelace", RegisteredAt);
        var before = ada.SecurityStamp;

        for (var attempt = 0; attempt < User.MaxFailedSignInAttempts; attempt++)
        {
            ada.RegisterFailedSignIn(RegisteredAt);
        }

        ada.IsLockedOut(RegisteredAt).Should().BeTrue();
        ada.SecurityStamp.Should().NotBe(before);
    }

    [Fact]
    public void RegisterFailedSignIn_BelowTheThreshold_LeavesTheStampAlone()
    {
        var ada = User.Register(Guid.NewGuid(), "ada", "hash", "Ada Lovelace", RegisteredAt);
        var before = ada.SecurityStamp;

        ada.RegisterFailedSignIn(RegisteredAt);

        // A single wrong guess must not sign the real user out. If it did, anyone who knew a
        // username could evict that user from their session at will, without the password.
        ada.SecurityStamp.Should().Be(before);
    }
}
