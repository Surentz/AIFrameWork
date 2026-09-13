using AiFramework.Domain.Products;

namespace AiFramework.Domain.Orders;

/// <summary>
/// What a catalogue product was at the instant an order was placed, copied onto the order rather
/// than read back through <see cref="Product"/>. A later <c>UpdateProduct</c> therefore cannot
/// change what an existing order says it cost — and, because nothing on the order reads the
/// product, repricing cannot stale a cached order page either.
///
/// A record, not an entity: it has no identity of its own, and two snapshots carrying the same
/// three values are the same snapshot.
/// </summary>
public sealed record OrderedProduct
{
    public OrderedProduct(Guid productId, string name, decimal unitPrice)
    {
        if (productId == Guid.Empty)
        {
            throw new DomainException("A snapshot needs a product.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A snapshot needs a product name.");
        }

        if (name.Trim().Length > Product.MaxNameLength)
        {
            throw new DomainException(
                $"A product name cannot be longer than {Product.MaxNameLength} characters.");
        }

        if (unitPrice < 0m)
        {
            throw new DomainException("A unit price cannot be negative.");
        }

        // Mirrors Product.ValidatePrice: the column is numeric(18,2), so a third decimal would be
        // rounded away by Postgres and the snapshot would stop matching what was charged.
        if (decimal.Round(unitPrice, Product.PriceScale) != unitPrice)
        {
            throw new DomainException(
                $"A unit price cannot have more than {Product.PriceScale} decimal places.");
        }

        ProductId = productId;
        Name = name.Trim();
        UnitPrice = unitPrice;
    }

    public Guid ProductId { get; }

    public string Name { get; }

    public decimal UnitPrice { get; }
}
