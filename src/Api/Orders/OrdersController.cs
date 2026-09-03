using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Orders;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(
    ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
{
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
                })],
                NextCursor = result.Value.NextCursor,
            })
            : result.Problem(HttpContext);
    }

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
            })
            : result.Problem(HttpContext);
    }
}
