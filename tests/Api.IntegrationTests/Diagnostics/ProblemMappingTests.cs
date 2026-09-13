using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Diagnostics;

/// <summary>
/// Proves ErrorKind.Unavailable's mapping to 503 + Retry-After over real HTTP, through
/// TestEndpointsStartupFilter's probe path rather than a real handler - nothing produces this
/// failure for real yet (ADR 0014's exchange-rate client is the first, and its own endpoint
/// tests take over proving it once it exists). The unit-level branch coverage for
/// ResultExtensions.Problem itself, including the Retry-After fallback and rounding, lives in
/// ResultExtensionsTests; this file exists only to prove the header survives the trip from
/// ObjectResult.ExecuteResultAsync onto the wire, the one part a pure unit test cannot see.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class ProblemMappingTests(ApiFactory factory)
{
    [Fact]
    public async Task Problem_WithAnUnavailableFailure_Returns503WithRetryAfter()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/test/problem/unavailable");

        response.StatusCode.Should().Be((HttpStatusCode)503);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var retryAfter = response.Headers.RetryAfter;
        retryAfter.Should().NotBeNull(
            "a 503 with no Retry-After leaves a well-behaved client guessing, the same gap " +
            "AuthRateLimitTests proves the rate limiter's 429 does not have");
        retryAfter.Delta.Should().Be(TimeSpan.FromSeconds(5));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("status").GetInt32().Should().Be(503);
        root.TryGetProperty("traceId", out _).Should().BeTrue(
            "every ProblemDetails from ResultExtensions.Problem carries one, 503 included");
    }
}
