using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Outbox;

[Collection(nameof(PostgresCollection))]
public sealed class OutboxAtomicityTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SaveChanges_WhenTheAggregateIsSaved_WritesAnOutboxRow()
    {
        var id = Guid.NewGuid();
        await using (var context = fixture.CreateContextWithOutbox())
        {
            context.Orders.Add(Order.Place(id, "SKU-A", 2, PlacedAt));
            await context.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContextWithOutbox();
        var row = await verify.Outbox.SingleOrDefaultAsync(m => m.Payload.Contains(id.ToString()));

        row.Should().NotBeNull();
        row.EventName.Should().Be("order.placed");
        row.Status.Should().Be(OutboxStatus.Pending);
        row.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task SaveChanges_WhenTheSaveFails_WritesNoOutboxRow()
    {
        var id = Guid.NewGuid();

        await using (var seed = fixture.CreateContextWithOutbox())
        {
            seed.Orders.Add(Order.Place(id, "SKU-B", 1, PlacedAt));
            await seed.SaveChangesAsync();
        }

        // Re-inserting the same primary key makes SaveChangesAsync fail at the database.
        // If the outbox row were written outside the aggregate's transaction, this would
        // leave a second row behind — that is exactly the bug this test exists to catch.
        await using (var clash = fixture.CreateContextWithOutbox())
        {
            clash.Orders.Add(Order.Place(id, "SKU-B", 1, PlacedAt));
            var act = async () => await clash.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verify = fixture.CreateContextWithOutbox();
        var rows = await verify.Outbox.Where(m => m.Payload.Contains(id.ToString())).ToListAsync();

        rows.Should().HaveCount(1, "the failed save must leave no additional outbox row");
    }

    [Fact]
    public async Task SaveChanges_ClearsTheAggregatesPendingEvents()
    {
        await using var context = fixture.CreateContextWithOutbox();
        var order = Order.Place(Guid.NewGuid(), "SKU-C", 1, PlacedAt);
        context.Orders.Add(order);

        await context.SaveChangesAsync();

        order.DomainEvents.Should().BeEmpty();
    }
}
