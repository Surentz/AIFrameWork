using AiFramework.Application.Abstractions;
using AiFramework.Domain.Orders;
using FluentValidation;

namespace AiFramework.Application.Orders;

public sealed record PlaceOrder(string Sku, int Quantity) : ICommand<Guid>, IInvalidatesCache
{
    // Both order reads, because a new order changes the list and nothing else. nameof rather
    // than a literal, so renaming a query type is a compile error here instead of a silent
    // eviction that stops matching anything.
    public IReadOnlyList<string> Tags => [nameof(GetOrders), nameof(GetOrder)];
}

public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrder>
{
    public PlaceOrderValidator()
    {
        RuleFor(c => c.Sku).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Quantity).GreaterThan(0);
    }
}

public sealed class PlaceOrderHandler(
    IOrderRepository orders, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<PlaceOrder, Guid>
{
    public async Task<Result<Guid>> HandleAsync(PlaceOrder command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<Guid>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        // TEMPORARY, replaced in the next commit when the handler resolves the real product.
        // Kept for one commit only so that the signature change is reviewable on its own.
        var order = Order.Place(
            Guid.NewGuid(), userId, command.Quantity, clock.UtcNow,
            new OrderedProduct(Guid.NewGuid(), command.Sku, 0m), command.Sku);

        await orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

        return Result.Success(order.Id);
    }
}
