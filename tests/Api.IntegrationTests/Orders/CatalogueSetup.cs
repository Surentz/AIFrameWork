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
    /// <remarks>
    /// Through an administrator of its own, never the caller's client: creating a product needs
    /// <c>Catalogue.Manage</c> (ADR 0025), and the tests that call this are about ordering as a
    /// member. The product is global, so who created it makes no difference to them.
    /// </remarks>
    public static async Task<string> CreateProductAsync(ApiFactory factory, decimal price = 19.95m)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var sku = $"SKU-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

        using var admin = await factory.CreateAdminClientAsync();
        var response = await admin.PostAsJsonAsync(
            "/api/products",
            new { Sku = sku, Name = "Widget", Description = (string?)null, Price = price });

        response.EnsureSuccessStatusCode();

        return sku;
    }
}
