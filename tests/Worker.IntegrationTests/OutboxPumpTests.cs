using AiFramework.Infrastructure.Outbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiFramework.Worker.IntegrationTests;

/// <summary>
/// The worker writes to the outbox but never delivers from it. The pumps run every domain event
/// handler, the notifiers among them, and a notifier pushes through <c>INotificationPush</c>,
/// which only the Api registers — so a notification delivered here was written with nothing to
/// push it, and the user's badge waited for its next poll. Only the Api calls AddOutboxPumps.
/// </summary>
[Collection(nameof(WorkerFactoryCollection))]
public sealed class OutboxPumpTests(WorkerFactory factory)
{
    [Fact]
    public void TheWorker_RunsNoOutboxPump()
    {
        var hostedServices = factory.Services.GetServices<IHostedService>();

        var pumps = hostedServices.Where(s => s is OutboxPollerService or OutboxWorkerService);

        pumps.Should().BeEmpty();
    }
}
