using AiFramework.Application.Abstractions;
using AiFramework.Domain;
using AiFramework.Domain.Orders;
using FluentValidation;

namespace AiFramework.Application.Orders;

/// <summary>
/// A warehouse reports an order shipped (<c>shipment.confirmed.v1</c>). The worker's counterpart
/// of the operator's <see cref="ShipOrder"/>, and deliberately a different command: there is no
/// signed-in caller, the time is the warehouse's, and a redelivery must succeed. ADR 0026.
/// </summary>
/// <remarks>
/// Not <c>IInvalidatesCache</c>: the worker runs with the cache off, and the buyer's own cached
/// reads lag by their 30-second TTL - the limit ADR 0024 already accepted for an operator's ship.
/// </remarks>
public sealed record RecordShipment(Guid OrderId, string ShipmentId, DateTimeOffset ShippedAt)
    : ICommand<ShipmentOutcome>;

public enum ShipmentOutcome
{
    Shipped,
    AlreadyShipped,
}

public sealed class RecordShipmentValidator : AbstractValidator<RecordShipment>
{
    /// <summary>Clock skew we tolerate from a warehouse's clock.</summary>
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);

    /// <summary>The longest shipment id accepted - and the most a rejection message quotes.</summary>
    public const int MaximumShipmentIdLength = 128;

    public RecordShipmentValidator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.ShipmentId).NotEmpty().MaximumLength(MaximumShipmentIdLength);
        RuleFor(c => c.ShippedAt)
            .Must(shippedAt => shippedAt <= clock.UtcNow + MaximumFutureSkew)
            .WithMessage("A shipment cannot be dated more than five minutes in the future.");
    }
}

public sealed class RecordShipmentHandler(IOrderRepository orders)
    : ICommandHandler<RecordShipment, ShipmentOutcome>
{
    public async Task<Result<ShipmentOutcome>> HandleAsync(
        RecordShipment command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Cross-owner by design: a warehouse ships whoever's order it is. The read is the one
        // named for fulfilment, for the reason IOrderRepository.GetForFulfilmentAsync gives.
        var order = await orders
            .GetForFulfilmentAsync(command.OrderId, cancellationToken)
            .ConfigureAwait(false);

        if (order is null)
        {
            return Result.Failure<ShipmentOutcome>(new Error(
                ErrorKind.NotFound, "orders.not_found", "That order does not exist."));
        }

        // Idempotent by state, not by message id: an external producer may send no message id at
        // all, and a redelivery or a second confirmation must not fail.
        if (order.Status is OrderStatus.Shipped)
        {
            return Result.Success(ShipmentOutcome.AlreadyShipped);
        }

        if (command.ShippedAt < order.PlacedAt)
        {
            return Result.Failure<ShipmentOutcome>(new Error(
                ErrorKind.Validation, "shipments.before_placed",
                "A shipment cannot be dated before the order was placed."));
        }

        // OrderStateException only, as ShipOrderHandler records: the narrow type keeps an input
        // rule from ever turning into a 409-shaped conflict.
        try
        {
            order.Ship(command.ShippedAt);
        }
        catch (OrderStateException exception)
        {
            return Result.Failure<ShipmentOutcome>(new Error(
                ErrorKind.Conflict, "orders.cannot_ship", exception.Message));
        }

        return Result.Success(ShipmentOutcome.Shipped);
    }
}
