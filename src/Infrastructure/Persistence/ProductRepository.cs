using AiFramework.Application.Products;
using AiFramework.Domain.Products;
using AiFramework.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiFramework.Infrastructure.Persistence;

public sealed class ProductRepository(AiFrameworkDbContext context) : IProductRepository
{
    public async Task<bool> TryAddAsync(Product product, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);

        await context.Products.AddAsync(product, cancellationToken).ConfigureAwait(false);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        // Only the sku index, by name, as UserRepository.TryAddAsync does for usernames. Any other
        // failure is not "that sku is taken" and must stay a 500.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ProductConfiguration.SkuIndex,
        })
        {
            // Left Added, the unit of work's commit would write it again and fail the same way.
            context.Entry(product).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>No-tracking: GetProduct only reads.</summary>
    public Task<Product?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        context.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    /// <summary>
    /// Tracked, unlike <see cref="GetAsync"/>: UpdateProduct mutates the product it loads here,
    /// and a no-tracking read would leave the change unsaved with no error.
    /// </summary>
    public Task<Product?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        context.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    /// <summary>No-tracking: the sku availability check only reads.</summary>
    public Task<Product?> GetBySkuAsync(string sku, CancellationToken cancellationToken) =>
        context.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Sku == sku, cancellationToken);

    public async Task<IReadOnlyList<Product>> ListAsync(
        int limit,
        (DateTimeOffset CreatedAt, Guid Id)? after,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var query = context.Products.AsNoTracking();

        if (after is { } cursor)
        {
            // Keyset, not offset: a row inserted while a caller sits on page 1 must not make
            // page 2 repeat what page 1 already showed. Both the comparison and the ordering run
            // as Postgres SQL, never as CLR code: EF Core translates p.Id.CompareTo(...) into
            // Postgres's own uuid comparison operator, and throws rather than silently
            // client-evaluating a clause it cannot translate. Never re-sort a page in memory -
            // that is the one thing that would put .NET's own Guid ordering in the path instead.
            query = query.Where(p => p.CreatedAt < cursor.CreatedAt
                || (p.CreatedAt == cursor.CreatedAt && p.Id.CompareTo(cursor.Id) < 0));
        }

        return await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// word_similarity finds a product's name inside a whole question ("how much is the widjet?"),
    /// which plain similarity, comparing whole strings, cannot. The function form uses no index; a
    /// catalogue of thousands is a sequential scan of milliseconds. Revisit with the &lt;% operator
    /// and a GIN trigram index past roughly 10,000 products. ADR 0036.
    /// </summary>
    public async Task<IReadOnlyList<ProductMatch>> FindAsync(
        string text, IReadOnlyList<string> skus, double minimumSimilarity, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(skus);

        var skuArray = skus.ToArray();
        var rows = await context.Database.SqlQuery<ProductMatch>($"""
            SELECT p."Id", p."Sku", p."Name", p."Price",
                   (CASE WHEN p."Sku" = ANY({skuArray}) THEN 1.0
                         ELSE word_similarity(lower(p."Name"), lower({text})) END)::double precision AS "Score"
            FROM products p
            WHERE p."Sku" = ANY({skuArray})
               OR word_similarity(lower(p."Name"), lower({text})) >= {minimumSimilarity}
            ORDER BY "Score" DESC, p."Name"
            LIMIT {limit}
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows;
    }
}
