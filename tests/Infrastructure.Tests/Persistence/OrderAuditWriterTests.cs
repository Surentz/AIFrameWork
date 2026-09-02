using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// Amendment 1: the plan's only tests substituted IOrderAuditWriter, so they could not tell a
/// real insert-if-absent from a mock that just recorded calls. These run against the real
/// Postgres primary key, which is what actually makes redelivery a no-op.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class OrderAuditWriterTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RecordAsync_CalledTwiceWithTheSameMessageId_ResultsInExactlyOneRow()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using (var context = fixture.CreateContext())
        {
            var writer = new OrderAuditWriter(context);
            await writer.RecordAsync(messageId, orderId, CancellationToken.None);
            await writer.RecordAsync(messageId, orderId, CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        // Scoped to this messageId, not a whole-table count: the container is shared across
        // the whole test run and never truncated (see PostgresFixture), so other tests' rows
        // are always present alongside this one.
        (await verify.OrderAudits.CountAsync(a => a.MessageId == messageId)).Should().Be(1);
    }

    [Fact]
    public async Task RecordAsync_WithTwoDifferentMessageIdsForTheSameOrder_ResultsInTwoRows()
    {
        var firstMessageId = Guid.NewGuid();
        var secondMessageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using (var context = fixture.CreateContext())
        {
            var writer = new OrderAuditWriter(context);
            await writer.RecordAsync(firstMessageId, orderId, CancellationToken.None);
            await writer.RecordAsync(secondMessageId, orderId, CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        // Without this, a writer that silently dropped every write would still pass the
        // "exactly one row" test above.
        (await verify.OrderAudits.CountAsync(a => a.OrderId == orderId)).Should().Be(2);
    }

    [Fact]
    public async Task RecordAsync_CalledConcurrentlyWithTheSameMessageId_NeitherThrowsAndResultsInExactlyOneRow()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // Amendment 2: a sequential double-call (the two tests above) never exercises the
        // check-then-insert race — it only ever sees the row absent once. To force real
        // overlap deterministically (not just hope two Task.WhenAll branches happen to
        // interleave — see OutboxPollerTests' own history of a "disjoint sets" assertion that
        // passed vacuously because one poller won the race before the other ever contended),
        // this holds a row insert open in an uncommitted transaction on one connection, then
        // starts a second RecordAsync for the SAME messageId on a second connection and proves
        // it is still running — genuinely blocked behind the first's uncommitted row, which is
        // exactly the concurrent-redelivery scenario OutboxPoller's lease-reclaim can produce —
        // before committing the first and letting the second resolve.
        await using var holdingContext = fixture.CreateContext();
        await using var transaction = await holdingContext.Database.BeginTransactionAsync();
        await holdingContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO order_audit ("MessageId", "OrderId") VALUES ({messageId}, {orderId})""");

        await using var secondContext = fixture.CreateContext();
        var writer = new OrderAuditWriter(secondContext);
        var secondCall = writer.RecordAsync(messageId, orderId, CancellationToken.None);

        var completedEarly = await Task.WhenAny(secondCall, Task.Delay(TimeSpan.FromSeconds(2)));
        completedEarly.Should().NotBe(secondCall,
            "the second insert must block behind the first transaction's uncommitted row " +
            "rather than race past it — if this fails, the two calls never actually " +
            "contended and the rest of this test proves nothing about the race");

        await transaction.CommitAsync();

        var act = async () => await secondCall;
        await act.Should().NotThrowAsync(
            "ON CONFLICT DO NOTHING must resolve once the first transaction commits, not " +
            "throw a duplicate-key DbUpdateException on the loser");

        await using var verify = fixture.CreateContext();
        (await verify.OrderAudits.CountAsync(a => a.MessageId == messageId)).Should().Be(1);
    }
}
