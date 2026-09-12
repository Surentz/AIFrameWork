namespace AiFramework.Api.Products;

/// <summary>
/// Business-rule validation (non-empty, max length, price range and scale) is owned by
/// <c>CreateProductValidator</c> in the validation behavior — not duplicated here as
/// DataAnnotations. See <c>src/Api/CLAUDE.md</c>.
/// </summary>
public sealed record CreateProductRequest
{
    public required string Sku { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required decimal Price { get; init; }
}

/// <summary>
/// No Sku: it is the catalogue's business key and the domain refuses to change it. No Id
/// either — that comes from the route, so a body that disagreed with the URL could not arise.
/// </summary>
public sealed record UpdateProductRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required decimal Price { get; init; }
}

public sealed record ProductResponse
{
    public required Guid Id { get; init; }

    public required string Sku { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required decimal Price { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ProductListItemResponse
{
    public required Guid Id { get; init; }

    public required string Sku { get; init; }

    public required string Name { get; init; }

    public required decimal Price { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record ProductPageResponse
{
    public required IReadOnlyList<ProductListItemResponse> Items { get; init; }

    public required string? NextCursor { get; init; }
}
