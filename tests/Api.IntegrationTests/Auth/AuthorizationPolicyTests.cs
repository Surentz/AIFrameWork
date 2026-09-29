using System.Reflection;
using System.Security.Claims;
using AiFramework.Api.Auth;
using AiFramework.Domain.Users;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests.Auth;

/// <summary>
/// Every capability policy exists and admits exactly the Admin role today. Run against the real
/// registration in Program.cs; an unregistered name would otherwise throw only at request time,
/// on whichever endpoint used it. ADR 0024.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class AuthorizationPolicyTests(ApiFactory factory)
{
    /// <summary>
    /// Every const string declared anywhere under <see cref="AuthorizationPolicies"/>, found by
    /// reflection rather than read from <see cref="AuthorizationPolicies.All"/> — the point is to
    /// catch a constant someone added without listing it there.
    /// </summary>
    private static string[] Declared() =>
    [
        .. typeof(AuthorizationPolicies).GetNestedTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            // A string const initialised with a literal is never null.
            .Select(field => (string)field.GetRawConstantValue()!),
    ];

    public static TheoryData<string> DeclaredPolicies() => [.. Declared()];

    private static ClaimsPrincipal Signed(UserRole role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
         new Claim(ClaimTypes.Role, role.ToString())],
        authenticationType: "Test"));

    [Theory]
    [MemberData(nameof(DeclaredPolicies))]
    public async Task Policy_IsRegistered(string policy)
    {
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        (await provider.GetPolicyAsync(policy)).Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(DeclaredPolicies))]
    public async Task Policy_RefusesAMember(string policy)
    {
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(Signed(UserRole.Member), policy);

        result.Succeeded.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(DeclaredPolicies))]
    public async Task Policy_AdmitsAnAdministrator(string policy)
    {
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(Signed(UserRole.Admin), policy);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void All_ListsEveryDeclaredPolicy()
    {
        AuthorizationPolicies.All.Should().BeEquivalentTo(Declared());
    }
}
