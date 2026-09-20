using AiFramework.Infrastructure.Security;
using AiFramework.Domain.Users;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Security;

public sealed class AdminOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(params string[] usernames)
    {
        var options = new AdminOptions();
        foreach (var username in usernames)
        {
            options.Usernames.Add(username);
        }

        return new AdminOptionsValidator().Validate(name: null, options);
    }

    [Fact]
    public void Validate_WithNoAdministrators_Succeeds()
    {
        // "No administrators" is the correct configuration for every environment without an
        // operator. Failing here would make the monitoring feature impossible to deploy.
        Validate().Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithAUsername_Succeeds() =>
        Validate("ada").Succeeded.Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithABlankEntry_Fails(string username)
    {
        // A blank entry can never match an account, so it is a typo - and a typo here fails
        // silently in the direction of nobody having access. Loud at startup beats invisible.
        var result = Validate(username);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("blank");
    }

    [Fact]
    public void Validate_WithAnOverLongEntry_Fails()
    {
        var result = Validate(new string('a', User.MaxUsernameLength + 1));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("cannot name an account");
    }
}
