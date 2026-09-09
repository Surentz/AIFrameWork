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

    [Fact]
    public void Id_ReadAgainAfterHttpContextIsGone_StillReturnsTheMemoizedValue()
    {
        // The scenario the caching behavior's cache-miss factory reproduces: a later read of
        // Id happens with no ambient HttpContext at all, on the same CurrentUser instance that
        // already resolved one successfully. Mutating the same accessor rather than building a
        // second CurrentUser is what makes this the same scenario as the scoped-instance reuse
        // Behaviors.CachedAsync relies on.
        var userId = Guid.NewGuid();
        var accessor = AccessorFor(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        var currentUser = new CurrentUser(accessor);

        currentUser.Id.Should().Be(userId);

        accessor.HttpContext = null;

        currentUser.Id.Should().Be(
            userId,
            "a live re-read would find no HttpContext and return null, which is exactly the " +
            "false-401 this memoization exists to prevent");
    }

    [Fact]
    public void Id_WhenSignedOutThenSignedIn_ReturnsTheNewId()
    {
        // The other half of the contract: only a NON-NULL read is memoized. A request that
        // reads Id before the user is authenticated must keep seeing the live value, not get
        // stuck on the first (null) read.
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var currentUser = new CurrentUser(accessor);

        currentUser.Id.Should().BeNull();

        var userId = Guid.NewGuid();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
        };

        currentUser.Id.Should().Be(userId);
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
