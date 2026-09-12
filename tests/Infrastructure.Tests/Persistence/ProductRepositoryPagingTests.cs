using AiFramework.Domain.Products;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Persistence;

[Collection(nameof(PostgresCollection))]
public sealed class ProductRepositoryPagingTests(PostgresFixture fixture)
{
    // Isolation here is weaker than OrderRepositoryPagingTests', and necessarily so: that class
    // can lean on ListAsync's UserId filter to exclude every other test's rows outright, and the
    // catalogue has no owner column to filter on. One Postgres container is shared by policy
    // (tests/CLAUDE.md: one container per collection, never per class), so every test in this
    // class must isolate itself by POSITION alone:
    //
    //   * Base is far in the future, above the 2026 timestamps ProductRepositoryTests seeds, so
    //     those rows always sort below these.
    //   * Each test takes a disjoint minute window and pages from a cursor at the TOP of its own
    //     window, which excludes every row another test seeded above it.
    //   * Each test asks for no more rows than it seeded, which stops the page reaching past the
    //     bottom of its window into an older test's rows.
    //
    // Break the third rule and a test starts asserting on rows it does not own. That is the one
    // that is easy to break by accident, so the limit in each test below is deliberate.
    private static readonly DateTimeOffset Base = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A cursor positioned above everything this test seeded, and below nothing else.</summary>
    private static (DateTimeOffset CreatedAt, Guid Id) TopOf(int windowMinute) =>
        (Base.AddMinutes(windowMinute), Guid.Empty);

    // Fixed, not Guid.NewGuid(): deterministic ids make the expected page contents and order
    // predictable ahead of time. Within a window High always sorts above Low under Postgres's
    // own uuid ordering, so a tie on CreatedAt resolves the same way on every run.
    //
    // Per window rather than two literals shared by the class, because the id is the PRIMARY KEY
    // and one Postgres container is shared by every test in this collection: a second test
    // seeding the same literal pair collides on PK_products instead of isolating anything.
    private static Guid LowIn(int windowMinute) =>
        Guid.Parse($"00000000-0000-0000-0000-{windowMinute:D12}");

    private static Guid HighIn(int windowMinute) =>
        Guid.Parse($"ffffffff-ffff-ffff-ffff-{windowMinute:D12}");

    /// <summary>
    /// Unique per row and deliberately independent of the id: the sku carries its own global
    /// unique index, so a sku derived from a fixed id would collide across windows in exactly
    /// the way a reused id does, just on a different constraint.
    /// </summary>
    private static string NewSku() => Product.NormalizeSku($"SKU-{Guid.NewGuid():N}"[..20]);

    private Task<Guid> SeedAsync(DateTimeOffset createdAt) =>
        SeedAsync(createdAt, Guid.NewGuid());

    private async Task<Guid> SeedAsync(DateTimeOffset createdAt, Guid id)
    {
        await using var context = fixture.CreateContext();
        await new ProductRepository(context).AddAsync(
            Product.Create(id, NewSku(), "Widget", null, 1m, createdAt),
            CancellationToken.None);
        await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        return id;
    }

    private async Task<IReadOnlyList<Product>> ListAsync(
        int limit, (DateTimeOffset CreatedAt, Guid Id)? after)
    {
        await using var context = fixture.CreateContext();
        return await new ProductRepository(context).ListAsync(limit, after, CancellationToken.None);
    }

    [Fact]
    public async Task ListAsync_ReturnsNewestFirst()
    {
        const int Window = 10;
        var older = await SeedAsync(Base.AddMinutes(Window - 2));
        var newer = await SeedAsync(Base.AddMinutes(Window - 1));

        var page = await ListAsync(2, TopOf(Window));

        page.Select(p => p.Id).Should().Equal(newer, older);
    }

    [Fact]
    public async Task ListAsync_BreaksATimestampTieOnIdDescending()
    {
        const int Window = 20;
        var tied = Base.AddMinutes(Window - 1);
        var low = LowIn(Window);
        var high = HighIn(Window);
        await SeedAsync(tied, low);
        await SeedAsync(tied, high);

        var page = await ListAsync(2, TopOf(Window));

        page.Select(p => p.Id).Should().Equal(high, low);
    }

    [Fact]
    public async Task ListAsync_HonoursTheLimit()
    {
        const int Window = 30;
        await SeedAsync(Base.AddMinutes(Window - 1));
        await SeedAsync(Base.AddMinutes(Window - 2));
        await SeedAsync(Base.AddMinutes(Window - 3));

        var page = await ListAsync(2, TopOf(Window));

        page.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListAsync_FromACursor_ExcludesTheCursorRowItself()
    {
        // The boundary that decides whether page 2 repeats page 1's last row.
        const int Window = 40;
        var first = await SeedAsync(Base.AddMinutes(Window - 1));
        var second = await SeedAsync(Base.AddMinutes(Window - 2));

        var page = await ListAsync(1, (Base.AddMinutes(Window - 1), first));

        page.Select(p => p.Id).Should().Equal(second);
    }

    [Fact]
    public async Task ListAsync_FromACursorOnATie_ContinuesBelowTheCursorId()
    {
        // The tie-break has to apply to the cursor comparison too, not just the ordering:
        // comparing on the timestamp alone would skip the second row of a tied pair.
        const int Window = 50;
        var tied = Base.AddMinutes(Window - 1);
        var low = LowIn(Window);
        var high = HighIn(Window);
        await SeedAsync(tied, low);
        await SeedAsync(tied, high);

        var page = await ListAsync(1, (tied, high));

        page.Select(p => p.Id).Should().Equal(low);
    }

    [Fact]
    public async Task ListAsync_PagedEndToEnd_VisitsEveryRowExactlyOnce()
    {
        const int Window = 60;
        var seeded = new List<Guid>();
        for (var i = 1; i <= 6; i++)
        {
            seeded.Add(await SeedAsync(Base.AddMinutes(Window - i)));
        }

        var seen = new List<Guid>();
        (DateTimeOffset CreatedAt, Guid Id)? cursor = TopOf(Window);
        for (var page = 0; page < 3; page++)
        {
            var rows = await ListAsync(2, cursor);
            seen.AddRange(rows.Select(p => p.Id));
            if (rows.Count == 0)
            {
                break;
            }

            cursor = (rows[^1].CreatedAt, rows[^1].Id);
        }

        // Newest first, so the seeding order (newest seeded first) is the order back out.
        seen.Should().Equal(seeded);
    }
}
