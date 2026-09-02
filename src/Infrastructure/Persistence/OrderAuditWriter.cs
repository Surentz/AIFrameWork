using AiFramework.Application.Orders;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class OrderAudit
{
    public required Guid MessageId { get; init; }

    public required Guid OrderId { get; init; }
}

public sealed class OrderAuditWriter(AiFrameworkDbContext context) : IOrderAuditWriter
{
    // Amendment 2: the plan's version was check-then-insert (AnyAsync, then Add +
    // SaveChangesAsync) across two separate statements with no lock spanning them. Two workers
    // processing the SAME message concurrently — which OutboxPoller.ClaimAsync's lease-reclaim
    // clause makes a real scenario, not a hypothetical one — could both see "absent" and both
    // insert, and the loser would throw a DbUpdateException on the primary key.
    //
    // A single INSERT ... ON CONFLICT DO NOTHING is one statement, so Postgres resolves the
    // race itself: the second writer's insert blocks behind the first's uncommitted row (or,
    // if the first already committed, simply finds the conflict) and silently does nothing
    // either way. See OrderAuditWriterTests for the concurrency proof.
    //
    // Table and column names are quoted exactly as EF's default (PascalCase) convention
    // generates them — OutboxPoller's raw SQL hit the same hazard: an unquoted identifier is
    // folded to lower case by Postgres and silently fails to match the migration's columns.
    public Task RecordAsync(Guid messageId, Guid orderId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO order_audit ("MessageId", "OrderId") VALUES ({messageId}, {orderId})
            ON CONFLICT ("MessageId") DO NOTHING
            """,
            cancellationToken);
}
