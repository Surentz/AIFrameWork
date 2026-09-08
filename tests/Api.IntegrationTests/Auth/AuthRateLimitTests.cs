using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Auth;

/// <summary>
/// The limiter's own behaviour, on its own host with a deliberately tiny limit. Outside
/// ApiFactoryCollection on purpose: the shared factory raises the limit out of the way so that
/// registering a user per test does not exhaust it, which is exactly what this class must not do.
/// </summary>
public sealed class AuthRateLimitTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const int PermitLimit = 3;

    private readonly WebApplicationFactory<Program> _factory;

    public AuthRateLimitTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            // No container: every request this class sends is rejected by the validator (empty
            // username) or by the limiter, so none of them reaches the database. Same placeholder
            // trick HealthTests uses, and the same reason Wolverine durability is off.
            builder.UseSetting(
                "ConnectionStrings:Default",
                "Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder");
            builder.UseSetting("Wolverine:Durable", "false");
            builder.UseSetting(
                "RateLimiting:Auth:PermitLimit", PermitLimit.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("RateLimiting:Auth:WindowSeconds", "60");
        });
    }

    // Combines what the brief's Step 1 specifies as two separate tests
    // (PostLogin_BeyondThePermitLimit_Returns429 and PostLogin_WhenRejected_SaysWhenToComeBack)
    // into one. The class holds a single IClassFixture<WebApplicationFactory<Program>>, so both
    // tests would share one host, and the limiter partitions on remote address - which for an
    // in-memory test host is the single "unknown" bucket. Both tests would therefore draw from
    // the same fixed window of PermitLimit permits inside the same 60 seconds, and xUnit does not
    // guarantee ordering within a class - whichever ran second would find the window already
    // exhausted. Merging removes the shared-window race while keeping every assertion from both.
    [Fact]
    public async Task PostLogin_BeyondThePermitLimit_Returns429WithRetryAfter()
    {
        using var client = _factory.CreateClient();
        var empty = new { Username = "", Password = "", RememberMe = false };

        for (var i = 0; i < PermitLimit; i++)
        {
            var allowed = await client.PostAsJsonAsync("/api/auth/login", empty);
            allowed.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "an empty username fails validation before touching the database, but it is still a permitted request");
        }

        var rejected = await client.PostAsJsonAsync("/api/auth/login", empty);

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull(
            "a 429 with no Retry-After leaves a well-behaved client guessing");
    }
}
