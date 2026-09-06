using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Auth;

[Collection(nameof(ApiFactoryCollection))]
public sealed class AuthEndpointTests(ApiFactory factory)
{
    private const string APassword = "a long enough test password";

    private static string AUsername() => $"u{Guid.NewGuid():N}"[..32];

    private static object ARegistration(string username) =>
        new { Username = username, Password = APassword, DisplayName = "Ada Lovelace" };

    [Fact]
    public async Task PostRegister_WithAFreeUsername_Returns200AndTheSession()
    {
        using var client = factory.CreateClient();
        var username = AUsername();

        var response = await client.PostAsJsonAsync("/api/auth/register", ARegistration(username));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var session = await response.Content.ReadFromJsonAsync<SessionDto>();
        session!.Username.Should().Be(username);
        session.DisplayName.Should().Be("Ada Lovelace");
        session.UserId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PostRegister_SetsTheSessionCookie()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", ARegistration(AUsername()));

        response.Headers.GetValues("Set-Cookie").Should().ContainMatch("aiframework.session=*");
    }

    [Fact]
    public async Task PostRegister_MarksTheCookieHttpOnly()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", ARegistration(AUsername()));

        // The whole reason for choosing a cookie over a token: script must not be able to read it.
        response.Headers.GetValues("Set-Cookie")
            .Should().ContainMatch("*httponly*", "the session must not be readable from JavaScript");
    }

    [Fact]
    public async Task PostRegister_WithATakenUsername_Returns409()
    {
        using var client = factory.CreateClient();
        var username = AUsername();
        (await client.PostAsJsonAsync("/api/auth/register", ARegistration(username)))
            .EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/auth/register", ARegistration(username));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be("user.username_taken");
    }

    [Fact]
    public async Task PostRegister_WithATooShortPassword_Returns400WithAFieldError()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = AUsername(), Password = "short", DisplayName = "Ada Lovelace" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("title").GetString().Should().Be("validation.failed");
        root.GetProperty("errors").GetProperty("Password").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PostLogin_WithTheRightPassword_Returns200()
    {
        var username = AUsername();
        using var registrar = factory.CreateClient();
        (await registrar.PostAsJsonAsync("/api/auth/register", ARegistration(username)))
            .EnsureSuccessStatusCode();

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { Username = username, Password = APassword, RememberMe = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostLogin_IsCaseInsensitiveAboutTheUsername()
    {
        var username = AUsername();
        using var registrar = factory.CreateClient();
        (await registrar.PostAsJsonAsync("/api/auth/register", ARegistration(username)))
            .EnsureSuccessStatusCode();

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = username.ToUpperInvariant(), Password = APassword, RememberMe = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostLogin_WithTheWrongPassword_Returns401()
    {
        var username = AUsername();
        using var registrar = factory.CreateClient();
        (await registrar.PostAsJsonAsync("/api/auth/register", ARegistration(username)))
            .EnsureSuccessStatusCode();

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { Username = username, Password = "not the password", RememberMe = false });

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized, "ErrorKind.Unauthorized must map to 401, not 400 or 500");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be("auth.failed");
    }

    [Fact]
    public async Task PostLogin_WithAnUnknownUsername_IsIndistinguishableFromAWrongPassword()
    {
        var username = AUsername();
        using var registrar = factory.CreateClient();
        (await registrar.PostAsJsonAsync("/api/auth/register", ARegistration(username)))
            .EnsureSuccessStatusCode();

        using var client = factory.CreateClient();
        var wrongPassword = await client.PostAsJsonAsync(
            "/api/auth/login", new { Username = username, Password = "not the password", RememberMe = false });
        var unknownUser = await client.PostAsJsonAsync(
            "/api/auth/login", new { Username = AUsername(), Password = "not the password", RememberMe = false });

        // Any difference here turns the endpoint into a way to find out which usernames exist.
        unknownUser.StatusCode.Should().Be(wrongPassword.StatusCode);
        (await StripTraceIdAsync(unknownUser))
            .Should().Be(await StripTraceIdAsync(wrongPassword), "traceId aside, the bodies must match");
    }

    [Fact]
    public async Task GetMe_WhenSignedIn_ReturnsTheUser()
    {
        var username = AUsername();
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", ARegistration(username)))
            .EnsureSuccessStatusCode();

        var session = await client.GetFromJsonAsync<SessionDto>("/api/auth/me");

        session!.Username.Should().Be(username);
    }

    [Fact]
    public async Task GetMe_WhenAnonymous_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "the cookie handler's default 302-to-login is overridden; an API must answer 401");
    }

    [Fact]
    public async Task PostLogout_ClearsTheSession()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var logout = await client.PostAsync("/api/auth/logout", content: null);
        var afterwards = await client.GetAsync("/api/auth/me");

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        afterwards.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostChangePassword_WithTheRightCurrentPassword_LetsTheNewOneSignIn()
    {
        var username = AUsername();
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", ARegistration(username)))
            .EnsureSuccessStatusCode();

        const string NewPassword = "an even longer new password";
        var change = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { CurrentPassword = APassword, NewPassword });

        change.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var fresh = factory.CreateClient();
        var login = await fresh.PostAsJsonAsync(
            "/api/auth/login", new { Username = username, Password = NewPassword, RememberMe = false });

        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostChangePassword_WithTheWrongCurrentPassword_Returns401()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { CurrentPassword = "not the password", NewPassword = "an even longer new password" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostChangePassword_WhenAnonymous_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { CurrentPassword = APassword, NewPassword = "an even longer new password" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// traceId is per-request and always differs, so it is the one field two otherwise-identical
    /// problem bodies cannot share.
    /// </summary>
    private static async Task<string> StripTraceIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var traceId = document.RootElement.GetProperty("traceId").GetString();

        return body.Replace(traceId!, "TRACE", StringComparison.Ordinal);
    }

    private sealed record SessionDto(Guid UserId, string Username, string DisplayName);
}
