using AiFramework.Api.Auth;
using AiFramework.Api.Orders;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Fulfilment;

/// <summary>
/// The operator's side of an order: every buyer's orders awaiting shipment, and shipping them.
/// </summary>
/// <remarks>
/// <para>
/// The policy sits on the CONTROLLER, so an action added later is gated by default. A member gets
/// 403 rather than 404: the route is a fixed string in the SPA bundle and admits nothing by
/// existing. An unknown order id is still a 404, exactly as for its buyer.
/// </para>
/// <para>
/// Shipping lives here and not on <c>OrdersController</c>, because a buyer shipping their own order
/// permanently blocked the operator's real ship. Cancelling stays with the buyer. See ADR 0024.
/// </para>
/// </remarks>
[ApiController]
[Route("api/fulfilment/orders")]
[Authorize(Policy = AuthorizationPolicies.Orders.Fulfil)]
// S6960: the queue and shipping from it are one resource, api/fulfilment/orders — declined on the
// grounds NotificationsController records.
#pragma warning disable S6960
public sealed class FulfilmentController(
    ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
#pragma warning restore S6960
{
    /// <summary>Every buyer's orders in one status, oldest first, one page at a time.</summary>
    [HttpGet]
    [ProducesResponseType<FulfilmentOrderPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> List(
        [FromQuery] OrderStatus status = OrderStatus.Placed,
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await queries.SendAsync(
            new GetOrdersToFulfil(status, limit, cursor), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new FulfilmentOrderPageResponse
            {
                Items = [.. result.Value.Items.Select(i => new FulfilmentOrderResponse
                {
                    Id = i.Id,
                    BuyerId = i.BuyerId,
                    BuyerUsername = i.BuyerUsername,
                    Status = i.Status,
                    Sku = i.Sku,
                    Quantity = i.Quantity,
                    PlacedAt = i.PlacedAt,
                    ProductName = i.ProductName,
                    UnitPrice = i.UnitPrice,
                })],
                NextCursor = result.Value.NextCursor,
            })
            : result.Problem(HttpContext);
    }

    /// <summary>
    /// Marks any buyer's order as shipped. 409 if it has already shipped or was cancelled — an
    /// illegal transition is a conflict with existing state, not a malformed request.
    /// </summary>
    [HttpPost("{id:guid}/ship")]
    [ProducesResponseType<OrderStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Ship(Guid id, CancellationToken cancellationToken)
    {
        var result = await commands.SendAsync(
            new ShipOrder(id), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new OrderStatusResponse
            {
                OrderId = result.Value.OrderId,
                Status = result.Value.Status,
                ChangedAt = result.Value.ChangedAt,
            })
            : result.Problem(HttpContext);
    }
}
