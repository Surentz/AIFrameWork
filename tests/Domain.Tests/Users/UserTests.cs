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
}
