using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Users;

public sealed class SignOutEverywhereHandlerTests
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    private static User AnAda() =>
        User.Register(Guid.NewGuid(), "ada", "stored-hash", "Ada Lovelace", RegisteredAt);

    [Fact]
    public async Task HandleAsync_RotatesTheStamp()
    {
        var ada = AnAda();
        var before = ada.SecurityStamp;
        _users.GetAsync(ada.Id, Arg.Any<CancellationToken>()).Returns(ada);
        var handler = new SignOutEverywhereHandler(_users);

        var result = await handler.HandleAsync(new SignOutEverywhere(ada.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ada.SecurityStamp.Should().NotBe(before);
    }

    [Fact]
    public async Task HandleAsync_ForAMissingUser_FailsUnauthorized()
    {
        _users.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        var handler = new SignOutEverywhereHandler(_users);

        var result = await handler.HandleAsync(
            new SignOutEverywhere(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }
}
