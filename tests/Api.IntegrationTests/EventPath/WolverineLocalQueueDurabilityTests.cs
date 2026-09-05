using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;

namespace AiFramework.Api.IntegrationTests.EventPath;

/// <summary>
/// Wolverine's local queues are <c>BufferedInMemory</c> unless a policy enrolls them, which means
/// a message sitting in one when the process dies is simply gone. That is not a rare failure on
/// either deployment target this app is headed for: IIS recycles app pools on a schedule and stops
/// the worker process after an idle timeout, and Kubernetes reschedules pods for deploys, node
/// drains and scaling. Both kill <c>BackgroundService</c>s, and message handling here is one.
///
/// The gap was measured rather than assumed. Before <c>UseDurableLocalQueues()</c>, this app
/// reported:
/// <code>
/// local://aiframework.infrastructure.eventpath.orderplacednotification/  mode=BufferedInMemory
/// </code>
/// and reports <c>Durable</c> after. This test pins that, because the policy is one line whose
/// absence changes nothing observable until a process dies at the wrong moment.
///
/// It reads the REAL application host rather than building a synthetic one on purpose: the policy
/// is applied during host start, and what matters is the configuration this app actually runs.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class WolverineLocalQueueDurabilityTests(ApiFactory factory)
{
    [Fact]
    public async Task EveryLocalQueue_IsEnrolledInDurability()
    {
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();

        var capabilities = await ServiceCapabilities.ReadFrom(
            runtime, new Uri("local://durability-test"), CancellationToken.None);

        var localQueues = capabilities.MessagingEndpoints
            .Where(endpoint => string.Equals(endpoint.TransportType, "Local Queue", StringComparison.Ordinal))
            .ToList();

        localQueues.Should().NotBeEmpty(
            "the assertion below passes vacuously if the transport name ever changes, which would " +
            "silently stop this test guarding anything");

        localQueues.Should().OnlyContain(
            endpoint => endpoint.Mode == EndpointMode.Durable,
            "a BufferedInMemory local queue loses its messages when the process dies, and both IIS " +
            "app-pool recycling and Kubernetes pod rescheduling do that routinely");
    }
}
