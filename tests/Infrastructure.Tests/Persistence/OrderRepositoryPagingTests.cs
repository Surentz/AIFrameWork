using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Persistence;

[Collection(nameof(PostgresCollection))]
public sealed class OrderRepositoryPagingTests(PostgresFixture fixture)
{
    // Every test owns a disjoint window and pages from a cursor at the TOP of its own window,
    // never from the global newest row. Infrastructure.Tests shares one Postgres container by
    // policy (tests/CLAUDE.md: one container per collection, never per class), so a test that
    // assumes it owns the newest rows is wrong by construction - the other tests in this very
    // class seed higher timestamps. Rows from other classes sort far below and are trimmed by
    // the limit.
    private static readonly DateTimeOffset Base = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A cursor positioned above everything this test seeded, and below nothing else.</summary>
    private static (DateTimeOffset PlacedAt, Guid Id) TopOf(int windowMinute) =>
        (Base.AddMinutes(windowMinute), Guid.Empty);

    private async Task<Guid> SeedAsync(string sku, DateTimeOffset placedAt)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        await new OrderRepository(context).AddAsync(
            Order.Place(id, sku, 1, placedAt), CancellationToken.None);
        await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        return id;
    }

    [Fact]
    public async Task ListAsync_ReturnsNewestFirst()
    {
        var older = await SeedAsync("SKU-OLD", Base.AddMinutes(1));
        var newer = await SeedAsync("SKU-NEW", Base.AddMinutes(2));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context)
            .ListAsync(2, TopOf(10), CancellationToken.None);

        rows.Select(o => o.Id).Should().Equal(newer, older);
    }

    [Fact]
    public async Task ListAsync_HonoursTheLimit()
    {
        await SeedAsync("SKU-A", Base.AddMinutes(11));
        await SeedAsync("SKU-B", Base.AddMinutes(12));
        await SeedAsync("SKU-C", Base.AddMinutes(13));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context)
            .ListAsync(2, TopOf(20), CancellationToken.None);

        rows.Should().HaveCount(2);
    }

    /// <summary>
    /// The row inserted between the two fetches is the whole point. A test that pages a
    /// static table passes identically under correct keyset paging and under broken offset
    /// paging, so it cannot fail for the reason its name claims.
    /// </summary>
    [Fact]
    public async Task ListAsync_WhenARowIsInsertedBetweenPages_DoesNotRepeatOrSkip()
    {
        var third = await SeedAsync("SKU-3", Base.AddMinutes(21));
        var second = await SeedAsync("SKU-2", Base.AddMinutes(22));
        var first = await SeedAsync("SKU-1", Base.AddMinutes(23));

        await using var context = fixture.CreateContext();
        var repository = new OrderRepository(context);

        var pageOne = await repository.ListAsync(1, TopOf(30), CancellationToken.None);
        pageOne.Select(o => o.Id).Should().Equal(first);

        // A newer order arrives while the caller sits on page 1, inside this test's window.
        // Under offset paging it shifts everything down and page 2 repeats what page 1 showed.
        await SeedAsync("SKU-0", Base.AddMinutes(24));

        var cursorRow = pageOne[^1];
        var pageTwo = await repository.ListAsync(
            2, (cursorRow.PlacedAt, cursorRow.Id), CancellationToken.None);

        pageTwo.Select(o => o.Id).Should().Equal(second, third);
        pageTwo.Should().NotContain(o => o.Id == first);
    }

    [Fact]
    public async Task ListAsync_WhenTwoOrdersSharePlacedAt_ReturnsBothAcrossPages()
    {
        var tie = Base.AddMinutes(31);
        var a = await SeedAsync("SKU-TIE-A", tie);
        var b = await SeedAsync("SKU-TIE-B", tie);

        await using var context = fixture.CreateContext();
        var repository = new OrderRepository(context);

        var pageOne = await repository.ListAsync(1, TopOf(40), CancellationToken.None);
        var cursorRow = pageOne[0];
        var pageTwo = await repository.ListAsync(
            1, (cursorRow.PlacedAt, cursorRow.Id), CancellationToken.None);

        new[] { pageOne[0].Id, pageTwo[0].Id }.Should().BeEquivalentTo(new[] { a, b });
    }
}
