using System.Net;
using AiFramework.Domain.Users;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Monitoring;

/// <summary>
/// The gate ADR 0020 exists for. These run against the real policy and the real per-request role
/// read; nothing here substitutes the validator.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class MonitoringAccessTests(ApiFactory factory)
{
    private static readonly Uri Access = new("/api/monitoring/access", UriKind.Relative);
    private static readonly Uri Me = new("/api/auth/me", UriKind.Relative);

    private readonly ApiFactory _factory = factory;

    [Fact]
    public async Task AnAnonymousCaller_IsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(Access);

        // 401, not 403: there is no session to refuse. The cookie handler's OnRedirectToLogin
        // turns this into a status code rather than a redirect to a page that does not exist.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AMember_IsForbidden()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync(Access);

        // 403 rather than 404, deliberately. Unlike an order id (ADR 0007) or a username
        // (ADR 0006), this route leaks nothing by admitting it exists - its path is a fixed
        // string in the SPA bundle every user downloads - and a 404 would send an administrator
        // debugging their own missing access hunting a broken deployment instead.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnAdministrator_IsAllowed()
    {
        var (client, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        await _factory.SetRoleAsync(username, UserRole.Admin);

        var response = await client.GetAsync(Access);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APromotion_TakesEffectWithoutSigningTheUserOut()
    {
        var (client, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        (await client.GetAsync(Access)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await _factory.SetRoleAsync(username, UserRole.Admin);

        // The SAME cookie, never re-issued. If the role were a claim minted at sign-in, this
        // would still be 403 until the user signed in again - and the promotion would have had
        // to rotate the security stamp to force that, signing them out to grant them access.
        // See ADR 0020.
        (await client.GetAsync(Access)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ADemotion_TakesEffectOnTheVeryNextRequest()
    {
        var (client, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        await _factory.SetRoleAsync(username, UserRole.Admin);
        (await client.GetAsync(Access)).StatusCode.Should().Be(HttpStatusCode.OK);

        await _factory.SetRoleAsync(username, UserRole.Member);

        // The test this whole design exists to pass. No re-authentication, no TTL, no forced
        // sign-out: authority is read from the database on the same request that checks the
        // stamp, so revoking it is immediate. A cookie-borne role could not do this.
        (await client.GetAsync(Access)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ARoleChange_LeavesTheSessionItself_Valid()
    {
        var (client, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();

        await _factory.SetRoleAsync(username, UserRole.Admin);
        await _factory.SetRoleAsync(username, UserRole.Member);

        // ADR 0011 anticipated that a permission change would rotate the security stamp. It does
        // not, and must not: the role is not in the cookie, so there is nothing stale to
        // invalidate, and rotating would sign an administrator out on every API restart courtesy
        // of the startup reconciler. ADR 0020 supersedes that expectation.
        (await client.GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
