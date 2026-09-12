using AiFramework.Domain.Products;

namespace AiFramework.Application.Products;

public interface IProductRepository
{
    public Task AddAsync(Product product, CancellationToken cancellationToken);

    /// <summary>
    /// No-tracking, for reads. Unlike <see cref="Orders.IOrderRepository.GetAsync"/> there is no
    /// owner parameter: the catalogue is global, so any signed-in caller may read any product.
    /// </summary>
    public Task<Product?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Tracked, so the caller's mutation is picked up by the unit of work. Separate from
    /// <see cref="GetAsync"/> rather than a flag, because the two differ in what the CALLER must
    /// then do: a product loaded here and mutated is saved when the command commits, while one
    /// loaded by <see cref="GetAsync"/> and mutated is discarded silently, with no error. A bool
    /// argument makes that difference easy to get wrong at a glance; two names do not.
    /// </summary>
    public Task<Product?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Keyed on the normalized form, so callers must pass <see cref="Product.NormalizeSku"/>'s
    /// output rather than the raw sku.
    /// </summary>
    public Task<Product?> GetBySkuAsync(string sku, CancellationToken cancellationToken);

    public Task<IReadOnlyList<Product>> ListAsync(
        int limit,
        (DateTimeOffset CreatedAt, Guid Id)? after,
        CancellationToken cancellationToken);
}
