using System.Net;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Auth;

[Collection(nameof(ApiFactoryCollection))]
public sealed class SessionInvalidationTests(ApiFactory factory)
{
    private static readonly Uri Me = new("/api/auth/me", UriKind.Relative);

    private readonly ApiFactory _factory = factory;

    [Fact]
    public async Task ARotatedStamp_InvalidatesTheSessionOnItsNextRequest()
    {
        var (client, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        (await client.GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.OK);

        await _factory.RotateSecurityStampAsync(username);

        (await client.GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARotationForOneUser_LeavesOtherUsersSessionsAlone()
    {
        var (ada, adaName) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        var grace = await _factory.CreateAuthenticatedClientAsync();

        await _factory.RotateSecurityStampAsync(adaName);

        (await ada.GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await grace.GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnUntouchedSession_KeepsWorkingAcrossManyRequests()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        // The per-request validation runs on every one of these. A bug that rejected a valid
        // session intermittently - a stale DbContext, a tracked entity, a cached read - shows up
        // here rather than as a flake somewhere else in the suite.
        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
