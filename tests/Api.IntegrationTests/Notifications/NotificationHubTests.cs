using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Notifications;

/// <summary>
/// The realtime hub's wiring and its authorization, with realtime deliberately switched on —
/// ApiFactory leaves it off for every other test. Asserts against SignalR's own negotiate
/// endpoint rather than standing up a full client: what is worth pinning here is that the route
/// exists only when configured and refuses anonymous callers, and negotiate is where both of
/// those are decided.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class NotificationHubTests : IDisposable
{
    private const string NegotiatePath = "/hubs/notifications/negotiate?negotiateVersion=1";

    private readonly ApiFactory _factory;
    private readonly WebApplicationFactory<Program> _realtime;

    public NotificationHubTests(ApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
        _realtime = factory.WithWebHostBuilder(
            builder => builder.UseSetting("Realtime:Enabled", "true"));
    }

    public void Dispose() => _realtime.Dispose();

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();

        // The same registration shape ApiFactory.CreateAuthenticatedClientAsync uses. Repeated
        // rather than reused because that helper is bound to the shared factory, and two of the
        // tests here need a client on the realtime host instead.
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                Username = $"u{Guid.NewGuid():N}"[..32],
                Password = ApiFactory.RegisteredPassword,
                DisplayName = "Hub Test User",
            });

        response.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Negotiate_WithoutASession_IsUnauthorized()
    {
        // [Authorize] on the hub is load-bearing: without it the connection is anonymous,
        // UserIdentifier is null, and Clients.User(...) would push to nobody.
        using var client = _realtime.CreateClient();

        var response = await client.PostAsync(NegotiatePath, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Negotiate_WithASession_Succeeds()
    {
        using var client = await SignedInAsync(_realtime);

        var response = await client.PostAsync(NegotiatePath, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Negotiate_WhenRealtimeIsOff_IsNotMappedAtAll()
    {
        // The gating half: mapping a hub whose INotificationPush was never registered would
        // accept connections that can never receive anything. Uses the shared factory, which
        // leaves Realtime:Enabled false.
        using var client = await SignedInAsync(_factory);

        var response = await client.PostAsync(NegotiatePath, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WithRealtimeOn_TheFeedStillWorks()
    {
        // Push is an optimization over the REST feed, never a replacement for it, and turning it
        // on must not change what the feed returns.
        using var client = await SignedInAsync(_realtime);

        var response = await client.GetAsync("/api/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
