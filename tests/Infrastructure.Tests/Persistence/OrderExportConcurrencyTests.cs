using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// Two deliveries of one build job racing, against real Postgres: the token is the system column
/// xmin, which no in-memory provider has. Each side reads the export in its own context, as two
/// concurrently running jobs would, before either saves.
/// </summary>
/// <remarks>
/// <see cref="OrderExport.Complete"/> is a no-op on a Ready export, but that only protects a second
/// delivery that runs AFTER the first committed. Without the token, two that overlap both see it
/// Requested, both complete it, and two order_export.completed rows mean two "ready" notifications.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class OrderExportConcurrencyTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2097, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task<OrderExport> SeedAsync()
    {
        await using var context = fixture.CreateContext();
        var export = OrderExport.Request(Guid.NewGuid(), Guid.NewGuid(), Now);
        export.ClearDomainEvents();
        context.OrderExports.Add(export);
        await context.SaveChangesAsync();
        return export;
    }

    private static async Task<OrderExport> ReadForUpdateAsync(AiFrameworkDbContext context, OrderExport seeded)
    {
        var export = await new OrderExportRepository(context)
            .GetForUpdateAsync(seeded.Id, seeded.UserId, CancellationToken.None);
        export.Should().NotBeNull();
        return export;
    }

    [Fact]
    public async Task SaveChangesAsync_WhenAnotherBuildCompletedFirst_ThrowsConcurrencyException()
    {
        var seeded = await SeedAsync();
        await using var first = fixture.CreateContextWithOutbox();
        await using var second = fixture.CreateContextWithOutbox();
        var firstCopy = await ReadForUpdateAsync(first, seeded);
        var secondCopy = await ReadForUpdateAsync(second, seeded);
        firstCopy.Complete("first", 1, Now.AddMinutes(1));
        await new UnitOfWork(first).SaveChangesAsync(CancellationToken.None);
        secondCopy.Complete("second", 2, Now.AddMinutes(2));

        var act = () => new UnitOfWork(second).SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the second build read the export before the first one's completion was saved");
    }

    [Fact]
    public async Task SaveChangesAsync_WhenTwoBuildsRace_LeavesOneCompletedEventAndTheFirstFile()
    {
        var seeded = await SeedAsync();
        await using (var first = fixture.CreateContextWithOutbox())
        await using (var second = fixture.CreateContextWithOutbox())
        {
            var firstCopy = await ReadForUpdateAsync(first, seeded);
            var secondCopy = await ReadForUpdateAsync(second, seeded);
            firstCopy.Complete("first", 1, Now.AddMinutes(1));
            await new UnitOfWork(first).SaveChangesAsync(CancellationToken.None);
            secondCopy.Complete("second", 2, Now.AddMinutes(2));
            await new UnitOfWork(second).Invoking(u => u.SaveChangesAsync(CancellationToken.None))
                .Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        await using var verify = fixture.CreateContext();
        var id = seeded.Id.ToString();
        (await verify.Outbox.AsNoTracking()
                .CountAsync(m => m.EventName == "order_export.completed" && m.Payload.Contains(id)))
            .Should().Be(1, "only the build that saved may announce the export");
        (await verify.OrderExports.AsNoTracking().SingleAsync(e => e.Id == seeded.Id))
            .Content.Should().Be("first");
    }
}
