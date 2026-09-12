using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Products;

public sealed record ProductListItem(
    Guid Id, string Sku, string Name, decimal Price, DateTimeOffset CreatedAt);

public sealed record ProductPage(IReadOnlyList<ProductListItem> Items, string? NextCursor);

public sealed record GetProducts(int Limit, string? Cursor) : IQuery<ProductPage>, ICacheable
{
    // Same construction as GetOrders, and for the same reasons: the user scope is NOT here (the
    // behavior prepends it), and "N" is the null case's WHOLE fragment while every non-null case
    // is prefixed "C", so no client-supplied cursor can collide with the first-page key.
    public string CacheKey => $"{Limit}:{(Cursor is null ? "N" : $"C{Cursor}")}";

    // Thirty seconds, matching GetOrders. The catalogue is global but the cache is scoped per
    // caller, and IInvalidatesCache eviction reaches only the writer — so for everyone else this
    // TTL, not the eviction, is what bounds how long an edit stays invisible. Shortening it is
    // the lever if that ever matters; an unscoped cache is not, because the scoping is what
    // keeps one caller's entries out of another's reach everywhere else in the app.
    public TimeSpan Duration => TimeSpan.FromSeconds(30);
}

/// <summary>
/// Validates its own inputs, because QueryDispatcher does not run the validation behavior —
/// that is command-only. A malformed cursor is a 400, never a 500.
/// </summary>
public sealed class GetProductsHandler(IProductRepository products)
    : IQueryHandler<GetProducts, ProductPage>
{
    private const int MaxLimit = 100;

    public async Task<Result<ProductPage>> HandleAsync(
        GetProducts query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit is < 1 or > MaxLimit)
        {
            return Result.Failure<ProductPage>(new Error(
                ErrorKind.Validation, "products.limit_out_of_range",
                $"Limit must be between 1 and {MaxLimit}."));
        }

        (DateTimeOffset CreatedAt, Guid Id)? after = null;
        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var decoded))
            {
                return Result.Failure<ProductPage>(new Error(
                    ErrorKind.Validation, "products.malformed_cursor",
                    "The cursor could not be parsed."));
            }

            after = decoded;
        }

        // One more than asked for, so "is there a next page" needs no second COUNT.
        var rows = await products
            .ListAsync(query.Limit + 1, after, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit)
            .Select(p => new ProductListItem(p.Id, p.Sku, p.Name, p.Price, p.CreatedAt))
            .ToArray();

        var next = hasMore
            ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id)
            : null;

        return Result.Success(new ProductPage(page, next));
    }
}
