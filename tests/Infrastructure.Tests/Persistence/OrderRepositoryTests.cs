using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Persistence;

[Collection(nameof(PostgresCollection))]
public sealed class OrderRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddAsync_ThenSaveChanges_PersistsTheOrder()
    {
        var id = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            var repository = new OrderRepository(context);
            await repository.AddAsync(Order.Place(id, "SKU-1", 5, PlacedAt), CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify).GetAsync(id, CancellationToken.None);

        found.Should().NotBeNull();
        found.Sku.Should().Be("SKU-1");
        found.Quantity.Should().Be(5);
        found.PlacedAt.Should().Be(PlacedAt);
    }

    [Fact]
    public async Task AddAsync_WithoutSaveChanges_PersistsNothing()
    {
        var id = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            var repository = new OrderRepository(context);
            await repository.AddAsync(Order.Place(id, "SKU-2", 1, PlacedAt), CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify).GetAsync(id, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenTheOrderIsMissing_ReturnsNull()
    {
        await using var context = fixture.CreateContext();

        var found = await new OrderRepository(context).GetAsync(Guid.NewGuid(), CancellationToken.None);

        found.Should().BeNull();
    }
}
