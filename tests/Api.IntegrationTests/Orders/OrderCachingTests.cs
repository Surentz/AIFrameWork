using System.Net.Http.Json;
using AiFramework.Api.Orders;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Orders;

/// <summary>
/// The cache's own behaviour over real HTTP, with it deliberately switched on. Joins
/// ApiFactoryCollection so it reuses the one Postgres container the project already starts, then
/// layers a second host on top with Cache:Enabled=true — WithWebHostBuilder composes over
/// ApiFactory.ConfigureWebHost, so the container's connection string and the outbox-pump removal
/// both still apply.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class OrderCachingTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _cached;

    public OrderCachingTests(ApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _cached = factory.WithWebHostBuilder(
            builder => builder.UseSetting("Cache:Enabled", "true"));
    }

    public void Dispose() => _cached.Dispose();

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = _cached.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                Username = $"u{Guid.NewGuid():N}"[..32],
                Password = "a long enough test password",
                DisplayName = "Cache Test User",
            });

        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task PlaceAsync(HttpClient client, string sku)
    {
        var response = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = sku, Quantity = 1 });

        response.EnsureSuccessStatusCode();
    }

    private static async Task<OrderPageResponse> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/orders");
        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<OrderPageResponse>();
        page.Should().NotBeNull();
        return page;
    }

    [Fact]
    public async Task PlacingAnOrder_ThenListingImmediately_ReturnsTheNewOrder()
    {
        using var client = await SignedInClientAsync();
        await PlaceAsync(client, "CACHE-FIRST");
        await ListAsync(client);

        // The page is now cached. Without synchronous eviction this second SKU would be missing.
        await PlaceAsync(client, "CACHE-SECOND");
        var page = await ListAsync(client);

        page.Items.Select(i => i.Sku).Should().Contain(
            "CACHE-SECOND",
            "a client that refetches straight after a 201 must read its own write; this is the " +
            "whole reason eviction is synchronous rather than riding the outbox");
    }

    [Fact]
    public async Task ListingOrders_ForTwoDifferentUsers_DoesNotShareAPage()
    {
        using var alice = await SignedInClientAsync();
        using var bob = await SignedInClientAsync();

        await PlaceAsync(alice, "ALICE-ONLY");
        await ListAsync(alice);

        var bobsPage = await ListAsync(bob);

        bobsPage.Items.Select(i => i.Sku).Should().NotContain(
            "ALICE-ONLY",
            "the cache key is scoped to ICurrentUser.Id; sharing one entry across callers would " +
            "be a data leak, not a stale read");
    }

    [Fact]
    public async Task FetchingAnOrderThatDoesNotExist_TwiceInARow_Returns404Both()
    {
        using var client = await SignedInClientAsync();
        var missing = Guid.NewGuid();

        var first = await client.GetAsync($"/api/orders/{missing}");
        var second = await client.GetAsync($"/api/orders/{missing}");

        first.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        second.StatusCode.Should().Be(
            System.Net.HttpStatusCode.NotFound,
            "the caching behavior's sentinel throws instead of returning Result.Value on a " +
            "failed Result, so a broken conversion back to a failed Result would already surface " +
            "as a 500 on the FIRST call, not just the second - this pins that conversion, not " +
            "retention. Whether a failure is actually kept out of the cache is proven separately, " +
            "at the Infrastructure level, by SendAsync_TwiceForTheSameUserAndArguments_" +
            "RunsTheHandlerOnce (Task 2) - a served 404 and a re-run 404 look identical over HTTP");
    }
}
