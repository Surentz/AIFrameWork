using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Products;

public sealed record ProductView(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    decimal Price,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GetProduct(Guid Id) : IQuery<ProductView>, ICacheable
{
    public string CacheKey => Id.ToString();

    public TimeSpan Duration => TimeSpan.FromSeconds(30);
}

public sealed class GetProductHandler(IProductRepository products)
    : IQueryHandler<GetProduct, ProductView>
{
    public async Task<Result<ProductView>> HandleAsync(
        GetProduct query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No current-user branch, unlike GetOrderHandler: the catalogue is global, so there is
        // no owner to check. The endpoint is still [Authorize]d — and has to be, because the
        // caching behavior throws when an ICacheable query is dispatched with no caller to
        // scope its key to.
        var product = await products.GetAsync(query.Id, cancellationToken).ConfigureAwait(false);

        return product is null
            ? Result.Failure<ProductView>(new Error(
                ErrorKind.NotFound, "product.not_found", $"No product with id '{query.Id}'."))
            : Result.Success(new ProductView(
                product.Id,
                product.Sku,
                product.Name,
                product.Description,
                product.Price,
                product.CreatedAt,
                product.UpdatedAt));
    }
}
