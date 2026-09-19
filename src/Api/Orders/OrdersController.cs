using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Orders;

[ApiController]
[Route("api/orders")]
[Authorize]
public sealed class OrdersController(
    ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
{
    /// <summary>Places an order and returns its new identifier.</summary>
    [HttpPost]
    [ProducesResponseType<Guid>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> Place(PlaceOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await commands.SendAsync(
            new PlaceOrder(request.Sku, request.Quantity), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? CreatedAtAction(nameof(Get), new { id = result.Value }, result.Value)
            : result.Problem(HttpContext);
    }

    /// <summary>Lists orders newest first, one page at a time.</summary>
    [HttpGet]
    [ProducesResponseType<OrderPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> List(
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await queries.SendAsync(
            new GetOrders(limit, cursor), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new OrderPageResponse
            {
                Items = [.. result.Value.Items.Select(i => new OrderListItemResponse
                {
                    Id = i.Id,
                    Sku = i.Sku,
                    Quantity = i.Quantity,
                    PlacedAt = i.PlacedAt,
                    ProductId = i.ProductId,
                    ProductName = i.ProductName,
                    UnitPrice = i.UnitPrice,
                })],
                NextCursor = result.Value.NextCursor,
            })
            : result.Problem(HttpContext);
    }

    /// <summary>
    /// Marks one of the caller's own orders as shipped. 409 if it has already shipped or was
    /// cancelled — an illegal transition is a conflict with existing state, not a malformed
    /// request.
    /// </summary>
    [HttpPost("{id:guid}/ship")]
    [ProducesResponseType<OrderStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Ship(Guid id, CancellationToken cancellationToken)
    {
        var result = await commands.SendAsync(
            new ShipOrder(id), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(ToResponse(result.Value)) : result.Problem(HttpContext);
    }

    /// <summary>
    /// Cancels one of the caller's own orders. 409 if it has already shipped or was already
    /// cancelled.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<OrderStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Cancel(
        Guid id, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await commands.SendAsync(
            new CancelOrder(id, request.Reason), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Ok(ToResponse(result.Value)) : result.Problem(HttpContext);
    }

    private static OrderStatusResponse ToResponse(OrderStatusView view) => new()
    {
        OrderId = view.OrderId,
        Status = view.Status,
        ChangedAt = view.ChangedAt,
    };

    /// <summary>Fetches a single order by its identifier.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetOrder(id), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new OrderResponse
            {
                Id = result.Value.Id,
                Sku = result.Value.Sku,
                Quantity = result.Value.Quantity,
                PlacedAt = result.Value.PlacedAt,
                ProductId = result.Value.ProductId,
                ProductName = result.Value.ProductName,
                UnitPrice = result.Value.UnitPrice,
            })
            : result.Problem(HttpContext);
    }
}
