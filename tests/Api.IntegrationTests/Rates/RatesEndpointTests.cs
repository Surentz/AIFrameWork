using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Rates;

/// <summary>
/// ADR 0014's reference resilience integration, exercised over real HTTP. ApiFactory points
/// Resilience:ExchangeRateBaseAddress at an unreachable loopback port and sets
/// Resilience:Enabled=false, so every test here that reaches the provider observes a fast,
/// deterministic 503 rather than a real network call or a real backoff wait — the pipeline
/// itself, under a real request/retry budget, is ExchangeRateClientTests' job.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class RatesEndpointTests(ApiFactory factory)
{
    [Fact]
    public async Task GetRates_WhenAnonymous_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/rates?from=EUR&to=USD");

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "GetExchangeRate is ICacheable, and the caching behavior throws when one is " +
            "dispatched with no caller to scope its key to");
    }

    [Fact]
    public async Task GetRates_WithAMalformedCurrencyCode_Returns400WithoutCallingTheProvider()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/rates?from=EU&to=USD");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "GetExchangeRateHandler validates the currency code before ever calling the " +
            "provider, so this never reaches the unreachable stub address either");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be("rates.invalid_currency_code");
    }

    [Fact]
    public async Task GetRates_WithAMissingCurrencyCode_Returns400()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/rates?to=USD");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetRates_WhenTheProviderIsUnreachable_Returns503WithRetryAfter()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/rates?from=EUR&to=USD");

        response.StatusCode.Should().Be((HttpStatusCode)503);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var retryAfter = response.Headers.RetryAfter;
        retryAfter.Should().NotBeNull(
            "a 503 with no Retry-After leaves a well-behaved client guessing");
        retryAfter.Delta.Should().NotBeNull();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("title").GetString().Should().Be("rates.provider_unavailable");
        root.TryGetProperty("traceId", out _).Should().BeTrue(
            "every ProblemDetails from ResultExtensions.Problem carries one, 503 included");
    }
}
