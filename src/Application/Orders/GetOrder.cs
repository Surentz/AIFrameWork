using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Orders;

public sealed record OrderView(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt);

public sealed record GetOrder(Guid Id) : IQuery<OrderView>;

public sealed class GetOrderHandler(IOrderRepository orders) : IQueryHandler<GetOrder, OrderView>
{
    public async Task<Result<OrderView>> HandleAsync(GetOrder query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var order = await orders.GetAsync(query.Id, cancellationToken).ConfigureAwait(false);

        return order is null
            ? Result.Failure<OrderView>(new Error(
                ErrorKind.NotFound, "order.not_found", $"No order with id '{query.Id}'."))
            : Result.Success(
                new OrderView(order.Id, order.Sku, order.Quantity, order.PlacedAt));
    }
}
