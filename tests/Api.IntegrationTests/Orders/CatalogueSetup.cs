using System.Net.Http.Json;

namespace AiFramework.Api.IntegrationTests.Orders;

/// <summary>
/// Every "place an order" test now needs a catalogue product first. One helper rather than the
/// same six lines in seven files.
/// </summary>
internal static class CatalogueSetup
{
    /// <summary>
    /// Creates a product and returns its normalized sku. Unique per call: the catalogue is global
    /// and its sku index is unique, so a fixed literal would collide across tests sharing the
    /// one Postgres container.
    /// </summary>
    public static async Task<string> CreateProductAsync(HttpClient client, decimal price = 19.95m)
    {
        var sku = $"SKU-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new { Sku = sku, Name = "Widget", Description = (string?)null, Price = price });

        response.EnsureSuccessStatusCode();

        return sku;
    }
}
