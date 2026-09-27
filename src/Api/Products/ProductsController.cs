using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Products;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Products;

/// <summary>
/// The catalogue. [Authorize] rather than [AllowAnonymous] even for the reads, for two reasons:
/// the catalogue is not public data, and both queries are ICacheable — the caching behavior
/// throws when one is dispatched with no caller to scope its key to.
/// </summary>
/// <remarks>
/// Reading is any signed-in caller's; writing is <see cref="AuthorizationPolicies.Catalogue"/>'s
/// <c>Manage</c>, per ACTION rather than on the controller, because the reads here are the whole
/// shop's and must stay open to members. A write action added later therefore has to carry the
/// policy itself — the opposite default from the admin-only controllers. See ADR 0025.
/// </remarks>
[ApiController]
[Route("api/products")]
[Authorize]
public sealed class ProductsController(
    ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
{
    /// <summary>Adds a product to the catalogue and returns its new identifier.</summary>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.Catalogue.Manage)]
    [ProducesResponseType<Guid>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Create(
        CreateProductRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await commands.SendAsync(
            new CreateProduct(request.Sku, request.Name, request.Description, request.Price),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? CreatedAtAction(nameof(Get), new { id = result.Value }, result.Value)
            : result.Problem(HttpContext);
    }

    /// <summary>Lists the catalogue newest first, one page at a time.</summary>
    [HttpGet]
    [ProducesResponseType<ProductPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> List(
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await queries.SendAsync(
            new GetProducts(limit, cursor), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new ProductPageResponse
            {
                Items = [.. result.Value.Items.Select(i => new ProductListItemResponse
                {
                    Id = i.Id,
                    Sku = i.Sku,
                    Name = i.Name,
                    Price = i.Price,
                    CreatedAt = i.CreatedAt,
                })],
                NextCursor = result.Value.NextCursor,
            })
            : result.Problem(HttpContext);
    }

    /// <summary>Fetches a single product by its identifier.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetProduct(id), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new ProductResponse
            {
                Id = result.Value.Id,
                Sku = result.Value.Sku,
                Name = result.Value.Name,
                Description = result.Value.Description,
                Price = result.Value.Price,
                CreatedAt = result.Value.CreatedAt,
                UpdatedAt = result.Value.UpdatedAt,
            })
            : result.Problem(HttpContext);
    }

    /// <summary>Replaces a product's editable fields. The sku is not among them.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Catalogue.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Update(
        Guid id, UpdateProductRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await commands.SendAsync(
            new UpdateProduct(id, request.Name, request.Description, request.Price),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : result.Problem(HttpContext);
    }
}
