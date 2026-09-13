using AiFramework.Application.Abstractions;
using AiFramework.Application.Products;
using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;
using FluentValidation;

namespace AiFramework.Application.Orders;

public sealed record PlaceOrder(string Sku, int Quantity) : ICommand<Guid>, IInvalidatesCache
{
    // Both order reads, because a new order changes the list and nothing else. nameof rather
    // than a literal, so renaming a query type is a compile error here instead of a silent
    // eviction that stops matching anything.
    public IReadOnlyList<string> Tags => [nameof(GetOrders), nameof(GetOrder)];
}

public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrder>
{
    public PlaceOrderValidator()
    {
        RuleFor(c => c.Sku).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Quantity).GreaterThan(0);
    }
}

public sealed class PlaceOrderHandler(
    IOrderRepository orders,
    IProductRepository products,
    IClock clock,
    ICurrentUser currentUser)
    : ICommandHandler<PlaceOrder, Guid>
{
    public async Task<Result<Guid>> HandleAsync(PlaceOrder command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<Guid>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        var sku = Product.NormalizeSku(command.Sku);

        // Check-then-insert with no uniqueness guard behind it, unlike CreateProduct's, whose
        // unique index is the real defence. The asymmetry is deliberate: the only race here is a
        // product deleted between this read and the insert, and the catalogue has no delete. Add
        // one and this is the site to revisit.
        var product = await products.GetBySkuAsync(sku, cancellationToken).ConfigureAwait(false);

        if (product is null)
        {
            // Validation rather than NotFound: from the caller's position this is a bad value in
            // a submitted field, and it lands beside the input as a 400, the way an invalid
            // quantity already does.
            return Result.Failure<Guid>(new Error(
                ErrorKind.Validation, "orders.unknown_sku",
                $"No product with sku '{sku}' is in the catalogue."));
        }

        var order = Order.Place(
            Guid.NewGuid(),
            userId,
            command.Quantity,
            clock.UtcNow,
            new OrderedProduct(product.Id, product.Name, product.Price),
            product.Sku);

        await orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

        return Result.Success(order.Id);
    }
}
