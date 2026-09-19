using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Orders;

public sealed class Order : Entity
{
    /// <summary>
    /// The longest cancellation reason <see cref="Cancel"/> accepts, mirrored by the EF mapping.
    /// </summary>
    public const int MaxCancellationReasonLength = 256;

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
    /// Where this order is in its life. Rows written before this column existed materialize as
    /// <see cref="OrderStatus.Placed"/>, which is what they were — see <see cref="OrderStatus"/>.
    /// </summary>
    public OrderStatus Status { get; private set; }

    /// <summary>
    /// When the order reached <see cref="OrderStatus.Shipped"/>, or null if it has not. Separate
    /// from <see cref="CancelledAt"/> rather than one shared "resolved at" column, because an
    /// order is never both and collapsing them would make the pair of nulls the only way to tell
    /// which happened.
    /// </summary>
    public DateTimeOffset? ShippedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>Why it was cancelled. Null unless <see cref="Status"/> is Cancelled.</summary>
    public string? CancellationReason { get; private set; }

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

    /// <summary>
    /// Placed -> Shipped. Terminal: there is deliberately no way back, because "unship" is not a
    /// thing that happens to a parcel — a shipment that should not have gone out is a return,
    /// which is a different aggregate this feature does not model.
    /// </summary>
    /// <exception cref="OrderStateException">
    /// If the order is already shipped, or was cancelled. Both are genuinely broken invariants
    /// rather than imprecise callers: shipping twice means two parcels, and shipping a cancelled
    /// order means one nobody agreed to pay for. This method throws NOTHING else, which is why
    /// ShipOrderHandler can map every exception it produces to a single status.
    /// </exception>
    public void Ship(DateTimeOffset shippedAt)
    {
        if (Status == OrderStatus.Shipped)
        {
            throw new OrderStateException("That order has already shipped.");
        }

        if (Status == OrderStatus.Cancelled)
        {
            throw new OrderStateException("A cancelled order cannot ship.");
        }

        Status = OrderStatus.Shipped;
        ShippedAt = shippedAt;
        Raise(new OrderShipped(Id, UserId, Sku));
    }

    /// <summary>
    /// Placed -> Cancelled. Refuses an order that has already shipped: once it is in the post,
    /// cancelling is a return, not a cancellation, and pretending otherwise would let the two
    /// diverge silently.
    /// </summary>
    /// <exception cref="OrderStateException">
    /// If the order has shipped or is already cancelled — a conflict with existing state.
    /// </exception>
    /// <exception cref="DomainException">
    /// If the reason is missing or too long — a malformed input, which must NOT reach the caller
    /// as the same status as a state conflict. See OrderStateException for the full reasoning.
    /// </exception>
    public void Cancel(string reason, DateTimeOffset cancelledAt)
    {
        if (Status == OrderStatus.Shipped)
        {
            throw new OrderStateException("An order that has shipped cannot be cancelled.");
        }

        if (Status == OrderStatus.Cancelled)
        {
            throw new OrderStateException("That order is already cancelled.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A cancellation needs a reason.");
        }

        var trimmed = reason.Trim();

        if (trimmed.Length > MaxCancellationReasonLength)
        {
            throw new DomainException(
                $"A cancellation reason cannot be longer than {MaxCancellationReasonLength} characters.");
        }

        Status = OrderStatus.Cancelled;
        CancelledAt = cancelledAt;
        CancellationReason = trimmed;
        Raise(new OrderCancelled(Id, UserId, Sku, trimmed));
    }
}
