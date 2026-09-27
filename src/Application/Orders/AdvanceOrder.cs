using AiFramework.Application.Abstractions;
using AiFramework.Domain;
using AiFramework.Domain.Orders;
using FluentValidation;

namespace AiFramework.Application.Orders;

/// <summary>
/// Moves ANY buyer's order to Shipped. The operator's command, not the buyer's.
/// </summary>
/// <remarks>
/// <para>
/// Owner-scoped until ADR 0024: without roles, owner-scoped was the one choice that could not
/// become a privilege escalation, but it let a buyer ship their own order — and since
/// <c>Order.Ship</c> cannot be undone, that permanently blocked the operator's real one.
/// Authorization is now the <c>Orders.Fulfil</c> policy on <c>FulfilmentController</c>, the only
/// caller; this layer has no notion of roles, and the repository read it makes is the explicitly
/// cross-owner one. Cancelling stays with the buyer (ADR 0019).
/// </para>
/// <para>
/// The tags evict only the CALLER's cache entries — the operator's, not the buyer's. The buyer's
/// cached <c>GetOrder</c>/<c>GetOrders</c> can therefore show Placed for up to their thirty-second
/// TTL, the limit ADR 0013 already accepted for the catalogue; HybridCache is L1-only per pod and
/// eviction is scoped per caller by construction (ADRs 0009, 0010). They stay so that an
/// administrator shipping their OWN order sees it at once.
/// </para>
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

        // Still required although the order is not scoped to the caller: the cache eviction this
        // command declares is composed with the caller's id, and a sessionless dispatch has none.
        if (currentUser.Id is null)
        {
            return Result.Failure<OrderStatusView>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        // Tracked, and cross-owner: the operator ships someone else's order. See
        // IOrderRepository.GetForFulfilmentAsync.
        var order = await orders
            .GetForFulfilmentAsync(command.OrderId, cancellationToken)
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
