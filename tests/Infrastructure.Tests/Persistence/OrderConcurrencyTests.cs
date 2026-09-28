using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// The buyer's Cancel and the operator's Ship racing on one order, against real Postgres - the
/// token is the system column xmin, which no in-memory provider has. Each side reads the order in
/// its own context, as two requests would, before either one saves.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class OrderConcurrencyTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2096, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task<Order> SeedAsync()
    {
        await using var context = fixture.CreateContext();
        var order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1, Now, AnOrderedProduct.Any(), "SKU-C");
        await new OrderRepository(context).AddAsync(order, CancellationToken.None);
        await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        return order;
    }

    /// <summary>The buyer's read: owner-scoped and tracked, as CancelOrder makes it.</summary>
    private static async Task<Order> AsBuyerAsync(AiFrameworkDbContext context, Order seeded)
    {
        var order = await new OrderRepository(context)
            .GetForUpdateAsync(seeded.Id, seeded.UserId, CancellationToken.None);
        order.Should().NotBeNull();
        return order;
    }

    /// <summary>The operator's read: cross-owner and tracked, as ShipOrder makes it.</summary>
    private static async Task<Order> AsOperatorAsync(AiFrameworkDbContext context, Order seeded)
    {
        var order = await new OrderRepository(context)
            .GetForFulfilmentAsync(seeded.Id, CancellationToken.None);
        order.Should().NotBeNull();
        return order;
    }

    [Fact]
    public async Task SaveChangesAsync_WhenAnotherWriterSavedFirst_ThrowsConcurrencyException()
    {
        var seeded = await SeedAsync();
        await using var buyer = fixture.CreateContext();
        await using var operatorContext = fixture.CreateContext();
        var buyersCopy = await AsBuyerAsync(buyer, seeded);
        var operatorsCopy = await AsOperatorAsync(operatorContext, seeded);
        buyersCopy.Cancel("Changed my mind.", Now.AddMinutes(1));
        await new UnitOfWork(buyer).SaveChangesAsync(CancellationToken.None);
        operatorsCopy.Ship(Now.AddMinutes(2));

        var act = () => new UnitOfWork(operatorContext).SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the operator read the order before the buyer's cancellation was saved");
    }

    [Fact]
    public async Task SaveChangesAsync_WhenTheLoserThrows_TheWinnersStateIsWhatRemains()
    {
        var seeded = await SeedAsync();
        await using var buyer = fixture.CreateContext();
        await using var operatorContext = fixture.CreateContext();
        var buyersCopy = await AsBuyerAsync(buyer, seeded);
        var operatorsCopy = await AsOperatorAsync(operatorContext, seeded);
        buyersCopy.Cancel("Changed my mind.", Now.AddMinutes(1));
        await new UnitOfWork(buyer).SaveChangesAsync(CancellationToken.None);
        operatorsCopy.Ship(Now.AddMinutes(2));
        await FluentActions
            .Awaiting(() => new UnitOfWork(operatorContext).SaveChangesAsync(CancellationToken.None))
            .Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var reader = fixture.CreateContext();
        var stored = await new OrderRepository(reader)
            .GetAsync(seeded.Id, seeded.UserId, CancellationToken.None);

        stored.Should().NotBeNull();
        stored.Status.Should().Be(OrderStatus.Cancelled);
        stored.ShippedAt.Should().BeNull("the losing Ship must not be half-applied");
    }

    [Fact]
    public async Task SaveChangesAsync_TwiceInOneContext_SucceedsBecauseTheTokenIsReadBack()
    {
        var seeded = await SeedAsync();
        await using var context = fixture.CreateContext();
        var order = await AsOperatorAsync(context, seeded);
        order.Ship(Now.AddMinutes(1));
        await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        context.Entry(order).Property(o => o.ShippedAt).CurrentValue = Now.AddMinutes(2);

        var act = () => new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "EF must pick up the new xmin after each save, or a context's own second write " +
            "would look like somebody else's");
    }
}
