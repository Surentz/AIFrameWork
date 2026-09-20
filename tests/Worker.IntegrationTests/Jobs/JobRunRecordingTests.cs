using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Infrastructure.Jobs;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace AiFramework.Worker.IntegrationTests.Jobs;

/// <summary>
/// What the monitoring page reads, recorded by the real middleware on the real host. See ADR 0021.
/// </summary>
[Collection(nameof(WorkerFactoryCollection))]
public sealed class JobRunRecordingTests(WorkerFactory factory)
{
    private static TrackedSessionConfiguration TrackJobs(IHost host) =>
        host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(TimeSpan.FromSeconds(30));

    private async Task EnqueueAsync<TJob>(TJob job)
        where TJob : IJob
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        await jobs.EnqueueAsync(job, CancellationToken.None);
    }

    private async Task<JobRun?> ReadRunAsync(string jobName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        return await context.JobRuns
            .AsNoTracking()
            .Where(run => run.JobName == jobName)
            .OrderByDescending(run => run.StartedAt)
            .FirstOrDefaultAsync();
    }

    [Fact]
    public async Task ASuccessfulJob_IsRecordedAsSucceeded()
    {
        var host = factory.Services.GetRequiredService<IHost>();

        await TrackJobs(host).ExecuteAndWaitAsync(
            _ => EnqueueAsync(new SendOrderConfirmation(Guid.NewGuid(), "SKU-RUN-OK", 1)));

        var run = await ReadRunAsync(nameof(SendOrderConfirmation));

        run.Should().NotBeNull("every job attempt is recorded, without the job opting in");
        run.Status.Should().Be(JobRunStatus.Succeeded);
        run.Error.Should().BeNull();
        run.CompletedAt.Should().NotBeNull();
        run.DurationMs.Should().NotBeNull().And.BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ARecordedRun_CarriesTheLaneItRanOn()
    {
        var host = factory.Services.GetRequiredService<IHost>();

        await TrackJobs(host).ExecuteAndWaitAsync(
            _ => EnqueueAsync(new RebuildOrderReport(Guid.NewGuid())));

        var run = await ReadRunAsync(nameof(RebuildOrderReport));

        // The lane comes from JobRegistration, not from the message — IJob.Lane is a static
        // abstract and cannot be read off an instance at all.
        run.Should().NotBeNull();
        run.Lane.Should().Be(JobLane.Heavy);
    }

    [Fact]
    public async Task AUserScopedJob_RecordsItsOwner()
    {
        var ownerId = Guid.NewGuid();
        var host = factory.Services.GetRequiredService<IHost>();

        await TrackJobs(host).ExecuteAndWaitAsync(_ => EnqueueAsync(new RebuildOrderReport(ownerId)));

        var run = await ReadRunAsync(nameof(RebuildOrderReport));

        run.Should().NotBeNull();
        run.OwnerId.Should().Be(ownerId, "a user-scoped job's run is attributable to its owner");
    }

    [Fact]
    public async Task ARecordedRun_CarriesTheTraceIdToFindItsLogsBy()
    {
        var host = factory.Services.GetRequiredService<IHost>();

        await TrackJobs(host).ExecuteAndWaitAsync(
            _ => EnqueueAsync(new SendOrderConfirmation(Guid.NewGuid(), "SKU-RUN-TRACE", 1)));

        var run = await ReadRunAsync(nameof(SendOrderConfirmation));

        // The whole of ADR 0021's "operational facts here, diagnostic detail in the log store,
        // joined by TraceId". A null here makes every row a dead end.
        run.Should().NotBeNull();
        run.TraceId.Should().NotBeNullOrWhiteSpace();
    }
}
