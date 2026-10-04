using System.Net;
using System.Net.Http.Json;
using AiFramework.Application.Maintenance;
using AiFramework.Application.Orders;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Monitoring;

/// <summary>
/// Phase 2's endpoints over real HTTP: the gate on every one of them, and the two actions'
/// outcomes. See ADR 0021.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class MonitoringJobsTests(ApiFactory factory)
{
    private readonly ApiFactory _factory = factory;

    public static TheoryData<string> ReadEndpoints()
    {
        var data = new TheoryData<string>();
        data.Add("/api/monitoring/jobs/health");
        data.Add("/api/monitoring/jobs/runs");
        data.Add("/api/monitoring/jobs/dead-letters");
        data.Add("/api/monitoring/sign-ins");
        data.Add("/api/monitoring/sign-ins/health");

        return data;
    }

    [Theory]
    [MemberData(nameof(ReadEndpoints))]
    public async Task AMember_IsForbiddenFromEveryReadEndpoint(string path)
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        // The policy is on the controller rather than on each action, so this is really asserting
        // that a new endpoint cannot be added ungated by forgetting an attribute.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(ReadEndpoints))]
    public async Task AnAdministrator_CanReadEveryReadEndpoint(string path)
    {
        var client = await _factory.CreateAdminClientAsync();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AMember_IsForbiddenFromTriggeringAJob()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/monitoring/jobs/trigger", new { JobName = nameof(PruneProcessedOutbox) });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TriggeringAScheduledJob_IsAccepted()
    {
        var client = await _factory.CreateAdminClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/monitoring/jobs/trigger", new { JobName = nameof(PruneProcessedOutbox) });

        // 202 rather than 200: the API publishes and stops there. Whether the job succeeds is a
        // later job_runs row, written by the worker that picks it up — the two hosts never talk.
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task TriggeringAJobThatTakesArguments_IsRefused()
    {
        var client = await _factory.CreateAdminClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/monitoring/jobs/trigger", new { JobName = nameof(BuildOrderExport) });

        // Registered, but not scheduled: it carries an owner, and a trigger has none to supply.
        // The same constraint JobDescriptor.Scheduled's new() puts in the type system (ADR 0017),
        // reported rather than guessed at with a default.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TriggeringAnUnknownJob_IsNotFound()
    {
        var client = await _factory.CreateAdminClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/monitoring/jobs/trigger", new { JobName = "NoSuchJob" });

        // The lookup is by name against the registration list, never by reflecting over the
        // assembly for a type matching whatever string the caller sent.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RetryingAMessageThatIsNotDeadLettered_IsNotFound()
    {
        var client = await _factory.CreateAdminClientAsync();

        var response = await client.PostAsync(
            new Uri($"/api/monitoring/jobs/dead-letters/{Guid.NewGuid()}/retry", UriKind.Relative),
            content: null);

        // Not a silent success: an operator who clicked retry on a message someone else had
        // already discarded should be told, not left watching for a run that never appears.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheHealthEndpoint_ListsTheJobsAnOperatorMayTrigger()
    {
        var client = await _factory.CreateAdminClientAsync();

        var health = await client.GetFromJsonAsync<JobHealthPayload>(
            new Uri("/api/monitoring/jobs/health", UriKind.Relative));

        health.Should().NotBeNull();
        health.TriggerableJobs.Should().Contain(nameof(PruneProcessedOutbox));
        health.TriggerableJobs.Should().NotContain(
            nameof(BuildOrderExport), "a job that takes arguments cannot be triggered");
    }

    [Fact]
    public async Task AnInvalidWindow_IsRefused()
    {
        var client = await _factory.CreateAdminClientAsync();

        var response = await client.GetAsync(
            new Uri("/api/monitoring/jobs/health?windowHours=0", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnOverLargePage_IsRefused()
    {
        var client = await _factory.CreateAdminClientAsync();

        var response = await client.GetAsync(
            new Uri("/api/monitoring/jobs/runs?pageSize=100000", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record JobHealthPayload(IReadOnlyList<string> TriggerableJobs);
}
