using System.Text.RegularExpressions;
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Products;

namespace AiFramework.Application.Products;

/// <summary>A product found in free text, and how sure: 1 for a sku match, else pg_trgm word similarity.</summary>
public sealed record ProductMatch(Guid Id, string Sku, string Name, decimal Price, double Score);

/// <summary>
/// Products whose name appears, possibly misspelt, in <paramref name="Text"/>, or whose sku is one
/// of its words. Best first. The catalogue is global, so there is no owner to scope by. ADR 0036.
/// </summary>
public sealed record FindProducts(string Text, int Limit) : IQuery<IReadOnlyList<ProductMatch>>;

/// <summary>Validates its own inputs, as every query does (QueryDispatcher runs no validation behavior).</summary>
public sealed partial class FindProductsHandler(IProductRepository products)
    : IQueryHandler<FindProducts, IReadOnlyList<ProductMatch>>
{
    public const int MaxLimit = 10;

    public const int MaxTextLength = 2000;

    /// <summary>Below this, a name is not in the text. Tuned with the evaluation set's product questions.</summary>
    public const double MinimumSimilarity = 0.5;

    public async Task<Result<IReadOnlyList<ProductMatch>>> HandleAsync(FindProducts query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Text) || query.Text.Length > MaxTextLength)
        {
            return Result.Failure<IReadOnlyList<ProductMatch>>(new Error(
                ErrorKind.Validation, "products.search_text", $"Search text is 1 to {MaxTextLength} characters."));
        }

        if (query.Limit is < 1 or > MaxLimit)
        {
            return Result.Failure<IReadOnlyList<ProductMatch>>(new Error(
                ErrorKind.Validation, "products.limit_out_of_range", $"Limit must be between 1 and {MaxLimit}."));
        }

        IReadOnlyList<string> skus = [.. SkuShaped().Matches(query.Text).Select(m => Product.NormalizeSku(m.Value)).Distinct(StringComparer.Ordinal).Take(20)];
        var matches = await products.FindAsync(query.Text, skus, MinimumSimilarity, query.Limit, cancellationToken).ConfigureAwait(false);
        return Result.Success(matches);
    }

    [GeneratedRegex("[A-Za-z0-9][A-Za-z0-9_-]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SkuShaped();
}
