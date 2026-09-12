using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Persistence;

[Collection(nameof(PostgresCollection))]
public sealed class OrderRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddAsync_ThenSaveChanges_PersistsTheOrder()
    {
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            var repository = new OrderRepository(context);
            await repository.AddAsync(
                Order.Place(id, owner, 5, PlacedAt, AnOrderedProduct.Any(), "SKU-1"), CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify).GetAsync(id, owner, CancellationToken.None);

        found.Should().NotBeNull();
        found.Sku.Should().Be("SKU-1");
        found.Quantity.Should().Be(5);
        found.PlacedAt.Should().Be(PlacedAt);
    }

    [Fact]
    public async Task AddAsync_WithoutSaveChanges_PersistsNothing()
    {
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            var repository = new OrderRepository(context);
            await repository.AddAsync(Order.Place(id, owner, 1, PlacedAt, AnOrderedProduct.Any(), "SKU-2"), CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify).GetAsync(id, owner, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenTheOrderIsMissing_ReturnsNull()
    {
        var owner = Guid.NewGuid();
        await using var context = fixture.CreateContext();

        var found = await new OrderRepository(context).GetAsync(Guid.NewGuid(), owner, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenTheOrderBelongsToAnotherUser_ReturnsNull()
    {
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            await new OrderRepository(context).AddAsync(
                Order.Place(id, owner, 1, PlacedAt, AnOrderedProduct.Any(), "SKU-PRIVATE"), CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify).GetAsync(id, stranger, CancellationToken.None);

        // Null, not the order: indistinguishable from an id that was never issued, which is
        // what makes the endpoint answer 404 rather than 403.
        found.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_ThenSaveChanges_PersistsTheProductSnapshot()
    {
        var snapshot = new OrderedProduct(Guid.NewGuid(), "Widget", 19.95m);
        var order = Order.Place(
            Guid.NewGuid(), Guid.NewGuid(), 2, PlacedAt, snapshot, "SKU-SNAPSHOT");

        await using (var context = fixture.CreateContext())
        {
            await new OrderRepository(context).AddAsync(order, CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify)
            .GetAsync(order.Id, order.UserId, CancellationToken.None);

        found.Should().NotBeNull();
        found.Product.Should().Be(snapshot);
    }

    [Fact]
    public async Task AnOrderRowWithNoSnapshot_MaterializesWithANullProduct()
    {
        // The rows that predate the catalogue link. Written as raw SQL because the domain can no
        // longer construct one - which is exactly the invariant Task 2 installed.
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using (var seed = fixture.CreateContext())
        {
            await seed.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO orders ("Id", "UserId", "Sku", "Quantity", "PlacedAt")
                 VALUES ({id}, {userId}, 'LEGACY', 1, {PlacedAt})
                 """);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify).GetAsync(id, userId, CancellationToken.None);

        found.Should().NotBeNull();
        found.Product.Should().BeNull();
        found.Sku.Should().Be("LEGACY");
    }
}
