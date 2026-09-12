using AiFramework.Application.Abstractions;
using AiFramework.Domain.Products;
using FluentValidation;

namespace AiFramework.Application.Products;

/// <summary>
/// Carries the whole editable state, not a patch. <see cref="Product.Sku"/> is absent on
/// purpose — it is the catalogue's business key and the domain refuses to change it.
/// </summary>
public sealed record UpdateProduct(Guid Id, string Name, string? Description, decimal Price)
    : ICommand<bool>, IInvalidatesCache
{
    // Same scoping caveat as CreateProduct: this reaches the editing caller's entries only.
    public IReadOnlyList<string> Tags => [nameof(GetProducts), nameof(GetProduct)];
}

public sealed class UpdateProductValidator : AbstractValidator<UpdateProduct>
{
    public UpdateProductValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Product.MaxNameLength);
        RuleFor(c => c.Description).MaximumLength(Product.MaxDescriptionLength);
        RuleFor(c => c.Price).InclusiveBetween(0m, Product.MaxPrice);
        RuleFor(c => c.Price)
            .Must(price => decimal.Round(price, Product.PriceScale) == price)
            .WithMessage($"Price cannot have more than {Product.PriceScale} decimal places.");
    }
}

public sealed class UpdateProductHandler(IProductRepository products, IClock clock)
    : ICommandHandler<UpdateProduct, bool>
{
    public async Task<Result<bool>> HandleAsync(
        UpdateProduct command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // GetForUpdateAsync, not GetAsync: the mutation below is persisted by the unit of work
        // only because this read tracks the entity. A no-tracking read here would leave the
        // command reporting success while nothing was written.
        var product = await products
            .GetForUpdateAsync(command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.NotFound, "product.not_found", $"No product with id '{command.Id}'."));
        }

        product.Update(command.Name, command.Description, command.Price, clock.UtcNow);

        return Result.Success(true);
    }
}
