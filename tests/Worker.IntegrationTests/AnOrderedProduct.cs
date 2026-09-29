using AiFramework.Domain.Orders;

namespace AiFramework.Worker.IntegrationTests;

/// <summary>
/// A snapshot for the many tests that need an order to exist but assert nothing about which
/// product it was for. Named for how it reads at the call site: Order.Place(..., AnOrderedProduct.Any()).
/// </summary>
/// <remarks>
/// A copy of the Api.IntegrationTests helper: the test assemblies share no test library.
/// </remarks>
internal static class AnOrderedProduct
{
    public static OrderedProduct Any() => new(Guid.NewGuid(), "Widget", 9.99m);
}
