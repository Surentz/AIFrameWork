using System.Security.Claims;
using AiFramework.Api.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace AiFramework.Api.IntegrationTests.Auth;

/// <summary>
/// A plain unit test, deliberately outside ApiFactoryCollection: CurrentUser needs an
/// IHttpContextAccessor, not a host and not a database.
/// </summary>
public sealed class CurrentUserTests
{
    [Fact]
    public void Id_WhenThereIsNoHttpContext_IsNull()
    {
        // Background work - the outbox pumps - resolves scopes with no request. This is the
        // case that makes the property nullable rather than throwing.
        var currentUser = new CurrentUser(new HttpContextAccessor());

        currentUser.Id.Should().BeNull();
    }

    [Fact]
    public void Id_WhenSignedOut_IsNull()
    {
        var currentUser = new CurrentUser(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        currentUser.Id.Should().BeNull();
    }

    [Fact]
    public void Id_WhenTheClaimIsPresent_IsTheUserId()
    {
        var userId = Guid.NewGuid();

        var currentUser = new CurrentUser(
            AccessorFor(new Claim(ClaimTypes.NameIdentifier, userId.ToString())));

        currentUser.Id.Should().Be(userId);
    }

    [Fact]
    public void Id_WhenTheClaimIsNotAGuid_IsNull()
    {
        // IssueCookieAsync can only mint a Guid today, but a cookie in an older format must
        // not throw its way out of a property read on every request.
        var currentUser = new CurrentUser(
            AccessorFor(new Claim(ClaimTypes.NameIdentifier, "not-a-guid")));

        currentUser.Id.Should().BeNull();
    }

    private static HttpContextAccessor AccessorFor(Claim claim) =>
        new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([claim], "test")),
            },
        };
}
