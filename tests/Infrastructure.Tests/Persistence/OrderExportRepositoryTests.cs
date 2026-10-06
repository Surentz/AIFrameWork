using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// The export repository and its mapping, against the real engine. Every test owns a disjoint
/// user, because Infrastructure.Tests shares one container by policy (tests/CLAUDE.md).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class OrderExportRepositoryTests(PostgresFixture fixture)
{
    private static readonly byte[] AFile = [0x25, 0x50, 0x44, 0x46];
    private static readonly DateTimeOffset Base = new(2099, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<OrderExport> SeedAsync(
        Guid userId, DateTimeOffset? requestedAt = null, byte[]? document = null)
    {
        var export = OrderExport.Request(Guid.NewGuid(), userId, requestedAt ?? Base);
        if (document is not null)
        {
            export.Complete(document, 3, (requestedAt ?? Base).AddMinutes(1));
        }

        export.ClearDomainEvents();
        await using var context = fixture.CreateContext();
        context.OrderExports.Add(export);
        await context.SaveChangesAsync();
        return export;
    }

    [Fact]
    public async Task AddAsync_RoundTripsEveryMappedProperty()
    {
        var export = await SeedAsync(Guid.NewGuid(), document: AFile);

        await using var context = fixture.CreateContext();
        var stored = await context.OrderExports.AsNoTracking().SingleAsync(e => e.Id == export.Id);

        stored.UserId.Should().Be(export.UserId);
        stored.RequestedAt.Should().Be(Base);
        stored.Status.Should().Be(OrderExportStatus.Ready);
        stored.CompletedAt.Should().Be(Base.AddMinutes(1));
        stored.RowCount.Should().Be(3);
        stored.Document.Should().Equal(AFile);
    }

    [Fact]
    public async Task Status_IsStoredAsItsName()
    {
        var export = await SeedAsync(Guid.NewGuid());

        await using var context = fixture.CreateContext();
        var raw = await context.Database
            .SqlQuery<string>($"SELECT \"Status\" AS \"Value\" FROM order_exports WHERE \"Id\" = {export.Id}")
            .SingleAsync();

        raw.Should().Be("Requested");
    }

    [Fact]
    public async Task GetInProgressAsync_ReturnsTheOwnersUnfinishedExport()
    {
        var userId = Guid.NewGuid();
        var export = await SeedAsync(userId);

        await using var context = fixture.CreateContext();
        var found = await new OrderExportRepository(context)
            .GetInProgressAsync(userId, Base.AddMinutes(5), CancellationToken.None);

        found.Should().NotBeNull();
        found.Id.Should().Be(export.Id);
    }

    [Fact]
    public async Task GetInProgressAsync_IgnoresAnExportPastStaleAfter()
    {
        var userId = Guid.NewGuid();
        await SeedAsync(userId);

        await using var context = fixture.CreateContext();
        var found = await new OrderExportRepository(context)
            .GetInProgressAsync(userId, Base + OrderExport.StaleAfter, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task GetInProgressAsync_IgnoresAReadyExport()
    {
        var userId = Guid.NewGuid();
        await SeedAsync(userId, document: AFile);

        await using var context = fixture.CreateContext();
        var found = await new OrderExportRepository(context)
            .GetInProgressAsync(userId, Base.AddMinutes(5), CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task GetInProgressAsync_IgnoresSomeoneElsesExport()
    {
        await SeedAsync(Guid.NewGuid());

        await using var context = fixture.CreateContext();
        var found = await new OrderExportRepository(context)
            .GetInProgressAsync(Guid.NewGuid(), Base.AddMinutes(5), CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task GetForUpdateAsync_ReturnsATrackedExportThatSaves()
    {
        var export = await SeedAsync(Guid.NewGuid());

        await using (var context = fixture.CreateContext())
        {
            var tracked = await new OrderExportRepository(context)
                .GetForUpdateAsync(export.Id, export.UserId, CancellationToken.None);
            // ! is safe: the export was seeded above for exactly this owner.
            tracked!.Complete(AFile, 0, Base.AddMinutes(1));
            await context.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContext();
        (await verify.OrderExports.AsNoTracking().SingleAsync(e => e.Id == export.Id))
            .Status.Should().Be(OrderExportStatus.Ready);
    }

    [Fact]
    public async Task GetSummaryAsync_ForSomeoneElse_IsNull()
    {
        var export = await SeedAsync(Guid.NewGuid());

        await using var context = fixture.CreateContext();
        var found = await new OrderExportRepository(context)
            .GetSummaryAsync(export.Id, Guid.NewGuid(), CancellationToken.None);

        found.Should().BeNull();
    }

    /// <summary>
    /// The build job reads this on every delivery, a duplicate one included, so it must not drag the
    /// file along: a summary has nowhere to put it.
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_ReadsTheStatusOfABuiltExport()
    {
        var export = await SeedAsync(Guid.NewGuid(), document: AFile);

        await using var context = fixture.CreateContext();
        var found = await new OrderExportRepository(context)
            .GetSummaryAsync(export.Id, export.UserId, CancellationToken.None);

        found.Should().Be(new OrderExportSummary(
            export.Id, OrderExportStatus.Ready, Base, Base.AddMinutes(1), 3));
    }

    [Fact]
    public async Task ListAsync_ReturnsTheOwnersExportsNewestFirst()
    {
        var userId = Guid.NewGuid();
        var older = await SeedAsync(userId, Base);
        var newer = await SeedAsync(userId, Base.AddHours(1), document: AFile);
        await SeedAsync(Guid.NewGuid());

        await using var context = fixture.CreateContext();
        var list = await new OrderExportRepository(context).ListAsync(userId, CancellationToken.None);

        list.Select(e => e.Id).Should().Equal(newer.Id, older.Id);
        list[0].Should().Be(new OrderExportSummary(
            newer.Id, OrderExportStatus.Ready, Base.AddHours(1), Base.AddHours(1).AddMinutes(1), 3));
    }

    [Fact]
    public async Task GetFileAsync_ReturnsAReadyExportsDocument()
    {
        var export = await SeedAsync(Guid.NewGuid(), document: AFile);

        await using var context = fixture.CreateContext();
        var file = await new OrderExportRepository(context)
            .GetFileAsync(export.Id, export.UserId, CancellationToken.None);

        file!.Document.Should().Equal(AFile);
        file.RequestedAt.Should().Be(Base);
    }

    [Fact]
    public async Task GetFileAsync_OfAnUnfinishedExport_IsNull()
    {
        var export = await SeedAsync(Guid.NewGuid());

        await using var context = fixture.CreateContext();
        var file = await new OrderExportRepository(context)
            .GetFileAsync(export.Id, export.UserId, CancellationToken.None);

        file.Should().BeNull();
    }

    [Fact]
    public async Task GetFileAsync_ForSomeoneElse_IsNull()
    {
        var export = await SeedAsync(Guid.NewGuid(), document: AFile);

        await using var context = fixture.CreateContext();
        var file = await new OrderExportRepository(context)
            .GetFileAsync(export.Id, Guid.NewGuid(), CancellationToken.None);

        file.Should().BeNull();
    }
}
