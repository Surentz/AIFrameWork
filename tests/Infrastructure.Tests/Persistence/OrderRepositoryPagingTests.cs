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
    // class seed higher timestamps. Rows from other classes are excluded by owner, not by sort
    // position - ListAsync now filters by UserId, so another class's rows never enter the result
    // set regardless of where they sort.
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

    /// <summary>
    /// One owner for every test in this class except
    /// <see cref="ListAsync_WithAnotherUsersOrdersInterleaved_ReturnsOnlyTheOwners"/>, which
    /// brings its own local owner/stranger pair instead. ListAsync now filters by UserId, so this
    /// scopes every read the other tests make to rows they themselves seeded - no other test
    /// class's rows can leak in. Within that one owner, the per-test TopOf(...) windows above are
    /// what keep those tests from seeing each other's rows.
    /// </summary>
    private static readonly Guid Owner = Guid.NewGuid();

    private Task<Guid> SeedAsync(string sku, DateTimeOffset placedAt) =>
        SeedAsync(sku, placedAt, Guid.NewGuid());

    private Task<Guid> SeedAsync(string sku, DateTimeOffset placedAt, Guid id) =>
        SeedAsync(Owner, sku, placedAt, id);

    private async Task<Guid> SeedAsync(Guid owner, string sku, DateTimeOffset placedAt, Guid id)
    {
        await using var context = fixture.CreateContext();
        await new OrderRepository(context).AddAsync(
            Order.Place(id, owner, sku, 1, placedAt), CancellationToken.None);
        await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        return id;
    }

    [Fact]
    public async Task ListAsync_WithTwoOrders_ReturnsNewestFirst()
    {
        var older = await SeedAsync("SKU-OLD", Base.AddMinutes(1));
        var newer = await SeedAsync("SKU-NEW", Base.AddMinutes(2));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context)
            .ListAsync(Owner, 2, TopOf(10), CancellationToken.None);

        rows.Select(o => o.Id).Should().Equal(newer, older);
    }

    [Fact]
    public async Task ListAsync_WithMoreRowsThanTheLimit_HonoursTheLimit()
    {
        await SeedAsync("SKU-A", Base.AddMinutes(11));
        var b = await SeedAsync("SKU-B", Base.AddMinutes(12));
        var c = await SeedAsync("SKU-C", Base.AddMinutes(13));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context)
            .ListAsync(Owner, 2, TopOf(20), CancellationToken.None);

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

        var pageOne = await repository.ListAsync(Owner, 1, TopOf(30), CancellationToken.None);
        pageOne.Select(o => o.Id).Should().Equal(first);

        // A newer order arrives while the caller sits on page 1, inside this test's window.
        // Under offset paging it shifts everything down and page 2 repeats what page 1 showed.
        await SeedAsync("SKU-0", Base.AddMinutes(24));

        var cursorRow = pageOne[^1];
        var pageTwo = await repository.ListAsync(
            Owner, 2, (cursorRow.PlacedAt, cursorRow.Id), CancellationToken.None);

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

        var pageOne = await repository.ListAsync(Owner, 1, TopOf(40), CancellationToken.None);
        // Order matters, not just membership: BeEquivalentTo would still pass with the tie-break
        // implemented backwards (ascending instead of descending on Id), since both rows would
        // still show up somewhere across the two pages either way. High comes first because
        // PlacedAt ties and the ordering is Id DESC.
        pageOne.Select(o => o.Id).Should().Equal(b);

        var cursorRow = pageOne[0];
        var pageTwo = await repository.ListAsync(
            Owner, 1, (cursorRow.PlacedAt, cursorRow.Id), CancellationToken.None);

        pageTwo.Select(o => o.Id).Should().Equal(a);
    }

    [Fact]
    public async Task ListAsync_WithNoCursorAndTwoOrders_ReturnsRowsOrderedNewestFirst()
    {
        // Every other test in this class pages from a TopOf(...) cursor. ListAsync(limit, null,
        // ct) is what the handler calls on every FIRST page - the most-exercised production
        // path - yet had no repository-level coverage at all.
        //
        // Two rows, seeded at the highest window this class seeds for Owner (minutes 50-51,
        // above every other test's window for Owner): asserting on a single seeded row would
        // pass vacuously, since BeInDescendingOrder is trivially true on a one-element (or empty)
        // result. With two known rows at the top of Owner's rows, ListAsync(2, null, ...) must
        // return exactly [newer, older] - it cannot pass under a broken or inverted ordering.
        // (The interleaving test below seeds higher windows still, minutes 61-65, but those rows
        // belong to other owners and are excluded from Owner's results by the UserId filter, not
        // by sort position - do not seed Owner above minute 51.)
        var older = await SeedAsync("SKU-HEAD-OLD", Base.AddMinutes(50));
        var newer = await SeedAsync("SKU-HEAD-NEW", Base.AddMinutes(51));

        await using var context = fixture.CreateContext();
        var rows = await new OrderRepository(context)
            .ListAsync(Owner, 2, after: null, CancellationToken.None);

        rows.Select(o => o.Id).Should().Equal(newer, older);
    }

    /// <summary>
    /// Interleaved in time, not merely present: a stranger's rows sitting BETWEEN the owner's
    /// mean a missing filter would not just add rows, it would change which rows land on which
    /// page and where the cursor points. A test that seeds the stranger's rows outside the
    /// owner's window would pass against a broken filter.
    /// </summary>
    [Fact]
    public async Task ListAsync_WithAnotherUsersOrdersInterleaved_ReturnsOnlyTheOwners()
    {
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var third = await SeedAsync(owner, "SKU-OWN-3", Base.AddMinutes(61), Guid.NewGuid());
        await SeedAsync(stranger, "SKU-STR-A", Base.AddMinutes(62), Guid.NewGuid());
        var second = await SeedAsync(owner, "SKU-OWN-2", Base.AddMinutes(63), Guid.NewGuid());
        await SeedAsync(stranger, "SKU-STR-B", Base.AddMinutes(64), Guid.NewGuid());
        var first = await SeedAsync(owner, "SKU-OWN-1", Base.AddMinutes(65), Guid.NewGuid());

        await using var context = fixture.CreateContext();
        var repository = new OrderRepository(context);

        var pageOne = await repository.ListAsync(owner, 2, TopOf(70), CancellationToken.None);
        pageOne.Select(o => o.Id).Should().Equal(first, second);

        var cursorRow = pageOne[^1];
        var pageTwo = await repository.ListAsync(
            owner, 2, (cursorRow.PlacedAt, cursorRow.Id), CancellationToken.None);

        pageTwo.Select(o => o.Id).Should().Equal(third);
    }
}
