using AiFramework.Application.Maintenance;
using AiFramework.Application.Orders;
using AiFramework.Infrastructure.Jobs;
using AiFramework.Infrastructure.Jobs.Scheduling;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Jobs;

public sealed class ScheduledJobTests
{
    [Fact]
    public void EveryScheduledJob_HasAValidDefaultCron()
    {
        var scheduled = JobRegistration.Jobs.Where(j => j.IsScheduled).ToArray();

        scheduled.Should().NotBeEmpty("PruneProcessedOutbox is scheduled; an empty set means the list lost it");

        var invalid = scheduled.Where(j => !JobSchedules.IsValidCron(j.DefaultCron!)).Select(j => j.Name);
        invalid.Should().BeEmpty("an invalid default cron must fail here, not in a deployed worker");
    }

    [Fact]
    public void EveryScheduledJob_CanBeEnqueuedByTheScheduler()
    {
        // A descriptor built directly, or copied with a with-expression, can carry a cron but no
        // factory; only the Scheduled factory keeps the two together. Such a job would be synced
        // into Quartz and then throw on every firing, so the registration list must never hold one.
        var unfireable = JobRegistration.Jobs
            .Where(j => j.IsScheduled && j.EnqueueNew is null)
            .Select(j => j.Name);

        unfireable.Should().BeEmpty("a scheduled job must be registered with JobDescriptor.Scheduled<TJob>(cron)");
    }

    [Fact]
    public void PruneProcessedOutbox_IsScheduledHourly()
    {
        var job = JobSchedules.Find(nameof(PruneProcessedOutbox));

        job.Should().NotBeNull();
        job.DefaultCron.Should().Be("0 5 * * * ?");
        job.EnqueueNew.Should().NotBeNull();
    }

    [Fact]
    public void AnUnscheduledJob_HasNoCronAndNoFactory()
    {
        var job = JobSchedules.Find(nameof(RebuildOrderReport))!;

        job.IsScheduled.Should().BeFalse();
        job.EnqueueNew.Should().BeNull();
    }

    [Fact]
    public void Keys_AreTheSimpleTypeNameInTheJobsGroup()
    {
        var job = JobSchedules.Find(nameof(PruneProcessedOutbox))!;

        JobSchedules.JobKeyFor(job).Name.Should().Be("PruneProcessedOutbox");
        JobSchedules.JobKeyFor(job).Group.Should().Be("jobs");
        JobSchedules.TriggerKeyFor(job).Name.Should().Be("PruneProcessedOutbox");
        JobSchedules.TriggerKeyFor(job).Group.Should().Be("jobs");
    }

    [Fact]
    public void EffectiveCron_WithoutAnOverride_IsTheDefault()
    {
        var job = JobSchedules.Find(nameof(PruneProcessedOutbox))!;

        JobSchedules.EffectiveCron(job, new JobOptions()).Should().Be("0 5 * * * ?");
    }

    [Fact]
    public void EffectiveCron_WithAnOverride_IsTheOverride()
    {
        var job = JobSchedules.Find(nameof(PruneProcessedOutbox))!;
        var options = new JobOptions();
        options.Schedules["PruneProcessedOutbox"] = "0 5 */6 * * ?";

        JobSchedules.EffectiveCron(job, options).Should().Be("0 5 */6 * * ?");
    }

    [Fact]
    public void Validate_WithAnOverrideForAnUnknownJob_ThrowsNamingIt()
    {
        var options = new JobOptions { Queues = "light,heavy" };
        options.Schedules["NoSuchJob"] = "0 5 * * * ?";

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*NoSuchJob*");
    }

    [Fact]
    public void Validate_WithAnOverrideForAnUnscheduledJob_Throws()
    {
        // RebuildOrderReport exists but cannot be scheduled (it needs an owner), so a cron for it is
        // a configuration mistake, not a way to schedule it.
        var options = new JobOptions { Queues = "light,heavy" };
        options.Schedules["RebuildOrderReport"] = "0 5 * * * ?";

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*RebuildOrderReport*");
    }

    [Fact]
    public void Validate_WithAnInvalidOverrideCron_ThrowsNamingTheKey()
    {
        var options = new JobOptions { Queues = "light,heavy" };
        options.Schedules["PruneProcessedOutbox"] = "every hour please";

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*PruneProcessedOutbox*");
    }

    [Theory]
    [InlineData("0 5 * * * ?", true)]
    [InlineData("0 0/15 * * * ?", true)]
    [InlineData("*/5 * * * *", false)]       // five fields: Unix cron, not Quartz's seconds-first format
    [InlineData("nonsense", false)]
    [InlineData("", false)]
    public void IsValidCron_AcceptsQuartzCronOnly(string cron, bool valid)
    {
        JobSchedules.IsValidCron(cron).Should().Be(valid);
    }
}
