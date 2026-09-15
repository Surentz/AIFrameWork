using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine.EntityFrameworkCore;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace AiFramework.Api.IntegrationTests.EventPath;

/// <summary>
/// Why <c>JobScheduler</c> publishes through the plain <c>IMessageBus</c> and NOT through
/// <c>IDbContextOutbox&lt;AiFrameworkDbContext&gt;</c> — which is the obvious thing to reach for,
/// and the thing someone will reach for again.
///
/// <para>
/// Three cases, isolating one variable each:
/// </para>
/// <list type="table">
/// <item><term>A</term><description>publish OUTSIDE an execution strategy, then plain
/// SaveChanges — <b>throws</b>, because publishing ENROLLS the DbContext and so opens a
/// user-initiated transaction that <c>NpgsqlRetryingExecutionStrategy</c> refuses</description></item>
/// <item><term>B</term><description>publish INSIDE an execution strategy, then plain
/// SaveChanges — gets past that, and <b>persists no envelope at all</b></description></item>
/// <item><term>C</term><description>publish INSIDE an execution strategy, then
/// <c>SaveChangesAndFlushMessagesAsync</c> — the control: persists and delivers</description></item>
/// </list>
///
/// <para>
/// Together, A and B are the finding: adopting the EF Core outbox for jobs would require changing
/// every command in the system — wrapping the unit-of-work commit in an execution strategy AND
/// swapping in <c>SaveChangesAndFlushMessagesAsync</c>. ADR 0016 refused that, and
/// <c>IJobScheduler</c>'s own remarks carry the consequence callers must know: an enqueue is not
/// transactional with the caller's work, so a job that must not be lost is enqueued from a domain
/// event handler instead.
/// </para>
///
/// <para>
/// Kept rather than deleted with the spike that produced it, for the reason
/// <c>WolverineLocalQueueDurabilityTests</c> is kept: it pins behaviour that is invisible at every
/// call site and would otherwise be rediscovered the hard way. Each case asserts its hypothesis,
/// so a failure message carries the finding rather than needing the console read.
/// </para>
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class JobEnqueueMechanismTests(ApiFactory factory, ITestOutputHelper output)
{
    /// <summary>
    /// A. Hypothesis: resolving the outbox and publishing ENROLLS the DbContext, which opens a
    /// user-initiated transaction — so the plain SaveChangesAsync that UnitOfWork issues throws
    /// before any envelope question can even be asked.
    /// </summary>
    [Fact]
    public async Task A_PublishOutsideStrategy_ThenPlainSaveChanges_Throws()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider
            .GetRequiredService<IDbContextOutbox<AiFrameworkDbContext>>();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        await outbox.PublishAsync(
            new OrderPlacedNotification(Guid.NewGuid(), "SKU-JOBS-A", 1));

        var act = async () => await context.SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not support user-initiated transactions*",
                "HYPOTHESIS: enrolling the DbContext in the Wolverine outbox opens a transaction, " +
                "so UnitOfWork's plain SaveChangesAsync cannot commit it");
    }

    /// <summary>
    /// B. Hypothesis: doing the same work INSIDE the execution strategy gets past the transaction
    /// complaint — but a plain SaveChangesAsync still persists no envelope, because persisting
    /// outgoing envelopes is what SaveChangesAndFlushMessagesAsync adds.
    /// </summary>
    [Fact]
    public async Task B_PublishInsideStrategy_ThenPlainSaveChanges_PersistsNoEnvelope()
    {
        var before = await CountOutgoingAsync("B BEFORE");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                var outbox = scope.ServiceProvider
                    .GetRequiredService<IDbContextOutbox<AiFrameworkDbContext>>();

                await outbox.PublishAsync(
                    new OrderPlacedNotification(Guid.NewGuid(), "SKU-JOBS-B", 1));

                await context.SaveChangesAsync(CancellationToken.None);
            });
        }

        var after = await CountOutgoingAsync("B AFTER plain SaveChangesAsync");

        after.Should().Be(
            before,
            "HYPOTHESIS: a plain SaveChangesAsync persists no Wolverine envelope. If this FAILS, " +
            "the envelope WAS persisted and IJobScheduler can use the outbox provided the commit " +
            "runs inside an execution strategy");
    }

    /// <summary>C. The control: the supported call persists and delivers.</summary>
    [Fact]
    public async Task C_PublishInsideStrategy_ThenFlushingSaveChanges_Delivers()
    {
        var orderId = Guid.NewGuid();
        var recorder = factory.Services.GetRequiredService<OrderPlacedNotificationRecorder>();
        var host = factory.Services.GetRequiredService<IHost>();

        // TrackActivity waits for every message the block sets in motion to be fully handled.
        // Without it this asserts before the durable local queue has run the handler — which is
        // exactly how this spike first "failed", with wolverine_incoming_envelopes = 1.
        await host.ExecuteAndWaitAsync(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                var outbox = scope.ServiceProvider
                    .GetRequiredService<IDbContextOutbox<AiFrameworkDbContext>>();

                await outbox.PublishAsync(
                    new OrderPlacedNotification(orderId, "SKU-JOBS-C", 1));

                await outbox.SaveChangesAndFlushMessagesAsync(CancellationToken.None);
            });
        });

        await CountOutgoingAsync("C AFTER SaveChangesAndFlushMessagesAsync");

        recorder.WasHandled(orderId).Should().BeTrue(
            "the supported call commits and releases the message in one step");
    }

    /// <summary>
    /// Dumps every table in the wolverine schema with its row count — so the spike discovers the
    /// table names rather than assuming them — and returns the total across the outgoing tables.
    /// </summary>
    private async Task<long> CountOutgoingAsync(string label)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        var tables = new List<string>();
        await using (var command = new NpgsqlCommand(
            "select table_name from information_schema.tables where table_schema = 'wolverine' order by table_name",
            connection))
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                tables.Add(reader.GetString(0));
            }
        }

        output.WriteLine($"[JOBS] --- {label} ---");

        long outgoing = 0;

        foreach (var table in tables)
        {
            await using var countCommand = new NpgsqlCommand(
                $"select count(*) from wolverine.\"{table}\"", connection);
            var count = (long)(await countCommand.ExecuteScalarAsync(CancellationToken.None))!;
            output.WriteLine($"[JOBS]   wolverine.{table} = {count}");

            if (table.Contains("outgoing", StringComparison.OrdinalIgnoreCase))
            {
                outgoing += count;
            }
        }

        output.WriteLine($"[JOBS]   => outgoing total = {outgoing}");
        return outgoing;
    }
}
