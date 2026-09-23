using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.EntityFrameworkCore;
using Wolverine.Tracking;

namespace AiFramework.Api.IntegrationTests.EventPath;

/// <summary>
/// The claim ADR 0005 actually rests on: a message published through Wolverine's EF Core outbox
/// is held until the DbContext transaction commits, and never sent if it does not. This is what
/// DomainEventsInterceptor + the hand-built outbox provide today, so it is the guarantee any
/// replacement has to reproduce before the existing implementation can be retired.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class WolverineOutboxAtomicityTests(ApiFactory factory)
{
    [Fact]
    public async Task PublishedThroughTheOutbox_ThenNotSaved_IsNeverDelivered()
    {
        var orderId = Guid.NewGuid();
        var recorder = factory.Services.GetRequiredService<OrderPlacedNotificationRecorder>();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider
                .GetRequiredService<IDbContextOutbox<AiFrameworkDbContext>>();

            await outbox.PublishAsync(new OrderPlacedNotification(orderId, "SKU-ATOMIC-ROLLBACK", 3));

            // Deliberately no SaveChangesAndFlushMessagesAsync. The scope falls away with the
            // message published but uncommitted, which is exactly the "handler fails after
            // publishing" shape the outbox pattern exists to make safe.
        }

        recorder.WasHandled(orderId).Should().BeFalse(
            "a message published but never committed must not reach a handler");

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var stranded = await context.Set<Order>().AsNoTracking()
            .AnyAsync(o => o.Id == orderId, CancellationToken.None);
        stranded.Should().BeFalse("nothing was saved, so no order should exist either");
    }

    [Fact]
    public async Task PublishedThroughTheOutbox_ThenSaved_IsDeliveredAndTheOrderIsPersisted()
    {
        var orderId = Guid.NewGuid();
        var recorder = factory.Services.GetRequiredService<OrderPlacedNotificationRecorder>();
        var host = factory.Services.GetRequiredService<IHost>();

        // TrackActivity waits for every message the block sets in motion to be fully handled, so
        // the assertions below are deterministic rather than racing the durability agent. This is
        // what tests/CLAUDE.md's no-sleep rule requires instead of a Task.Delay.
        await host.ExecuteAndWaitAsync(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider
                .GetRequiredService<IDbContextOutbox<AiFrameworkDbContext>>();
            var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

            // AddInfrastructure's EnableRetryOnFailure (ADR 0014) means this DbContext now
            // refuses a user-initiated transaction outside an execution strategy - and
            // SaveChangesAndFlushMessagesAsync opens exactly one internally, to hold the message
            // until the order commits. Discovered by this exact test going red once
            // EnableRetryOnFailure landed ("does not support user-initiated transactions").
            // This is the shape a real handler adopting IDbContextOutbox<T> would need too, so
            // wrapping it here proves the pattern instead of hiding a test-only workaround from
            // whoever adopts it for real - see WolverineEventPath.cs's own comment on
            // UseEntityFrameworkCoreTransactions and Infrastructure/CLAUDE.md's EF rules.
            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                context.Set<Order>().Add(
                    Order.Place(orderId, Guid.NewGuid(), 2, DateTimeOffset.UtcNow, AnOrderedProduct.Any(), "SKU-ATOMIC-COMMIT"));

                await outbox.PublishAsync(new OrderPlacedNotification(orderId, "SKU-ATOMIC-COMMIT", 2));

                // One call commits the order and releases the message. That is the whole point:
                // the two cannot diverge, because there is only one transaction.
                await outbox.SaveChangesAndFlushMessagesAsync(CancellationToken.None);
            });
        }, timeoutInMilliseconds: MessageTracking.TimeoutMs);

        recorder.WasHandled(orderId).Should().BeTrue(
            "a committed message must be delivered");

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        var orderExists = await verifyContext.Set<Order>().AsNoTracking()
            .AnyAsync(o => o.Id == orderId, CancellationToken.None);
        orderExists.Should().BeTrue("the order committed in the same transaction as the message");

        // Order.Place raises OrderPlaced, so the hand-built outbox wrote its own row in that same
        // SaveChanges. Both mechanisms committing together is the clearest evidence they can run
        // side by side without either one losing an event.
        var handBuiltRowExists = await verifyContext.Outbox.AsNoTracking()
            .AnyAsync(m => m.Payload.Contains(orderId.ToString()), CancellationToken.None);
        handBuiltRowExists.Should().BeTrue(
            "the existing DomainEventsInterceptor must still capture OrderPlaced in the same commit");
    }
}
