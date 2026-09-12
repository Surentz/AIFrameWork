using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Auth;

/// <summary>
/// Whether the auth limiter believes X-Forwarded-For. Outside ApiFactoryCollection for the same
/// reason AuthRateLimitTests is: these hosts run with a deliberately tiny permit limit, which the
/// shared factory raises out of the way.
/// </summary>
/// <remarks>
/// The flag-OFF test is the important one. Trusting the header requires clearing
/// KnownNetworks/KnownProxies, and a build that trusts it everywhere would let any caller who can
/// reach the API directly mint a fresh rate-limit partition per request by varying the header -
/// a complete bypass of ADR 0008's volume defence. If this test ever goes green by accident, the
/// gate has been removed.
/// </remarks>
public sealed class ForwardedHeadersTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const int PermitLimit = 1;
    private const int WindowSeconds = 60;

    private readonly WebApplicationFactory<Program> _bare;

    public ForwardedHeadersTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _bare = factory;
    }

    private WebApplicationFactory<Program> HostWith(bool forwardedHeadersEnabled) =>
        _bare.WithWebHostBuilder(builder =>
        {
            // No container: every request below is rejected by the validator (empty username) or
            // by the limiter, so none reaches the database. Same placeholder trick HealthTests
            // and AuthRateLimitTests use, and the same reason Wolverine durability is off.
            builder.UseSetting(
                "ConnectionStrings:Default",
                "Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder");
            builder.UseSetting("Wolverine:Durable", "false");
            builder.UseSetting(
                "RateLimiting:Auth:PermitLimit",
                PermitLimit.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting(
                "RateLimiting:Auth:WindowSeconds",
                WindowSeconds.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting(
                "ForwardedHeaders:Enabled",
                forwardedHeadersEnabled ? "true" : "false");
        });

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { Username = "", Password = "", RememberMe = false }),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task PostLogin_WithForwardedHeadersDisabled_IgnoresTheHeader()
    {
        using var factory = HostWith(forwardedHeadersEnabled: false);
        using var client = factory.CreateClient();

        var first = await PostLoginAsync(client, "203.0.113.1");
        first.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "an empty username fails validation, but the request still spends a permit");

        var second = await PostLoginAsync(client, "203.0.113.2");

        second.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests,
            "with the header ignored both requests share the one partition, so the second is over the limit");
    }

    [Fact]
    public async Task PostLogin_WithForwardedHeadersEnabled_PartitionsByTheForwardedAddress()
    {
        using var factory = HostWith(forwardedHeadersEnabled: true);
        using var client = factory.CreateClient();

        var first = await PostLoginAsync(client, "203.0.113.1");
        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var second = await PostLoginAsync(client, "203.0.113.2");

        second.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a different forwarded address is a different partition, so it has its own permit");

        var third = await PostLoginAsync(client, "203.0.113.1");

        third.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests,
            "the first address has already spent its single permit");
    }
}
