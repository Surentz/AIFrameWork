using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Users;

public sealed class RegisterUserHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAdministratorDirectory _administrators =
        Substitute.For<IAdministratorDirectory>();

    public RegisterUserHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _hasher.Hash(Arg.Any<string>()).Returns("hashed");
        _users.GetByNormalizedUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);
        _users.TryAddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact]
    public async Task HandleAsync_WithAFreeUsername_AddsTheUser()
    {
        var handler = new RegisterUserHandler(_users, _hasher, _clock, _administrators);

        var result = await handler.HandleAsync(
            new RegisterUser("ada", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _users.Received(1).TryAddAsync(
            Arg.Is<User>(u => u.Username == "ada" && u.DisplayName == "Ada Lovelace" && u.RegisteredAt == Now),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StoresTheHashAndNeverThePassword()
    {
        var handler = new RegisterUserHandler(_users, _hasher, _clock, _administrators);

        await handler.HandleAsync(
            new RegisterUser("ada", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        _hasher.Received(1).Hash("correct horse battery");
        await _users.Received(1).TryAddAsync(
            Arg.Is<User>(u => u.PasswordHash == "hashed"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ReturnsTheSessionForTheNewUser()
    {
        var handler = new RegisterUserHandler(_users, _hasher, _clock, _administrators);

        var result = await handler.HandleAsync(
            new RegisterUser("ada", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        result.Value.Username.Should().Be("ada");
        result.Value.DisplayName.Should().Be("Ada Lovelace");
        result.Value.UserId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithATakenUsername_FailsAsConflict()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>())
            .Returns(User.Register(Guid.NewGuid(), "Ada", "hash", "The First Ada", Now));
        var handler = new RegisterUserHandler(_users, _hasher, _clock, _administrators);

        var result = await handler.HandleAsync(
            new RegisterUser("ada", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Conflict);
        result.Error.Code.Should().Be("user.username_taken");
        await _users.DidNotReceive().TryAddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenTheNameIsTakenBetweenTheCheckAndTheInsert_FailsAsConflict()
    {
        // The race: the pre-check saw the name free, and a concurrent registration took it first.
        _users.TryAddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(false);
        var handler = new RegisterUserHandler(_users, _hasher, _clock, _administrators);

        var result = await handler.HandleAsync(
            new RegisterUser("ada", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Conflict);
        result.Error.Code.Should().Be("user.username_taken");
    }

    [Fact]
    public async Task HandleAsync_ChecksAvailabilityAgainstTheNormalisedUsername()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>())
            .Returns(User.Register(Guid.NewGuid(), "Ada", "hash", "The First Ada", Now));
        var handler = new RegisterUserHandler(_users, _hasher, _clock, _administrators);

        var result = await handler.HandleAsync(
            new RegisterUser("ADA", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse("differing only by case must not make a second account");
    }

    [Fact]
    public async Task HandleAsync_WithAUsernameConfigurationAppoints_RegistersAnAdministrator()
    {
        _administrators.IsAdministrator("Ada").Returns(true);
        var handler = new RegisterUserHandler(_users, _hasher, _clock, _administrators);

        var result = await handler.HandleAsync(
            new RegisterUser("Ada", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        // Without this, a fresh deployment's operator holds no access until someone restarts the
        // API - the reconciler cannot promote an account that did not exist when it ran.
        result.Value.Role.Should().Be(UserRole.Admin);
        await _users.Received(1).TryAddAsync(
            Arg.Is<User>(u => u.Role == UserRole.Admin), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithAnyOtherUsername_RegistersAMember()
    {
        var handler = new RegisterUserHandler(_users, _hasher, _clock, _administrators);

        var result = await handler.HandleAsync(
            new RegisterUser("Ada", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        // The substitute answers false for everything, which is the configured-nobody case: an
        // empty Admin:Usernames is legal and means exactly this.
        result.Value.Role.Should().Be(UserRole.Member);
    }
}
