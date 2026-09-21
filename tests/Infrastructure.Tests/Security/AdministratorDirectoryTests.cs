using AiFramework.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Security;

/// <summary>
/// The registration-time half of the configured administrator list. It must answer exactly what
/// <c>ReconcileAdministrators</c> would answer at startup for the same list — both paths only
/// ever promote (ADR 0022), so agreeing means agreeing about WHO, and a mismatch would leave an
/// account that registers as a member and becomes an administrator at the next restart, or the
/// reverse.
/// </summary>
public sealed class AdministratorDirectoryTests
{
    private static AdministratorDirectory Directory(params string[] usernames)
    {
        var options = new AdminOptions();
        foreach (var username in usernames)
        {
            options.Usernames.Add(username);
        }

        return new AdministratorDirectory(Options.Create(options));
    }

    [Fact]
    public void IsAdministrator_WithAConfiguredName_IsTrue()
    {
        Directory("ada").IsAdministrator("ada").Should().BeTrue();
    }

    [Theory]
    [InlineData("ADA")]
    [InlineData("Ada")]
    [InlineData("  ada  ")]
    public void IsAdministrator_MatchesThroughNormalize(string registered)
    {
        // The same comparison the reconciler makes, and the same one the unique index enforces.
        // Matching by ordinal equality instead would promote "ada" and never "Ada", silently.
        Directory("Ada").IsAdministrator(registered).Should().BeTrue();
    }

    [Fact]
    public void IsAdministrator_WithAnUnlistedName_IsFalse()
    {
        Directory("ada").IsAdministrator("grace").Should().BeFalse();
    }

    [Fact]
    public void IsAdministrator_WithNobodyConfigured_IsFalse()
    {
        // An empty list is legal and means nobody — the correct configuration for an environment
        // with no operator, which AdminOptionsValidator deliberately passes.
        Directory().IsAdministrator("ada").Should().BeFalse();
    }

    [Fact]
    public void IsAdministrator_WithABlankEntry_DoesNotThrow()
    {
        // ValidateOnStart rejects a blank entry in any host that binds the section, but a host
        // that never binds "Admin" has no validator running — and User.Normalize throws on null.
        var directory = Directory("   ");

        directory.Invoking(d => d.IsAdministrator("ada")).Should().NotThrow();
        directory.IsAdministrator("ada").Should().BeFalse();
    }
}
