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

    // Fixed, not Guid.NewGuid(): deterministic ids make the expected page contents and order
    // predictable ahead of time. High is the maximum possible uuid and Low is near the minimum,
    // so Postgres will always place High above Low when PlacedAt ties - letting the test below
    // assert the exact page split, not just that both rows appear somewhere.
    private static readonly Guid Low = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid High = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private Task<Guid> SeedAsync(string sku, DateTimeOffset placedAt) =>
        SeedAsync(sku, placedAt, Guid.NewGuid());

    private async Task<Guid> SeedAsync(string sku, DateTimeOffset placedAt, Guid id)
    {
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
        var b = await SeedAsync("SKU-B", Base.AddMinutes(12));
        var c = await SeedAsync("SKU-C", Base.AddMinutes(13));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context)
            .ListAsync(2, TopOf(20), CancellationToken.None);

        // HaveCount(2) alone passes against any broken ListAsync, since the shared container
        // always holds more than two rows regardless of ordering, cursor handling or tie-break.
        // Asserting the exact two highest ids pins ordering inside this test's own window too.
        rows.Select(o => o.Id).Should().Equal(c, b);
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
        // Low/High make the correct split predictable ahead of time - see the class-level
        // comment above their declaration.
        var a = await SeedAsync("SKU-TIE-A", tie, Low);
        var b = await SeedAsync("SKU-TIE-B", tie, High);

        await using var context = fixture.CreateContext();
        var repository = new OrderRepository(context);

        var pageOne = await repository.ListAsync(1, TopOf(40), CancellationToken.None);
        // Order matters, not just membership: BeEquivalentTo would still pass with the tie-break
        // implemented backwards (ascending instead of descending on Id), since both rows would
        // still show up somewhere across the two pages either way. High comes first because
        // PlacedAt ties and the ordering is Id DESC.
        pageOne.Select(o => o.Id).Should().Equal(b);

        var cursorRow = pageOne[0];
        var pageTwo = await repository.ListAsync(
            1, (cursorRow.PlacedAt, cursorRow.Id), CancellationToken.None);

        pageTwo.Select(o => o.Id).Should().Equal(a);
    }

    [Fact]
    public async Task ListAsync_WithNoCursor_ReturnsRowsOrderedNewestFirst()
    {
        // Every other test in this class pages from a TopOf(...) cursor. ListAsync(limit, null,
        // ct) is what the handler calls on every FIRST page - the most-exercised production
        // path - yet had no repository-level coverage at all. A property assertion, not an
        // identity one, because the shared container holds rows from every other test and this
        // class's own windows, so asserting an exact id list here would be flaky by design.
        await SeedAsync("SKU-HEAD", Base.AddMinutes(50));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context)
            .ListAsync(50, after: null, CancellationToken.None);

        rows.Should().BeInDescendingOrder(o => o.PlacedAt);
    }
}
