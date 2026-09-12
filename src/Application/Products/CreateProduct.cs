using AiFramework.Application.Abstractions;
using AiFramework.Domain.Products;
using FluentValidation;

namespace AiFramework.Application.Products;

public sealed record CreateProduct(string Sku, string Name, string? Description, decimal Price)
    : ICommand<Guid>, IInvalidatesCache
{
    // Both catalogue reads, because a new product changes the list and nothing else. nameof
    // rather than a literal, so renaming a query type is a compile error here instead of a
    // silent eviction that stops matching anything.
    //
    // Worth knowing what this does NOT do: the behavior scopes tags to the CALLING user, and the
    // catalogue is global, so this evicts only the creator's own cached pages. Every other
    // caller keeps theirs until the thirty seconds below lapse. That is a bounded staleness
    // accepted on purpose — see GetProducts.
    public IReadOnlyList<string> Tags => [nameof(GetProducts), nameof(GetProduct)];
}

public sealed class CreateProductValidator : AbstractValidator<CreateProduct>
{
    public CreateProductValidator()
    {
        RuleFor(c => c.Sku).NotEmpty().MaximumLength(Product.MaxSkuLength);
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Product.MaxNameLength);
        RuleFor(c => c.Description).MaximumLength(Product.MaxDescriptionLength);
        RuleFor(c => c.Price).InclusiveBetween(0m, Product.MaxPrice);
        RuleFor(c => c.Price)
            .Must(price => decimal.Round(price, Product.PriceScale) == price)
            .WithMessage($"Price cannot have more than {Product.PriceScale} decimal places.");
    }
}

public sealed class CreateProductHandler(IProductRepository products, IClock clock)
    : ICommandHandler<CreateProduct, Guid>
{
    public async Task<Result<Guid>> HandleAsync(
        CreateProduct command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Check-then-insert, so a taken sku is a friendly 409 rather than a 500. The unique
        // index on Sku is still the real guard: two simultaneous creates of the same sku both
        // pass this check, and the second one fails at SaveChanges.
        var taken = await products
            .GetBySkuAsync(Product.NormalizeSku(command.Sku), cancellationToken)
            .ConfigureAwait(false);

        if (taken is not null)
        {
            return Result.Failure<Guid>(new Error(
                ErrorKind.Conflict,
                "product.sku_taken",
                $"The sku '{Product.NormalizeSku(command.Sku)}' is already in the catalogue."));
        }

        var product = Product.Create(
            Guid.NewGuid(),
            command.Sku,
            command.Name,
            command.Description,
            command.Price,
            clock.UtcNow);

        await products.AddAsync(product, cancellationToken).ConfigureAwait(false);

        return Result.Success(product.Id);
    }
}
