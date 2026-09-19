using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Products;

/// <summary>
/// Raised by <see cref="Product.Update"/> only when the price actually moved — an update that
/// rewrites the name and leaves the price alone raises nothing, so no one is notified about a
/// change that did not happen.
///
/// Carries both prices because the notification says "was X, now Y", and re-reading the old
/// value later is impossible: the row has already been overwritten by the time anything handles
/// this.
/// </summary>
public sealed record ProductPriceChanged(
    Guid ProductId,
    string Sku,
    string Name,
    decimal OldPrice,
    decimal NewPrice) : IDomainEvent;
