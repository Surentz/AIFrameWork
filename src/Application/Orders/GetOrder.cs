using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Orders;

public sealed record OrderView(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt);

public sealed record GetOrder(Guid Id) : IQuery<OrderView>, ICacheable
{
    public string CacheKey => Id.ToString();

    public TimeSpan Duration => TimeSpan.FromSeconds(30);
}

public sealed class GetOrderHandler(IOrderRepository orders, ICurrentUser currentUser)
    : IQueryHandler<GetOrder, OrderView>
{
    public async Task<Result<OrderView>> HandleAsync(GetOrder query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<OrderView>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        var order = await orders.GetAsync(query.Id, userId, cancellationToken).ConfigureAwait(false);

        // No ownership branch: the repository cannot return another user's order, so someone
        // else's id lands on the same not-found failure as an id that was never issued.
        return order is null
            ? Result.Failure<OrderView>(new Error(
                ErrorKind.NotFound, "order.not_found", $"No order with id '{query.Id}'."))
            : Result.Success(
                new OrderView(order.Id, order.Sku, order.Quantity, order.PlacedAt));
    }
}
