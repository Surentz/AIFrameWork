using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Orders;

public sealed class Order : Entity
{
    private Order(Guid id, string sku, int quantity, DateTimeOffset placedAt)
    {
        Id = id;
        Sku = sku;
        Quantity = quantity;
        PlacedAt = placedAt;
    }

    public Guid Id { get; private set; }

    public string Sku { get; private set; }

    public int Quantity { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }

    public static Order Place(Guid id, string sku, int quantity, DateTimeOffset placedAt)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new DomainException("An order needs a sku.");
        }

        if (quantity <= 0)
        {
            throw new DomainException("An order needs a positive quantity.");
        }

        var order = new Order(id, sku, quantity, placedAt);
        order.Raise(new OrderPlaced(id, sku, quantity));
        return order;
    }
}
