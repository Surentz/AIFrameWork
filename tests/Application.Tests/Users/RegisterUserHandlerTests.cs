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

    public RegisterUserHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _hasher.Hash(Arg.Any<string>()).Returns("hashed");
        _users.GetByNormalizedUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);
    }

    [Fact]
    public async Task HandleAsync_WithAFreeUsername_AddsTheUser()
    {
        var handler = new RegisterUserHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(
            new RegisterUser("ada", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _users.Received(1).AddAsync(
            Arg.Is<User>(u => u.Username == "ada" && u.DisplayName == "Ada Lovelace" && u.RegisteredAt == Now),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StoresTheHashAndNeverThePassword()
    {
        var handler = new RegisterUserHandler(_users, _hasher, _clock);

        await handler.HandleAsync(
            new RegisterUser("ada", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        _hasher.Received(1).Hash("correct horse battery");
        await _users.Received(1).AddAsync(
            Arg.Is<User>(u => u.PasswordHash == "hashed"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ReturnsTheSessionForTheNewUser()
    {
        var handler = new RegisterUserHandler(_users, _hasher, _clock);

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
        var handler = new RegisterUserHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(
            new RegisterUser("ada", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Conflict);
        result.Error.Code.Should().Be("user.username_taken");
        await _users.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ChecksAvailabilityAgainstTheNormalisedUsername()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>())
            .Returns(User.Register(Guid.NewGuid(), "Ada", "hash", "The First Ada", Now));
        var handler = new RegisterUserHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(
            new RegisterUser("ADA", "correct horse battery", "Ada Lovelace"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse("differing only by case must not make a second account");
    }
}
