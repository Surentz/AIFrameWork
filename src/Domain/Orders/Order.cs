using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Orders;

public sealed class Order : Entity
{
    // Deliberately does NOT take OrderedProduct as a constructor parameter, even though Place
    // always has one to give it. EF's constructor-binding convention can bind a reference to an
    // owned type's OWN constructor (OrderedProduct's), but not the owning entity's — "Navigations
    // to related entities, including references to owned types, cannot be bound" is EF's own
    // wording for exactly this shape. Product is assigned by Place, below, through the private
    // setter instead, which both EF's owned-navigation fixup and this constructor are free to use.
    private Order(
        Guid id,
        Guid userId,
        string sku,
        int quantity,
        DateTimeOffset placedAt)
    {
        Id = id;
        UserId = userId;
        Sku = sku;
        Quantity = quantity;
        PlacedAt = placedAt;
    }

    public Guid Id { get; private set; }

    /// <summary>The user who placed it. Set once; an order is never reassigned.</summary>
    public Guid UserId { get; private set; }

    public string Sku { get; private set; }

    public int Quantity { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }

    /// <summary>
    /// The catalogue entry as it was when this order was placed.
    ///
    /// Nullable ONLY because the table holds rows written before the catalogue link existed, and
    /// EF must materialize those with something. It is not an invariant that bends: <see
    /// cref="Place"/> requires a snapshot, so no order created from here on can be without one.
    /// </summary>
    public OrderedProduct? Product { get; private set; }

    /// <summary>
    /// Takes a NON-nullable snapshot: from the catalogue link onwards there is no such thing as
    /// an order without a product. <paramref name="sku"/> is passed separately rather than read
    /// off the snapshot because the snapshot is the product's identity and price, not its sku —
    /// the caller supplies the catalogue's own normalized sku, which PlaceOrderHandler reads from
    /// the Product it just resolved.
    /// </summary>
    public static Order Place(
        Guid id,
        Guid userId,
        int quantity,
        DateTimeOffset placedAt,
        OrderedProduct product,
        string sku)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException("An order needs a user.");
        }

        if (product is null)
        {
            throw new DomainException("An order needs a catalogue product.");
        }

        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new DomainException("An order needs a sku.");
        }

        if (quantity <= 0)
        {
            throw new DomainException("An order needs a positive quantity.");
        }

        var order = new Order(id, userId, sku, quantity, placedAt) { Product = product };
        order.Raise(new OrderPlaced(id, sku, quantity));
        return order;
    }
}
