using AiFramework.Domain.Products;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Persistence;

[Collection(nameof(PostgresCollection))]
public sealed class ProductRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Unique per call, because the catalogue is global: every test in this class shares one
    /// database with every other, and a fixed sku would make them collide on the unique index.
    /// </summary>
    private static string NewSku() => $"SKU-{Guid.NewGuid():N}"[..20];

    private static Product NewProduct(
        string sku, string name = "Widget", decimal price = 9.99m, int minutesOld = 0) =>
        Product.Create(
            Guid.NewGuid(), sku, name, "A widget.", price, CreatedAt.AddMinutes(-minutesOld));

    private async Task<Guid> PersistAsync(Product product)
    {
        await using var context = fixture.CreateContext();
        await new ProductRepository(context).AddAsync(product, CancellationToken.None);
        await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        return product.Id;
    }

    [Fact]
    public async Task AddAsync_ThenSaveChanges_PersistsEveryColumn()
    {
        var sku = NewSku();
        var id = await PersistAsync(NewProduct(sku, "Widget", 19.95m));

        await using var verify = fixture.CreateContext();
        var found = await new ProductRepository(verify).GetAsync(id, CancellationToken.None);

        found.Should().NotBeNull();
        found.Sku.Should().Be(sku);
        found.Name.Should().Be("Widget");
        found.Description.Should().Be("A widget.");
        found.Price.Should().Be(19.95m);
        found.CreatedAt.Should().Be(CreatedAt);
        found.UpdatedAt.Should().Be(CreatedAt);
    }

    [Fact]
    public async Task AddAsync_WithoutSaveChanges_PersistsNothing()
    {
        var product = NewProduct(NewSku());
        await using (var context = fixture.CreateContext())
        {
            await new ProductRepository(context).AddAsync(product, CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new ProductRepository(verify).GetAsync(product.Id, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenTheProductIsMissing_ReturnsNull()
    {
        await using var context = fixture.CreateContext();

        var found = await new ProductRepository(context)
            .GetAsync(Guid.NewGuid(), CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task GetBySkuAsync_FindsTheProduct()
    {
        var sku = NewSku();
        var id = await PersistAsync(NewProduct(sku));

        await using var context = fixture.CreateContext();
        var found = await new ProductRepository(context).GetBySkuAsync(sku, CancellationToken.None);

        found.Should().NotBeNull();
        found.Id.Should().Be(id);
    }

    [Fact]
    public async Task SavingTwoProductsWithOneSku_IsRefusedByTheUniqueIndex()
    {
        // The real guard behind CreateProductHandler's check-then-insert, which two concurrent
        // creates both pass. Without this index the second write would simply succeed and the
        // catalogue would hold two products under one sku.
        var sku = NewSku();
        await PersistAsync(NewProduct(sku));

        var act = async () => await PersistAsync(NewProduct(sku, "Duplicate"));

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task GetAsync_ReturnsAnUntrackedProduct()
    {
        // Mutating what GetAsync returns must NOT be persisted - that is the whole reason
        // GetForUpdateAsync exists as a separate method.
        var id = await PersistAsync(NewProduct(NewSku()));

        await using (var context = fixture.CreateContext())
        {
            var found = await new ProductRepository(context).GetAsync(id, CancellationToken.None);
            found!.Update("Renamed", null, 1m, CreatedAt.AddDays(1));
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var reread = await new ProductRepository(verify).GetAsync(id, CancellationToken.None);

        reread!.Name.Should().Be("Widget");
    }

    [Fact]
    public async Task GetForUpdateAsync_ReturnsATrackedProduct()
    {
        var id = await PersistAsync(NewProduct(NewSku()));

        await using (var context = fixture.CreateContext())
        {
            var found = await new ProductRepository(context)
                .GetForUpdateAsync(id, CancellationToken.None);
            found!.Update("Renamed", null, 24.50m, CreatedAt.AddDays(1));
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var reread = await new ProductRepository(verify).GetAsync(id, CancellationToken.None);

        reread!.Name.Should().Be("Renamed");
        reread.Description.Should().BeNull();
        reread.Price.Should().Be(24.50m);
        reread.UpdatedAt.Should().Be(CreatedAt.AddDays(1));
    }

    [Fact]
    public async Task ListAsync_WithALimitBelowOne_Throws()
    {
        await using var context = fixture.CreateContext();

        var act = async () => await new ProductRepository(context)
            .ListAsync(0, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
