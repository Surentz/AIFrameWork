using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace AiFramework.Worker.IntegrationTests.Jobs;

/// <summary>
/// The export's build job on the real worker: over RabbitMQ, on the heavy lane, against real
/// Postgres, with the caller set from OwnerId. The Application tests cover its paging and failure
/// branches with ports substituted; this proves the pieces meet.
/// </summary>
[Collection(nameof(WorkerFactoryCollection))]
public sealed class OrderExportBuildTests(WorkerFactory factory)
{
    private static TrackedSessionConfiguration TrackJobs(IHost host) =>
        host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(TimeSpan.FromSeconds(30));

    private async Task PlaceAsync(Guid ownerId, string sku)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        context.Orders.Add(Order.Place(
            Guid.NewGuid(), ownerId, 2, DateTimeOffset.UtcNow.AddMinutes(-5), AnOrderedProduct.Any(), sku));
        await context.SaveChangesAsync();
    }

    private async Task<Guid> RequestAsync(Guid ownerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var export = OrderExport.Request(Guid.NewGuid(), ownerId, DateTimeOffset.UtcNow);
        context.OrderExports.Add(export);
        await context.SaveChangesAsync();
        return export.Id;
    }

    /// <summary>See JobDeliveryTests.EnqueueAsync for why this is a method, not a lambda.</summary>
    private async Task EnqueueAsync(BuildOrderExport job)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobScheduler>().EnqueueAsync(job, CancellationToken.None);
    }

    private async Task<OrderExport> ReadAsync(Guid exportId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        return await context.OrderExports.AsNoTracking().SingleAsync(e => e.Id == exportId);
    }

    [Fact]
    public async Task BuildOrderExport_StoresACsvOfTheOwnersOrdersOnly()
    {
        var ownerId = Guid.NewGuid();
        await PlaceAsync(ownerId, "SKU-EXPORT-MINE-1");
        await PlaceAsync(ownerId, "SKU-EXPORT-MINE-2");
        await PlaceAsync(Guid.NewGuid(), "SKU-EXPORT-SOMEONE-ELSES");
        var exportId = await RequestAsync(ownerId);

        var tracked = await TrackJobs(factory.Services.GetRequiredService<IHost>())
            .ExecuteAndWaitAsync(_ => EnqueueAsync(new BuildOrderExport(exportId, ownerId)));

        tracked.AllExceptions().Should().BeEmpty();
        var export = await ReadAsync(exportId);
        export.Status.Should().Be(OrderExportStatus.Ready);
        export.RowCount.Should().Be(2);
        System.Text.Encoding.UTF8.GetString(export.Document!).Should()
            .Contain("SKU-EXPORT-MINE-1").And.Contain("SKU-EXPORT-MINE-2")
            .And.NotContain("SKU-EXPORT-SOMEONE-ELSES");
    }

    /// <summary>
    /// At-least-once delivery, for real: a redelivered OrderExportRequested enqueues the build twice.
    /// The second must find the export Ready and stop, so the owner is told once and the file is the
    /// first build's. (Two builds that OVERLAP are caught by the xmin token instead —
    /// Infrastructure.Tests' OrderExportConcurrencyTests.)
    /// </summary>
    [Fact]
    public async Task BuildOrderExport_DeliveredTwice_CompletesOnceAndKeepsTheFirstFile()
    {
        var ownerId = Guid.NewGuid();
        await PlaceAsync(ownerId, "SKU-EXPORT-TWICE");
        var exportId = await RequestAsync(ownerId);
        var host = factory.Services.GetRequiredService<IHost>();
        await TrackJobs(host).ExecuteAndWaitAsync(_ => EnqueueAsync(new BuildOrderExport(exportId, ownerId)));
        var firstCompletedAt = (await ReadAsync(exportId)).CompletedAt;

        var second = await TrackJobs(host).ExecuteAndWaitAsync(_ => EnqueueAsync(new BuildOrderExport(exportId, ownerId)));

        second.AllExceptions().Should().BeEmpty();
        (await ReadAsync(exportId)).CompletedAt.Should().Be(firstCompletedAt);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var id = exportId.ToString();
        (await context.Outbox.AsNoTracking()
                .CountAsync(m => m.EventName == "order_export.completed" && m.Payload.Contains(id)))
            .Should().Be(1, "a second delivery of the build must not announce the export again");
    }

    [Fact]
    public async Task BuildOrderExport_WritesTheCompletedEventForTheApiToDeliver()
    {
        var ownerId = Guid.NewGuid();
        var exportId = await RequestAsync(ownerId);

        await TrackJobs(factory.Services.GetRequiredService<IHost>())
            .ExecuteAndWaitAsync(_ => EnqueueAsync(new BuildOrderExport(exportId, ownerId)));

        // The worker writes this row and never delivers it (ADR 0028): an API replica's pump turns
        // it into the notification, because only the API can push.
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var id = exportId.ToString();
        (await context.Outbox.AsNoTracking()
                .CountAsync(m => m.EventName == "order_export.completed" && m.Payload.Contains(id)))
            .Should().Be(1);
    }
}
