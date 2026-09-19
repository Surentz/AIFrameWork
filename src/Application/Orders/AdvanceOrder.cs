using AiFramework.Application.Abstractions;
using AiFramework.Domain;
using AiFramework.Domain.Orders;
using FluentValidation;

namespace AiFramework.Application.Orders;

/// <summary>
/// Moves one of the caller's own orders to Shipped.
/// </summary>
/// <remarks>
/// Owner-scoped, which is admittedly odd for shipping — in a real system an operator ships,
/// not the buyer. It is scoped this way because there is no role system yet (the same gap
/// <c>User.SecurityStamp</c> calls a "plan 2" idea, and the same one ADR 0013 works around for
/// the catalogue), and owner-scoped is the choice that cannot become a privilege escalation in
/// the meantime. When roles arrive, this is the command whose authorization changes; cancelling
/// stays with the buyer.
/// </remarks>
public sealed record ShipOrder(Guid OrderId) : ICommand<OrderStatusView>, IInvalidatesCache
{
    // Shipping changes what both order reads return.
    public IReadOnlyList<string> Tags => [nameof(GetOrders), nameof(GetOrder)];
}

/// <summary>What both transition commands answer with, so a client can render the new state.</summary>
public sealed record OrderStatusView(Guid OrderId, OrderStatus Status, DateTimeOffset ChangedAt);

public sealed class ShipOrderValidator : AbstractValidator<ShipOrder>
{
    public ShipOrderValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
    }
}

public sealed class ShipOrderHandler(
    IOrderRepository orders, ICurrentUser currentUser, IClock clock)
    : ICommandHandler<ShipOrder, OrderStatusView>
{
    public async Task<Result<OrderStatusView>> HandleAsync(
        ShipOrder command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<OrderStatusView>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        // Tracked: this handler mutates the order, and GetAsync reads untracked — the write
        // would be lost silently. See IOrderRepository.GetAsync's remarks.
        var order = await orders
            .GetForUpdateAsync(command.OrderId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (order is null)
        {
            return Result.Failure<OrderStatusView>(new Error(
                ErrorKind.NotFound, "orders.not_found", "That order does not exist."));
        }

        var shippedAt = clock.UtcNow;

        // Order.Ship throws on an illegal transition rather than returning a Result, because a
        // broken state machine IS a broken invariant. Caught here and turned into a 409 rather
        // than left to GlobalExceptionHandler's 400 for DomainException: "you already shipped
        // this" is a conflict with existing state, not a malformed request the caller can fix by
        // editing their input.
        //
        // OrderStateException specifically, not DomainException: catching the base would also
        // swallow input-validation failures and answer 409 for them. Ship happens to throw only
        // conflicts today, but catching the narrow type is what keeps that true if it ever
        // gains an input rule.
        try
        {
            order.Ship(shippedAt);
        }
        catch (OrderStateException exception)
        {
            return Result.Failure<OrderStatusView>(new Error(
                ErrorKind.Conflict, "orders.cannot_ship", exception.Message));
        }

        return Result.Success(new OrderStatusView(order.Id, order.Status, shippedAt));
    }
}

/// <summary>Cancels one of the caller's own orders, with a reason.</summary>
public sealed record CancelOrder(Guid OrderId, string Reason) : ICommand<OrderStatusView>, IInvalidatesCache
{
    public IReadOnlyList<string> Tags => [nameof(GetOrders), nameof(GetOrder)];
}

public sealed class CancelOrderValidator : AbstractValidator<CancelOrder>
{
    public CancelOrderValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(Order.MaxCancellationReasonLength);
    }
}

public sealed class CancelOrderHandler(
    IOrderRepository orders, ICurrentUser currentUser, IClock clock)
    : ICommandHandler<CancelOrder, OrderStatusView>
{
    public async Task<Result<OrderStatusView>> HandleAsync(
        CancelOrder command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<OrderStatusView>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        // Tracked: this handler mutates the order, and GetAsync reads untracked — the write
        // would be lost silently. See IOrderRepository.GetAsync's remarks.
        var order = await orders
            .GetForUpdateAsync(command.OrderId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (order is null)
        {
            return Result.Failure<OrderStatusView>(new Error(
                ErrorKind.NotFound, "orders.not_found", "That order does not exist."));
        }

        var cancelledAt = clock.UtcNow;

        // OrderStateException ONLY. Cancel throws for two different classes of problem — a state
        // conflict (409) and a malformed reason (400) — and catching the base DomainException
        // answered 409 for both. That was unreachable purely because CancelOrderValidator above
        // duplicates the same input rules and rejects them first with a 400; the moment the
        // validator and the aggregate drifted, a blank reason would have become a 409 carrying a
        // domain message. A missing or overlong reason now falls through to
        // GlobalExceptionHandler's 400, which is what it always should have been.
        try
        {
            order.Cancel(command.Reason, cancelledAt);
        }
        catch (OrderStateException exception)
        {
            return Result.Failure<OrderStatusView>(new Error(
                ErrorKind.Conflict, "orders.cannot_cancel", exception.Message));
        }

        return Result.Success(new OrderStatusView(order.Id, order.Status, cancelledAt));
    }
}
