using AiFramework.Domain.Orders;
using AiFramework.Domain.Users;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// The operator's cross-owner reads. The shared container holds every other test's orders too, and
/// this list is NOT scoped by owner, so each test owns a disjoint time window far from anything
/// else and starts its keyset just below that window: oldest first, a page of exactly the rows it
/// seeded comes back before anything later can.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class OrderRepositoryFulfilmentTests(PostgresFixture fixture)
{
    // Distinct from OrderRepositoryPagingTests' 2099 so the two classes never share a window.
    private static readonly DateTimeOffset Base = new(2097, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A cursor just below everything a test seeded from <paramref name="windowMinute"/>.</summary>
    private static (DateTimeOffset PlacedAt, Guid Id) BottomOf(int windowMinute) =>
        (Base.AddMinutes(windowMinute), Guid.Empty);

    private async Task<Order> SeedAsync(Guid owner, DateTimeOffset placedAt, Action<Order>? advance = null)
    {
        await using var context = fixture.CreateContext();
        var order = Order.Place(Guid.NewGuid(), owner, 1, placedAt, AnOrderedProduct.Any(), "SKU-F");
        advance?.Invoke(order);
        await new OrderRepository(context).AddAsync(order, CancellationToken.None);
        await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        return order;
    }

    [Fact]
    public async Task ListForFulfilmentAsync_WithTwoOwnersInterleaved_ReturnsBothOldestFirst()
    {
        var ada = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var first = await SeedAsync(ada, Base.AddMinutes(1));
        var second = await SeedAsync(bob, Base.AddMinutes(2));
        var third = await SeedAsync(ada, Base.AddMinutes(3));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context).ListForFulfilmentAsync(
            OrderStatus.Placed, 3, BottomOf(0), CancellationToken.None);

        rows.Select(r => r.Id).Should().Equal(first.Id, second.Id, third.Id);
    }

    [Fact]
    public async Task ListForFulfilmentAsync_FiltersByStatus()
    {
        var owner = Guid.NewGuid();
        var placed = await SeedAsync(owner, Base.AddMinutes(11));
        await SeedAsync(owner, Base.AddMinutes(12), o => o.Ship(Base.AddMinutes(13)));
        await SeedAsync(owner, Base.AddMinutes(14), o => o.Cancel("Out of stock.", Base.AddMinutes(15)));
        var alsoPlaced = await SeedAsync(owner, Base.AddMinutes(16));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context).ListForFulfilmentAsync(
            OrderStatus.Placed, 2, BottomOf(10), CancellationToken.None);

        rows.Select(r => r.Id).Should().Equal(placed.Id, alsoPlaced.Id);
    }

    [Fact]
    public async Task ListForFulfilmentAsync_InTheShippedStatus_ReturnsOnlyShipped()
    {
        var owner = Guid.NewGuid();
        await SeedAsync(owner, Base.AddMinutes(21));
        var shipped = await SeedAsync(owner, Base.AddMinutes(22), o => o.Ship(Base.AddMinutes(23)));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context).ListForFulfilmentAsync(
            OrderStatus.Shipped, 1, BottomOf(20), CancellationToken.None);

        rows.Should().ContainSingle().Which.Id.Should().Be(shipped.Id);
    }

    [Fact]
    public async Task ListForFulfilmentAsync_PagingFromTheLastRow_ContinuesWithoutRepeatsOrGaps()
    {
        var ada = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            // Alternating owners, one minute apart: a keyset that leaked an owner filter in, or
            // one that compared the wrong way for an ascending list, splits this differently.
            seeded.Add((await SeedAsync(i % 2 == 0 ? ada : bob, Base.AddMinutes(31 + i))).Id);
        }

        await using var context = fixture.CreateContext();
        var repository = new OrderRepository(context);
        var page1 = await repository.ListForFulfilmentAsync(
            OrderStatus.Placed, 2, BottomOf(30), CancellationToken.None);
        var page2 = await repository.ListForFulfilmentAsync(
            OrderStatus.Placed, 2, (page1[^1].PlacedAt, page1[^1].Id), CancellationToken.None);
        var page3 = await repository.ListForFulfilmentAsync(
            OrderStatus.Placed, 1, (page2[^1].PlacedAt, page2[^1].Id), CancellationToken.None);

        page1.Concat(page2).Concat(page3).Select(r => r.Id).Should().Equal(seeded);
    }

    [Fact]
    public async Task ListForFulfilmentAsync_WithTiedTimestamps_BreaksTheTieById()
    {
        var owner = Guid.NewGuid();
        var at = Base.AddMinutes(41);
        var a = await SeedAsync(owner, at);
        var b = await SeedAsync(owner, at);

        await using var context = fixture.CreateContext();
        var repository = new OrderRepository(context);
        var page1 = await repository.ListForFulfilmentAsync(
            OrderStatus.Placed, 1, BottomOf(40), CancellationToken.None);
        var page2 = await repository.ListForFulfilmentAsync(
            OrderStatus.Placed, 1, (page1[0].PlacedAt, page1[0].Id), CancellationToken.None);

        new[] { page1[0].Id, page2[0].Id }.Should().BeEquivalentTo([a.Id, b.Id]);
    }

    [Fact]
    public async Task ListForFulfilmentAsync_JoinsTheBuyersUsername()
    {
        await using var seed = fixture.CreateContext();
        var buyer = User.Register(
            Guid.NewGuid(), $"buyer{Guid.NewGuid():N}"[..20], "hash", "Buyer", Base);
        seed.Users.Add(buyer);
        await seed.SaveChangesAsync();
        await SeedAsync(buyer.Id, Base.AddMinutes(51));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context).ListForFulfilmentAsync(
            OrderStatus.Placed, 1, BottomOf(50), CancellationToken.None);

        rows.Should().ContainSingle().Which.BuyerUsername.Should().Be(buyer.Username);
    }

    [Fact]
    public async Task ListForFulfilmentAsync_WithNoUserRow_StillReturnsTheOrder()
    {
        var order = await SeedAsync(Guid.NewGuid(), Base.AddMinutes(61));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context).ListForFulfilmentAsync(
            OrderStatus.Placed, 1, BottomOf(60), CancellationToken.None);

        var row = rows.Should().ContainSingle().Subject;
        row.Id.Should().Be(order.Id);
        row.BuyerUsername.Should().BeNull();
    }

    [Fact]
    public async Task GetForFulfilmentAsync_ReturnsAnyOwnersOrderTracked_SoAShipPersists()
    {
        var order = await SeedAsync(Guid.NewGuid(), Base.AddMinutes(71));
        var shippedAt = Base.AddMinutes(72);

        await using (var context = fixture.CreateContext())
        {
            var loaded = await new OrderRepository(context)
                .GetForFulfilmentAsync(order.Id, CancellationToken.None);
            loaded!.Ship(shippedAt);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var reread = await new OrderRepository(verify)
            .GetForFulfilmentAsync(order.Id, CancellationToken.None);
        reread!.Status.Should().Be(OrderStatus.Shipped);
        reread.ShippedAt.Should().Be(shippedAt);
    }

    [Fact]
    public async Task GetForFulfilmentAsync_WithAnUnknownId_ReturnsNull()
    {
        await using var context = fixture.CreateContext();

        var order = await new OrderRepository(context)
            .GetForFulfilmentAsync(Guid.NewGuid(), CancellationToken.None);

        order.Should().BeNull();
    }
}
