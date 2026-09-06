using AiFramework.Application.Abstractions;
using AiFramework.Domain.Orders;
using FluentValidation;

namespace AiFramework.Application.Orders;

public sealed record PlaceOrder(string Sku, int Quantity) : ICommand<Guid>;

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

        var order = Order.Place(
            Guid.NewGuid(), userId, command.Sku, command.Quantity, clock.UtcNow);

        await orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

        return Result.Success(order.Id);
    }
}
