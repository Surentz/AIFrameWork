using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;

namespace AiFramework.Api.IntegrationTests.Jobs;

/// <summary>
/// <b>The test that makes ADR 0016 a rule rather than an intention.</b>
///
/// The whole design rests on one sentence — "jobs run in the worker; the API listens to nothing" —
/// and on one thing being true of this host: it registers job ROUTING but no job LISTENER. Nothing
/// about that is visible at a call site, nothing fails to compile if it changes, and the symptom
/// of getting it wrong is not an error but a slow API, which is the failure this whole design
/// exists to prevent.
///
/// It reads the REAL application host rather than a synthetic one, for the reason
/// <c>WolverineLocalQueueDurabilityTests</c> gives: endpoint configuration is applied during host
/// start, and what matters is what this app actually runs.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class ApiPublishesOnlyTests(ApiFactory factory)
{
    [Fact]
    public async Task TheApi_ListensOnNoJobQueue()
    {
        var endpoints = await ReadEndpointsAsync();

        var jobListeners = endpoints
            .Where(endpoint => endpoint.IsListener)
            .Where(endpoint => IsAJobQueue(endpoint.Uri))
            .Select(endpoint => endpoint.Uri.ToString())
            .ToList();

        jobListeners.Should().BeEmpty(
            "the API must publish jobs and consume none — a job handled here would compete with " +
            "request handling for the thread pool, the GC heap and the Npgsql pool, which is the " +
            "whole reason ADR 0016 put a worker host in front of them");
    }

    /// <summary>
    /// The other half, and the one that would otherwise pass vacuously: if routing were missing
    /// too, the test above would be satisfied by a host that simply cannot do jobs at all.
    /// </summary>
    [Fact]
    public async Task TheApi_StillRoutesJobsToTheirQueues()
    {
        var endpoints = await ReadEndpointsAsync();

        var jobSenders = endpoints
            .Where(endpoint => !endpoint.IsListener)
            .Where(endpoint => IsAJobQueue(endpoint.Uri))
            .Select(endpoint => endpoint.Uri.ToString())
            .ToList();

        jobSenders.Should().NotBeEmpty(
            "the API is where jobs are enqueued from, so it needs the routing rules even though " +
            "it never handles one; without them PublishAsync has nowhere to send a job");
    }

    /// <summary>
    /// Both lanes must be reachable from here. A lane whose queue name the API never learned is a
    /// job that silently goes nowhere.
    /// </summary>
    [Fact]
    public async Task EveryLane_HasARouteFromTheApi()
    {
        var endpoints = await ReadEndpointsAsync();

        var routed = endpoints
            .Where(endpoint => !endpoint.IsListener)
            .Select(endpoint => endpoint.Uri.ToString())
            .ToList();

        foreach (var lane in Enum.GetValues<JobLane>())
        {
            routed.Should().Contain(
                uri => uri.Contains(JobRegistration.QueueFor(lane), StringComparison.OrdinalIgnoreCase),
                $"every lane needs a route from the API, and {lane} had none");
        }
    }

    private static bool IsAJobQueue(Uri uri) =>
        Enum.GetValues<JobLane>().Any(lane =>
            uri.ToString().Contains(JobRegistration.QueueFor(lane), StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<EndpointDescriptor>> ReadEndpointsAsync()
    {
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();

        var capabilities = await ServiceCapabilities.ReadFrom(
            runtime, new Uri("local://jobs-host-role-test"), CancellationToken.None);

        return capabilities.MessagingEndpoints;
    }
}
