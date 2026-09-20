using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using AiFramework.Application.Users;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests.Monitoring;

/// <summary>
/// The sign-in audit over real HTTP: what gets recorded, and — more importantly — what the
/// caller is still not told. See ADR 0021.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class SignInAuditTests(ApiFactory factory)
{
    private readonly ApiFactory _factory = factory;

    private async Task<SignInOutcome?> LastOutcomeForAsync(string username)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        var latest = await context.SignInEvents
            .AsNoTracking()
            .Where(e => e.UsernameAttempted == username)
            .OrderByDescending(e => e.At)
            .FirstOrDefaultAsync();

        return latest?.Outcome;
    }

    [Fact]
    public async Task ASuccessfulSignIn_IsRecorded()
    {
        var (_, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = username, Password = ApiFactory.RegisteredPassword, RememberMe = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LastOutcomeForAsync(username)).Should().Be(SignInOutcome.Succeeded);
    }

    [Fact]
    public async Task AWrongPassword_IsRecordedAsBadCredentials()
    {
        var (_, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        var client = _factory.CreateClient();

        await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = username, Password = "not the right password", RememberMe = false });

        (await LastOutcomeForAsync(username)).Should().Be(SignInOutcome.BadCredentials);
    }

    [Fact]
    public async Task AnAttemptAgainstAnAccountThatDoesNotExist_IsRecorded()
    {
        var username = $"ghost{Guid.NewGuid():N}"[..32];
        var client = _factory.CreateClient();

        await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = username, Password = "anything at all", RememberMe = false });

        // The row an enumeration sweep leaves behind. Without the attempted username being
        // stored, an attack that probes a thousand names is invisible: there is no user id to
        // hang the attempts off.
        (await LastOutcomeForAsync(username)).Should().Be(SignInOutcome.UnknownUser);
    }

    [Fact]
    public async Task TheResponse_IsIdentical_ForAnUnknownUserAndAWrongPassword()
    {
        var (_, known) = await _factory.CreateAuthenticatedClientWithUsernameAsync();
        var unknown = $"ghost{Guid.NewGuid():N}"[..32];
        var client = _factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = known, Password = "not the right password", RememberMe = false });
        var noSuchUser = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = unknown, Password = "not the right password", RememberMe = false });

        // The audit records the difference; the RESPONSE must not. A differing status or body
        // between "no such user" and "wrong password" is an account-enumeration oracle, and
        // adding an audit is exactly the kind of change that could leak one by accident.
        // ADR 0006.
        noSuchUser.StatusCode.Should().Be(wrongPassword.StatusCode);

        // Everything BUT traceId, which is per-request by construction and says nothing about
        // which account was named. Compared field by field rather than as whole strings, so this
        // still fails on a differing title, detail or status - the parts that would leak.
        (await WithoutTraceIdAsync(noSuchUser))
            .Should().Be(await WithoutTraceIdAsync(wrongPassword));
    }

    /// <summary>The problem body with its per-request trace id removed.</summary>
    private static async Task<string> WithoutTraceIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var fields = document.RootElement
            .EnumerateObject()
            .Where(property => !string.Equals(property.Name, "traceId", StringComparison.Ordinal))
            .Select(property => $"{property.Name}={property.Value}")
            .Order(StringComparer.Ordinal);

        return string.Join("|", fields);
    }

    [Fact]
    public async Task SigningOutEverywhere_IsRecorded()
    {
        var (client, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();

        var response = await client.PostAsync(
            new Uri("/api/auth/sign-out-everywhere", UriKind.Relative), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LastOutcomeForAsync(username)).Should().Be(SignInOutcome.SignedOutEverywhere);
    }

    [Fact]
    public async Task AnAuthenticatedRequest_StampsLastSeen()
    {
        var (client, username) = await _factory.CreateAuthenticatedClientWithUsernameAsync();

        (await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var normalized = Domain.Users.User.Normalize(username);

        var lastSeen = await context.Users
            .AsNoTracking()
            .Where(u => u.UsernameNormalized == normalized)
            .Select(u => u.LastSeenAt)
            .SingleAsync();

        lastSeen.Should().NotBeNull("the session-validation path stamps it on every request that " +
            "finds the stored value stale, and a fresh user's is null");
    }
}
