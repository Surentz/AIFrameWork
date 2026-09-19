using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Products;

/// <summary>
/// A catalogue entry. Global rather than owned: unlike <see cref="Orders.Order"/> there is no
/// UserId here, because the catalogue is one shared list that every signed-in caller reads.
/// </summary>
public sealed class Product : Entity
{
    /// <summary>The longest sku <see cref="Create"/> accepts, mirrored by the EF mapping.</summary>
    public const int MaxSkuLength = 64;

    /// <summary>The longest name <see cref="Create"/> accepts, mirrored by the EF mapping.</summary>
    public const int MaxNameLength = 128;

    /// <summary>The longest description <see cref="Create"/> accepts, mirrored by the EF mapping.</summary>
    public const int MaxDescriptionLength = 1024;

    /// <summary>
    /// The most a product may cost. A business sanity guard rather than a storage limit — the
    /// column is numeric(18,2), which holds far more than this — so that a mistyped price is
    /// refused at the boundary instead of being stored and read back as real money.
    /// </summary>
    public const decimal MaxPrice = 1_000_000_000m;

    /// <summary>
    /// Decimal places the price may carry. Matches the EF mapping's scale exactly: a third
    /// decimal would be rounded away by Postgres on write, so the value read back would differ
    /// from the one the caller sent, silently. Refused here instead.
    /// </summary>
    public const int PriceScale = 2;

    private Product(
        Guid id,
        string sku,
        string name,
        string? description,
        decimal price,
        DateTimeOffset createdAt)
    {
        Id = id;
        Sku = sku;
        Name = name;
        Description = description;
        Price = price;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// The catalogue's business key, stored already normalized so the unique index is on the
    /// value itself. Set once: <see cref="Update"/> deliberately cannot change it, because a
    /// sku is how everything outside the catalogue — an order line, a price list, a label on a
    /// shelf — refers to this product. Retire a product and create a new one instead.
    /// </summary>
    public string Sku { get; private set; }

    public string Name { get; private set; }

    /// <summary>Genuinely optional: a product may have no description at all.</summary>
    public string? Description { get; private set; }

    public decimal Price { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Equal to <see cref="CreatedAt"/> until the first <see cref="Update"/>, never null, so a
    /// caller sorting or displaying it needs no coalesce.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// The one way a sku becomes a lookup key, for the same reason
    /// <see cref="Users.User.Normalize"/> exists: a caller that folds case its own way could
    /// store a sku that no later lookup matches. Unlike a username, the normalized form is the
    /// only form kept — skus are conventionally upper-case, so there is no "as the user typed
    /// it" worth preserving alongside it.
    /// </summary>
    public static string NormalizeSku(string sku)
    {
        ArgumentNullException.ThrowIfNull(sku);

        return sku.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Handed its id and timestamp rather than reading a clock, the same reason
    /// <see cref="Orders.Order.Place"/> is.
    /// </summary>
    public static Product Create(
        Guid id,
        string sku,
        string name,
        string? description,
        decimal price,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new DomainException("A product needs a sku.");
        }

        var normalized = NormalizeSku(sku);

        if (normalized.Length > MaxSkuLength)
        {
            throw new DomainException($"A sku cannot be longer than {MaxSkuLength} characters.");
        }

        ValidateName(name);
        ValidateDescription(description);
        ValidatePrice(price);

        return new Product(id, normalized, name.Trim(), NormalizeDescription(description), price, createdAt);
    }

    /// <summary>
    /// Changes everything about a product except its <see cref="Sku"/>. Takes the whole editable
    /// state rather than one field at a time: a partial update would need every argument to be
    /// nullable, which makes "clear the description" and "leave the description alone"
    /// indistinguishable.
    /// </summary>
    public void Update(string name, string? description, decimal price, DateTimeOffset updatedAt)
    {
        ValidateName(name);
        ValidateDescription(description);
        ValidatePrice(price);

        var previousPrice = Price;

        Name = name.Trim();
        Description = NormalizeDescription(description);
        Price = price;
        UpdatedAt = updatedAt;

        // Only when it actually moved: an update that rewrites the name and leaves the price
        // alone must not notify anyone that the price changed. decimal's == compares numeric
        // value rather than representation, so 1.5 and 1.50 are correctly NOT a change.
        if (previousPrice != price)
        {
            Raise(new ProductPriceChanged(Id, Sku, Name, previousPrice, price));
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A product needs a name.");
        }

        if (name.Trim().Length > MaxNameLength)
        {
            throw new DomainException($"A name cannot be longer than {MaxNameLength} characters.");
        }
    }

    private static void ValidateDescription(string? description)
    {
        if (description is not null && description.Trim().Length > MaxDescriptionLength)
        {
            throw new DomainException(
                $"A description cannot be longer than {MaxDescriptionLength} characters.");
        }
    }

    private static void ValidatePrice(decimal price)
    {
        if (price < 0m)
        {
            throw new DomainException("A price cannot be negative.");
        }

        if (price > MaxPrice)
        {
            throw new DomainException($"A price cannot be more than {MaxPrice}.");
        }

        if (decimal.Round(price, PriceScale) != price)
        {
            throw new DomainException(
                $"A price cannot have more than {PriceScale} decimal places.");
        }
    }

    /// <summary>
    /// Whitespace-only collapses to null, so "absent" has exactly one representation. Without
    /// this a product could be stored with a description of "   ", which every caller would then
    /// have to treat as present-but-empty.
    /// </summary>
    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}
