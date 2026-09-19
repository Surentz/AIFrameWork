namespace AiFramework.Domain.Orders;

/// <summary>
/// Where an order is in its life. Persisted by name, like
/// <see cref="Notifications.NotificationKind"/> — the names are a stored contract.
///
/// <see cref="Placed"/> is first so that it is also the CLR default: rows written before this
/// column existed materialize as Placed, which is what they were.
/// </summary>
public enum OrderStatus
{
    Placed,
    Shipped,
    Cancelled,
}
