using System.Net.Http.Json;
using AiFramework.Api.Orders;
using AiFramework.Infrastructure.Caching;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        var skuFirst = await CatalogueSetup.CreateProductAsync(client);
        var skuSecond = await CatalogueSetup.CreateProductAsync(client);
        await PlaceAsync(client, skuFirst);
        await ListAsync(client);

        // The page is now cached. Without synchronous eviction this second SKU would be missing.
        await PlaceAsync(client, skuSecond);
        var page = await ListAsync(client);

        page.Items.Select(i => i.Sku).Should().Contain(
            skuSecond,
            "a client that refetches straight after a 201 must read its own write; this is the " +
            "whole reason eviction is synchronous rather than riding the outbox");

        // A second list of the same page is a cache HIT, and that is the point of asserting it:
        // nothing else in the suite ever reads a cached OrderPage back out, so without this the
        // deserialization half of the round-trip ADR 0009 rests on is never executed at all.
        var served = await ListAsync(client);
        served.Items.Should().BeEquivalentTo(page.Items);
    }

    [Fact]
    public void TheLayeredHost_ActuallyHasCachingEnabled()
    {
        // Every assertion above only means something if this host really has the cache on.
        // ApiFactory sets Cache:Enabled=false, and the constructor above layers
        // Cache:Enabled=true on top via WithWebHostBuilder, relying on last-write-wins over
        // ApiFactory.ConfigureWebHost's own settings — a method that was refactored on this
        // very branch. If that precedence ever inverts, every test in this class would go on
        // passing while testing nothing, with no failure anywhere. This is that tripwire.
        _cached.Services.GetRequiredService<IOptions<CacheOptions>>().Value.Enabled
            .Should().BeTrue("every assertion in this class is vacuous with the cache off");
    }

    [Fact]
    public async Task ListingOrders_ForTwoDifferentUsers_DoesNotShareAPage()
    {
        using var alice = await SignedInClientAsync();
        using var bob = await SignedInClientAsync();

        var aliceSku = await CatalogueSetup.CreateProductAsync(alice);
        await PlaceAsync(alice, aliceSku);
        await ListAsync(alice);

        var bobsPage = await ListAsync(bob);

        bobsPage.Items.Select(i => i.Sku).Should().NotContain(
            aliceSku,
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
