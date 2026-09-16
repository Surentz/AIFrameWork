# Quartz Scheduling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Jobs can be declared with a cron schedule. A clustered Quartz scheduler in the worker
fires each schedule exactly once across replicas and enqueues the job into Wolverine, which runs
it exactly as it runs every other job. The outbox retention sweep becomes the first scheduled job
and leaves the API.

**Architecture:** Quartz 4.1 is only the clock. Its single job type, `EnqueueScheduledJob`, calls
`IJobScheduler.EnqueueAsync`. Schedules live on `JobDescriptor` (`JobDescriptor.Scheduled<TJob>(cron)`,
constrained `new()`), can be overridden per environment through `Jobs__Schedules__<Name>`, and are
synced into a Postgres-backed clustered store at worker startup without touching paused state. An
EF migration creates the `quartz` schema from Quartz's own bundled script; Quartz's default
`SchemaProvisioning.Validate` refuses to start against a missing or stale one.

**Tech Stack:** .NET 10, Quartz 4.1.0, Wolverine 6.33.0, EF Core + Npgsql 10.0.x, xUnit +
FluentAssertions + NSubstitute, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-16-quartz-scheduling-design.md`

## Global Constraints

- **Warnings are errors** — compiler, analyzers and build. Never suppress without a narrow
  `#pragma warning disable`/`restore` pair carrying a justification comment above it.
- **Nullable is enabled. Never `catch (Exception)`.** `throw;`, never `throw ex;`. Quartz jobs are
  not a new CA1031 exemption: `EnqueueScheduledJob` lets exceptions propagate to Quartz.
- **The dependency rule is hook-enforced.** No Quartz type in `src/Application`. `Quartz` is
  referenced by `AiFramework.Infrastructure.csproj` only.
- **Quartz runs only in the worker.** `AddQuartz`/`AddQuartzHostedService` are called from
  `src/Worker/Program.cs`, never from `AddInfrastructure` and never from `src/Api`.
- **Quartz 4.1 API, not 3.x.** Verified shapes: `services.AddQuartz(q => ...)`,
  `q.UsePersistentStore(s => { s.UsePostgres(cs); s.ConfigureStore(o => { o.TablePrefix = ...;
  o.SchemaProvisioning = SchemaProvisioning.Validate; }); s.UseClustering(c => c.Enabled = true); })`,
  `services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true)`,
  `IJob.Execute(IJobExecutionContext, CancellationToken)` returning `ValueTask`,
  `CronExpression.TryParse(string, out _)`, `scheduler.AddJob(job, AddJobOptions.Replacing, ct)`,
  `scheduler.ScheduleJob(trigger, ScheduleJobOptions.Replacing, ct)`,
  `scheduler.QueryJobs(new JobQuery { Group = GroupMatcher<JobKey>.GroupEquals(g) }, ct)` returning
  `PagedResult<JobHeader>` (`.Items`, `.HasMore`), `scheduler.TriggerJob(key, new JobDataMap(), ct)`,
  `ISchedulerFactory.GetScheduler(ct)`, `CronTriggerMisfireInstruction.FireAndProceed`,
  `Quartz.Diagnostics.QuartzInstrumentation.ActivitySourceName` (= `"Quartz"`). There is **no**
  `GetJobKeys`, **no** `CronExpression.IsValidExpression`, **no** settable `SchedulerName`.
- **Quartz group is `jobs`; keys are the job type's simple name.** Table prefix `quartz.qrtz_`.
- **Applied migrations are never edited.** The Quartz SQL lives **inline in the migration `.cs`** —
  `.claude/hooks/protect-migrations.ps1` guards `Migrations/*.cs` and nothing else.
- **Config keys use double underscores** — `Jobs__Schedules__PruneProcessedOutbox`.
- **After changing a job handler, regenerate the worker's adapters**:
  `dotnet run --project src/Worker -- codegen write` (with
  `ConnectionStrings__Default=...placeholder...` and `Wolverine__Durable=false`), and commit.
- **No `Thread.Sleep`/`Task.Delay` in tests.** Fire triggers with `TriggerJob`; observe with a
  Wolverine tracking session using `IncludeExternalTransports()`.
- Run `dotnet test <project>` **one project at a time**.
- Dev connection string: `Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres`
- Branch: `claude/quartz-scheduling`.

---

## File Structure

**Created:**
- `src/Application/Maintenance/PruneProcessedOutbox.cs` — the job, its handler, the `IOutboxRetention` port
- `src/Infrastructure/Outbox/OutboxRetention.cs` — `IOutboxRetention` over `OutboxPoller.PruneAsync`
- `src/Infrastructure/Jobs/Scheduling/JobSchedules.cs` — effective-cron resolution, cron validation, keys
- `src/Infrastructure/Jobs/Scheduling/EnqueueScheduledJob.cs` — the one Quartz `IJob`
- `src/Infrastructure/Jobs/Scheduling/ScheduleSynchronizer.cs` — startup sync into the store
- `src/Infrastructure/Jobs/Scheduling/QuartzRegistration.cs` — `AddJobScheduling(connectionString)`
- `src/Infrastructure/Persistence/Migrations/<ts>_AddQuartzSchema.cs` (+ `.Designer.cs`)
- `tests/Infrastructure.Tests/Jobs/ScheduledJobTests.cs`
- `tests/Application.Tests/Maintenance/PruneProcessedOutboxHandlerTests.cs`
- `tests/Worker.IntegrationTests/Jobs/SchedulingTests.cs`
- `tests/Api.IntegrationTests/Jobs/ApiHasNoSchedulerTests.cs`
- `docs/adr/0017-quartz-as-the-job-clock.md`

**Modified:**
- `src/Infrastructure/AiFramework.Infrastructure.csproj` — `Quartz` 4.1.0
- `src/Infrastructure/Jobs/JobRegistration.cs` — `JobDescriptor` gains the schedule; registration of the new job
- `src/Infrastructure/Jobs/JobOptions.cs` — `Schedules` and its validation
- `src/Infrastructure/Outbox/OutboxHostedServices.cs`, `OutboxOptions.cs` — prune removed from the poll loop
- `src/Worker/Program.cs`, `src/Worker/Observability/WorkerObservability.cs`
- `src/Worker/Internal/Generated/**` — regenerated
- `tests/Infrastructure.Tests/Outbox/OutboxRegistrationTests.cs` — stale comment
- `CLAUDE.md`, `src/Worker/CLAUDE.md`, `.claude/commands/job.md`, `docs/adr/0016-…md`

---

## Task 1: Spike — Quartz 4.1 against a real Postgres, before anything is built

The spec lists three things not yet verified. This task proves them in a throwaway test, records
the results in this plan, and deletes the test. **Nothing in later tasks may be built on an
assumption this task could have checked.**

**Files:**
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj`
- Create (throwaway): `tests/Worker.IntegrationTests/Spikes/QuartzSpike.cs`

**Interfaces:**
- Produces: the `Quartz` 4.1.0 package reference later tasks compile against; three recorded findings.

- [ ] **Step 1: Add the package**

In `src/Infrastructure/AiFramework.Infrastructure.csproj`, beside the Wolverine references:

```xml
    <!--
      Quartz 4.1 is the job CLOCK only: its one job type enqueues through IJobScheduler, and
      Wolverine runs the work (ADR 0017). Referenced here and nowhere else — Application never
      sees a Quartz type, and only the worker starts a scheduler.
    -->
    <PackageReference Include="Quartz" Version="4.1.0" />
```

Run: `dotnet build src/Infrastructure -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 2: Write the spike**

`tests/Worker.IntegrationTests/Spikes/QuartzSpike.cs` — creates the schema exactly the way the
migration in Task 5 will (the embedded script with `{0}`/`{1}` substituted), then starts a real
clustered, `Validate`-mode scheduler against it and fires a job:

```csharp
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Quartz;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace AiFramework.Worker.IntegrationTests.Spikes;

/// <summary>THROWAWAY — Task 1 of docs/superpowers/plans/2026-09-16-quartz-scheduling.md.</summary>
public sealed class QuartzSpike(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _db.StartAsync();

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task ValidateAcceptsASchemaQualifiedPrefix_AndAClusteredSchedulerFires()
    {
        var script = await ReadEmbeddedScriptAsync();
        await using (var connection = new NpgsqlConnection(_db.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand(
                "CREATE SCHEMA IF NOT EXISTS quartz;\n" +
                script.Replace("{0}", "quartz.qrtz_", StringComparison.Ordinal)
                      .Replace("{1}", "qrtz_", StringComparison.Ordinal)
                      .Replace("--;;", string.Empty, StringComparison.Ordinal),
                connection);
            await create.ExecuteNonQueryAsync();
        }

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<SpikeRecorder>();
                services.AddQuartz(q => q.UsePersistentStore(s =>
                {
                    s.UsePostgres(_db.GetConnectionString());
                    s.ConfigureStore(o =>
                    {
                        o.TablePrefix = "quartz.qrtz_";
                        o.SchemaProvisioning = SchemaProvisioning.Validate;
                    });
                    s.UseClustering(c => c.Enabled = true);
                }));
                services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
            })
            .Build();

        host.Services.GetRequiredService<SpikeRecorder>().Root = host.Services;
        await host.StartAsync();   // FINDING 1: throws here if Validate rejects the prefix

        var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(CancellationToken.None);
        var key = new JobKey("spike", "jobs");
        await scheduler.AddJob(
            JobBuilder.Create<SpikeJob>().WithIdentity(key).StoreDurably().Build(),
            AddJobOptions.Replacing,
            CancellationToken.None);
        await scheduler.TriggerJob(key, new JobDataMap(), CancellationToken.None);

        var recorder = host.Services.GetRequiredService<SpikeRecorder>();
        var fired = await recorder.Fired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        output.WriteLine($"[SPIKE] fired={fired.Fired}");
        output.WriteLine($"[SPIKE] job resolved from a child scope, not the root: {fired.FromChildScope}");   // FINDING 3
        fired.Fired.Should().BeTrue();

        await host.StopAsync();
    }

    private static async Task<string> ReadEmbeddedScriptAsync()
    {
        var assembly = typeof(IJob).Assembly;
        await using var stream = assembly.GetManifestResourceStream(
            "Quartz.Impl.AdoJobStore.Schema.create_postgres.sql")!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public sealed record SpikeResult(bool Fired, bool FromChildScope);

    public sealed class SpikeRecorder
    {
        public TaskCompletionSource<SpikeResult> Fired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServiceProvider? Root { get; set; }
    }

    public sealed class SpikeJob(SpikeRecorder recorder, IServiceProvider services) : IJob
    {
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            // FINDING 3: does Quartz 4 resolve the job inside a scope? Compared by identity rather than
            // by resolving a scoped service, because a Production host does not validate scopes and
            // would hand one out from the root without complaint.
            var fromChildScope = !ReferenceEquals(services, recorder.Root);
            recorder.Fired.TrySetResult(new SpikeResult(true, fromChildScope));
            return ValueTask.CompletedTask;
        }
    }
}
```

- [ ] **Step 3: Run it**

Run: `dotnet test tests/Worker.IntegrationTests -c Debug --filter "FullyQualifiedName~QuartzSpike" --logger "console;verbosity=detailed"`
Expected: PASS. Read the `[SPIKE]` lines.

If `host.StartAsync()` throws a schema validation error, **the `{0}`/`--;;` handling is wrong for
the migration too** — fix it here (e.g. if `--;;` must split into separate commands) and carry the
fix into Task 5 before going on.

- [ ] **Step 4: Record the findings and delete the spike**

Add a `> **RESULT**` block under this task's heading recording:
1. whether `Validate` accepted `quartz.qrtz_`, and the exact SQL transformation that worked;
2. that a clustered scheduler started and fired;
3. whether the job was resolved in a scope.

Then:

```bash
git rm -q --cached tests/Worker.IntegrationTests/Spikes/QuartzSpike.cs 2>/dev/null; rm -rf tests/Worker.IntegrationTests/Spikes
```

`EnqueueScheduledJob` (Task 4) creates its own scope through `IServiceScopeFactory` regardless of
finding 3, so no later code depends on it; the finding goes in the class's remarks.

- [ ] **Step 5: Commit**

```bash
git add src/Infrastructure/AiFramework.Infrastructure.csproj docs/superpowers/plans/2026-09-16-quartz-scheduling.md
git commit -m "chore(jobs): add Quartz 4.1 and record the spike against real Postgres"
```

---

## Task 2: The first scheduled job's domain — `PruneProcessedOutbox`

**Files:**
- Create: `src/Application/Maintenance/PruneProcessedOutbox.cs`
- Create: `src/Infrastructure/Outbox/OutboxRetention.cs`
- Test: `tests/Application.Tests/Maintenance/PruneProcessedOutboxHandlerTests.cs`

**Interfaces:**
- Produces: `public sealed record PruneProcessedOutbox : IJob` (Light, parameterless);
  `public interface IOutboxRetention { Task<int> PruneProcessedAsync(CancellationToken cancellationToken); }`;
  `public sealed class PruneProcessedOutboxHandler(IOutboxRetention retention)` with
  `Task Handle(PruneProcessedOutbox job, CancellationToken cancellationToken)`;
  `public sealed class OutboxRetention(OutboxPoller poller) : IOutboxRetention`.

- [ ] **Step 1: Write the failing test**

`tests/Application.Tests/Maintenance/PruneProcessedOutboxHandlerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Maintenance;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Maintenance;

public sealed class PruneProcessedOutboxHandlerTests
{
    private readonly IOutboxRetention _retention = Substitute.For<IOutboxRetention>();

    [Fact]
    public async Task Handle_PrunesThroughThePort()
    {
        _retention.PruneProcessedAsync(Arg.Any<CancellationToken>()).Returns(3);

        await new PruneProcessedOutboxHandler(_retention)
            .Handle(new PruneProcessedOutbox(), CancellationToken.None);

        await _retention.Received(1).PruneProcessedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheJob_RunsOnTheLightLane()
    {
        // A single DELETE with an indexed predicate: milliseconds, no CPU to speak of.
        PruneProcessedOutbox.Lane.Should().Be(JobLane.Light);
    }
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test tests/Application.Tests -c Debug --filter "FullyQualifiedName~PruneProcessedOutbox"`
Expected: build FAIL — `The type or namespace name 'Maintenance' does not exist`.

- [ ] **Step 3: Write the job, port and handler**

`src/Application/Maintenance/PruneProcessedOutbox.cs`:

```csharp
using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Maintenance;

/// <summary>
/// Removes outbox rows that were delivered longer ago than the retention period. Infrastructure
/// owns what "old enough" means (<c>OutboxOptions.RetentionPeriod</c>) and how the rows go.
/// </summary>
public interface IOutboxRetention
{
    /// <summary>Deletes processed rows past retention. Returns how many went.</summary>
    public Task<int> PruneProcessedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The first SCHEDULED job (ADR 0017), and the reference for writing one.
/// </summary>
/// <remarks>
/// <para>
/// Parameterless on purpose: a schedule fires with no caller and no arguments, so
/// <c>JobDescriptor.Scheduled&lt;TJob&gt;</c> requires <c>new()</c> and the compiler refuses to
/// schedule a job that needs data. A job that needs an owner is enqueued, never scheduled.
/// </para>
/// <para>
/// This used to run inside <c>OutboxPollerService</c>'s loop, in every pod that polls — both API
/// replicas and the worker — each issuing the same DELETE every five minutes. As a scheduled job it
/// runs once, on one worker, off the API.
/// </para>
/// </remarks>
public sealed record PruneProcessedOutbox : IJob
{
    public static JobLane Lane => JobLane.Light;
}

public sealed class PruneProcessedOutboxHandler(IOutboxRetention retention)
{
    public Task Handle(PruneProcessedOutbox job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // No logging of the count here: Wolverine records the message lifecycle, and a routine
        // sweep that deleted N rows is the "it worked" noise root CLAUDE.md keeps out of the store.
        return retention.PruneProcessedAsync(cancellationToken);
    }
}
```

`src/Infrastructure/Outbox/OutboxRetention.cs`:

```csharp
using AiFramework.Application.Maintenance;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>
/// <see cref="IOutboxRetention"/> over <see cref="OutboxPoller.PruneAsync"/>, which is unchanged and
/// keeps its own Postgres test (<c>OutboxPollerTests.PruneAsync_DeletesProcessedRowsPastRetentionButKeepsDeadOnes</c>).
/// </summary>
public sealed class OutboxRetention(OutboxPoller poller) : IOutboxRetention
{
    public Task<int> PruneProcessedAsync(CancellationToken cancellationToken) =>
        poller.PruneAsync(cancellationToken);
}
```

- [ ] **Step 4: Run the test**

Run: `dotnet test tests/Application.Tests -c Debug --filter "FullyQualifiedName~PruneProcessedOutbox"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Application/Maintenance src/Infrastructure/Outbox/OutboxRetention.cs tests/Application.Tests/Maintenance
git commit -m "feat(jobs): PruneProcessedOutbox job, its port and adapter"
```

---

## Task 3: Schedules on the job registration, and config overrides

**Files:**
- Create: `src/Infrastructure/Jobs/Scheduling/JobSchedules.cs`
- Modify: `src/Infrastructure/Jobs/JobRegistration.cs`
- Modify: `src/Infrastructure/Jobs/JobOptions.cs`
- Test: `tests/Infrastructure.Tests/Jobs/ScheduledJobTests.cs`

**Interfaces:**
- Consumes: `PruneProcessedOutbox`, `IOutboxRetention`, `OutboxRetention` (Task 2).
- Produces:
  - `JobDescriptor` gains `string? DefaultCron` and `Func<IJobScheduler, CancellationToken, Task>? EnqueueNew`,
    plus `string Name => JobType.Name` and `bool IsScheduled => DefaultCron is not null`.
  - `public static JobDescriptor JobDescriptor.Scheduled<TJob>(string cron) where TJob : IJob, new()`
  - `JobOptions.Schedules : IDictionary<string, string>` (get-only, ordinal keys)
  - `public static class JobSchedules` with
    `const string Group = "jobs"`,
    `static bool IsValidCron(string cron)`,
    `static JobKey JobKeyFor(JobDescriptor job)`, `static TriggerKey TriggerKeyFor(JobDescriptor job)`,
    `static string EffectiveCron(JobDescriptor job, JobOptions options)`,
    `static JobDescriptor? Find(string name)`.

- [ ] **Step 1: Write the failing tests**

`tests/Infrastructure.Tests/Jobs/ScheduledJobTests.cs`:

```csharp
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
    public void PruneProcessedOutbox_IsScheduledHourly()
    {
        var job = JobSchedules.Find(nameof(PruneProcessedOutbox));

        job.Should().NotBeNull();
        job!.DefaultCron.Should().Be("0 5 * * * ?");
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
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Infrastructure.Tests -c Debug --filter "FullyQualifiedName~ScheduledJobTests"`
Expected: build FAIL — `JobSchedules` does not exist.

If the `"*/5 * * * *"` case turns out to be accepted by Quartz 4.1 (4.0 added Unix cron support
behind `CronFormat`), change the expectation to match `CronExpression.TryParse(string, out _)`'s
actual behaviour and add a comment saying which format the default parse uses — do not guess.

- [ ] **Step 3: Extend `JobDescriptor`**

In `src/Infrastructure/Jobs/JobRegistration.cs`, replace the `JobDescriptor` record and add the
scheduled job to the list:

```csharp
/// <summary>
/// One job's registration: its type, its lane, how it is routed, and — for a scheduled job — its
/// default cron and how to create one. The job-side equivalent of
/// <c>CommandDescriptor</c>/<c>QueryDescriptor</c>.
/// </summary>
/// <remarks>
/// <see cref="Route"/> and <see cref="EnqueueNew"/> are delegates captured over the closed generic
/// at construction time, so routing and scheduled enqueueing stay <b>reflection-free</b> — the same
/// posture <c>AddCommand</c>/<c>AddQuery</c> hold. <see cref="JobType"/> exists so completeness
/// tests can read the list without executing it.
/// </remarks>
public sealed record JobDescriptor(
    Type JobType,
    JobLane Lane,
    Action<WolverineOptions> Route,
    string? DefaultCron = null,
    Func<IJobScheduler, CancellationToken, Task>? EnqueueNew = null)
{
    /// <summary>The job's key everywhere a string is needed: Quartz keys and config overrides.</summary>
    public string Name => JobType.Name;

    public bool IsScheduled => DefaultCron is not null;

    public static JobDescriptor For<TJob>()
        where TJob : IJob =>
        new(typeof(TJob), TJob.Lane, RouteFor<TJob>());

    /// <summary>
    /// A job that also runs on a schedule. <c>new()</c> is the point: a schedule fires with no caller
    /// and no arguments, so a job that needs data (an owner, an id) cannot be scheduled, and the
    /// compiler says so rather than a worker firing it with defaults. ADR 0017.
    /// </summary>
    /// <param name="cron">A Quartz cron expression — seconds first, e.g. <c>"0 5 * * * ?"</c>.</param>
    public static JobDescriptor Scheduled<TJob>(string cron)
        where TJob : IJob, new() =>
        new(
            typeof(TJob),
            TJob.Lane,
            RouteFor<TJob>(),
            cron,
            static (jobs, cancellationToken) => jobs.EnqueueAsync(new TJob(), cancellationToken));

    private static Action<WolverineOptions> RouteFor<TJob>()
        where TJob : IJob =>
        static opts => opts.PublishMessage<TJob>()
            .ToPostgresqlQueue(JobRegistration.QueueFor(TJob.Lane));
}
```

and in `JobRegistration`:

```csharp
    public static IReadOnlyList<JobDescriptor> Jobs { get; } =
    [
        JobDescriptor.For<SendOrderConfirmation>(),
        JobDescriptor.For<RebuildOrderReport>(),

        // Hourly at :05. Retention is seven days, so hourly is already generous; the old
        // five-minute cadence existed only because the sweep piggy-backed on the poll loop.
        JobDescriptor.Scheduled<PruneProcessedOutbox>("0 5 * * * ?"),
    ];
```

In `IncludeJobHandlers`, add the handler:

```csharp
        opts.Discovery
            .IncludeType<SendOrderConfirmationHandler>()
            .IncludeType<RebuildOrderReportHandler>()
            .IncludeType<PruneProcessedOutboxHandler>();
```

In `AddJobs`, register the port beside the other two:

```csharp
        services.AddScoped<IOutboxRetention, OutboxRetention>();
```

Add the usings `AiFramework.Application.Maintenance` and `AiFramework.Infrastructure.Outbox`.

- [ ] **Step 4: Add `JobSchedules`**

`src/Infrastructure/Jobs/Scheduling/JobSchedules.cs`:

```csharp
using Quartz;

namespace AiFramework.Infrastructure.Jobs.Scheduling;

/// <summary>
/// Everything about a schedule that does not need a running scheduler: keys, the effective cron,
/// and cron validation. Kept apart from <see cref="ScheduleSynchronizer"/> so it is testable with
/// no host and no database.
/// </summary>
public static class JobSchedules
{
    /// <summary>
    /// The only Quartz group this code ever reads or writes. The synchronizer deletes stale entries
    /// within it and nowhere else, so nothing else a store might hold is ever at risk.
    /// </summary>
    public const string Group = "jobs";

    /// <summary>The JobDataMap key carrying the job's <see cref="JobDescriptor.Name"/>.</summary>
    public const string JobNameKey = "job";

    public static JobKey JobKeyFor(JobDescriptor job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new JobKey(job.Name, Group);
    }

    public static TriggerKey TriggerKeyFor(JobDescriptor job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new TriggerKey(job.Name, Group);
    }

    /// <summary>
    /// Quartz's own parser, so "valid" means exactly what the scheduler will accept — seconds
    /// first (<c>"0 5 * * * ?"</c>).
    /// </summary>
    public static bool IsValidCron(string cron) =>
        !string.IsNullOrWhiteSpace(cron) && CronExpression.TryParse(cron, out _);

    /// <summary>The config override if there is one, otherwise the code default.</summary>
    public static string EffectiveCron(JobDescriptor job, JobOptions options)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(options);

        if (!job.IsScheduled)
        {
            throw new InvalidOperationException($"{job.Name} is not a scheduled job.");
        }

        return options.Schedules.TryGetValue(job.Name, out var overridden)
            ? overridden
            : job.DefaultCron!;
    }

    public static JobDescriptor? Find(string name) =>
        JobRegistration.Jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.Ordinal));
}
```

- [ ] **Step 5: Add `Schedules` and its validation to `JobOptions`**

In `src/Infrastructure/Jobs/JobOptions.cs`, add the property after `HeavyParallelism`:

```csharp
    /// <summary>
    /// Per-environment cron overrides, keyed by job type name —
    /// <c>Jobs__Schedules__PruneProcessedOutbox</c>. A job without an entry keeps the cron its
    /// registration declares. Get-only: the configuration binder fills an existing dictionary,
    /// confirmed against Microsoft.Extensions.Configuration.Binder 10.0.
    /// </summary>
    public IDictionary<string, string> Schedules { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
```

and extend `Validate()` so it ends with:

```csharp
        ParseQueues();
        ValidateSchedules();
    }

    /// <summary>
    /// An override must name a job that exists AND is scheduled, and carry a cron Quartz accepts.
    /// Anything else fails startup naming the key, rather than being ignored — an ignored override
    /// is a schedule an operator believes they changed.
    /// </summary>
    private void ValidateSchedules()
    {
        foreach (var (name, cron) in Schedules)
        {
            var job = Scheduling.JobSchedules.Find(name);

            if (job is null)
            {
                throw new InvalidOperationException(
                    $"Jobs:Schedules:{name} names no job. Scheduled jobs are: " +
                    $"{string.Join(", ", JobRegistration.Jobs.Where(j => j.IsScheduled).Select(j => j.Name))}.");
            }

            if (!job.IsScheduled)
            {
                throw new InvalidOperationException(
                    $"Jobs:Schedules:{name} is set, but {name} is not a scheduled job and cannot be " +
                    "made one by configuration.");
            }

            if (!Scheduling.JobSchedules.IsValidCron(cron))
            {
                throw new InvalidOperationException(
                    $"Jobs:Schedules:{name} = '{cron}' is not a Quartz cron expression " +
                    "(seconds first, e.g. '0 5 * * * ?').");
            }
        }
    }
```

`JobOptionsValidator` already turns a throw from `ParseQueues` into a validation failure; change
its `try` body to call `options.Validate()` instead of `options.ParseQueues()` so schedule errors
are reported the same way.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests -c Debug --filter "FullyQualifiedName~Jobs"`
Expected: PASS — the new `ScheduledJobTests` plus the existing `JobRegistrationTests` (which now
also sees `PruneProcessedOutbox` and must still find it routed).

- [ ] **Step 7: Commit**

```bash
git add src/Infrastructure/Jobs tests/Infrastructure.Tests/Jobs/ScheduledJobTests.cs
git commit -m "feat(jobs): declare schedules on job registrations, with config overrides"
```

---

## Task 4: The Quartz job and the startup synchronizer

**Files:**
- Create: `src/Infrastructure/Jobs/Scheduling/EnqueueScheduledJob.cs`
- Create: `src/Infrastructure/Jobs/Scheduling/ScheduleSynchronizer.cs`
- Create: `src/Infrastructure/Jobs/Scheduling/QuartzRegistration.cs`

**Interfaces:**
- Consumes: `JobSchedules`, `JobDescriptor.EnqueueNew`, `JobOptions` (Task 3).
- Produces:
  - `public sealed class EnqueueScheduledJob(IServiceScopeFactory scopes) : IJob`
  - `public sealed class ScheduleSynchronizer(ISchedulerFactory schedulers, IOptions<JobOptions> options, ILogger<ScheduleSynchronizer> logger)`
    with `public Task SynchronizeAsync(CancellationToken cancellationToken)`
  - `public static IServiceCollection AddJobScheduling(this IServiceCollection services, string connectionString)`
  - `public const string QuartzRegistration.TablePrefix = "quartz.qrtz_"`

These are verified end to end in Task 6; this task ends at a clean build.

- [ ] **Step 1: `EnqueueScheduledJob`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using AiFramework.Application.Abstractions;
using Quartz;

namespace AiFramework.Infrastructure.Jobs.Scheduling;

/// <summary>
/// <b>The only Quartz job this codebase has, and should ever have.</b> Quartz decides when; this
/// hands the work to Wolverine, which runs it on its lane with its retry policy like any other job.
/// ADR 0017.
/// </summary>
/// <remarks>
/// <para>
/// Creates its own scope rather than depending on how Quartz resolves jobs: <see cref="IJobScheduler"/>
/// is scoped, and a scoped dependency resolved from a root provider is exactly the bug scope
/// validation exists to catch. (Task 1's finding on Quartz 4.1's own behaviour: RECORD IT HERE.)
/// </para>
/// <para>
/// No catch: an exception propagates to Quartz, which logs it against the trigger. A job that
/// could not even be ENQUEUED is a scheduling fault, not a job failure — Wolverine's retry policy
/// has nothing to retry yet.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class EnqueueScheduledJob(IServiceScopeFactory scopes) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var name = context.MergedJobDataMap.GetString(JobSchedules.JobNameKey);
        var job = name is null ? null : JobSchedules.Find(name);

        if (job?.EnqueueNew is not { } enqueue)
        {
            // The synchronizer deletes stale entries at startup, so this means a job was removed
            // from the code while a trigger for it was already firing on another node.
            throw new InvalidOperationException(
                $"Quartz fired '{name}', which is not a scheduled job in this build.");
        }

        await using var scope = scopes.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        await enqueue(jobs, cancellationToken).ConfigureAwait(false);
    }
}
```

`[DisallowConcurrentExecution]` keeps one fire per job key in flight across the cluster; it costs
nothing because the body returns in milliseconds. If the attribute does not exist under that name
in Quartz 4.1, check `JobBuilder`/`IJobConfigurator.DisallowConcurrentExecution` (both verified to
exist) and set it in the synchronizer's `JobBuilder` instead — do not drop it.

- [ ] **Step 2: `ScheduleSynchronizer`**

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace AiFramework.Infrastructure.Jobs.Scheduling;

/// <summary>
/// Makes the store match the code: one durable job and one cron trigger per scheduled job, nothing
/// else in the <see cref="JobSchedules.Group"/> group. Runs once, at worker startup.
/// </summary>
/// <remarks>
/// <b>Never changes a trigger's paused state.</b> A pause (piece 5's monitoring page) must survive
/// redeploys, so a paused trigger whose cron is updated is paused again immediately. With several
/// workers starting together, Quartz's cluster locks serialise the writes and every node writes the
/// same values.
/// </remarks>
public sealed partial class ScheduleSynchronizer(
    ISchedulerFactory schedulers,
    IOptions<JobOptions> options,
    ILogger<ScheduleSynchronizer> logger)
{
    public async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        var scheduler = await schedulers.GetScheduler(cancellationToken).ConfigureAwait(false);
        var scheduled = JobRegistration.Jobs.Where(j => j.IsScheduled).ToArray();

        foreach (var job in scheduled)
        {
            await UpsertAsync(scheduler, job, JobSchedules.EffectiveCron(job, options.Value), cancellationToken)
                .ConfigureAwait(false);
        }

        await RemoveStaleAsync(scheduler, scheduled, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertAsync(IScheduler scheduler, JobDescriptor job, string cron, CancellationToken ct)
    {
        var jobKey = JobSchedules.JobKeyFor(job);
        var triggerKey = JobSchedules.TriggerKeyFor(job);

        await scheduler.AddJob(
                JobBuilder.Create<EnqueueScheduledJob>()
                    .WithIdentity(jobKey)
                    .UsingJobData(JobSchedules.JobNameKey, job.Name)
                    .StoreDurably()
                    .Build(),
                AddJobOptions.Replacing,
                ct)
            .ConfigureAwait(false);

        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(cron, b => b.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed))
            .Build();

        var wasPaused = await scheduler.GetTriggerState(triggerKey, ct).ConfigureAwait(false) == TriggerState.Paused;

        if (await scheduler.GetTrigger(triggerKey, ct).ConfigureAwait(false) is null)
        {
            await scheduler.ScheduleJob(trigger, ScheduleJobOptions.Replacing, ct).ConfigureAwait(false);
        }
        else
        {
            await scheduler.RescheduleJob(triggerKey, trigger, ct).ConfigureAwait(false);
        }

        if (wasPaused)
        {
            await scheduler.PauseTrigger(triggerKey, ct).ConfigureAwait(false);
        }

        LogScheduled(logger, job.Name, cron, wasPaused);
    }

    private async Task RemoveStaleAsync(IScheduler scheduler, JobDescriptor[] scheduled, CancellationToken ct)
    {
        var wanted = scheduled.Select(j => j.Name).ToHashSet(StringComparer.Ordinal);
        var stale = new List<JobKey>();

        // Paged: one page is enough for any realistic job count, but reading HasMore means a large
        // store cannot silently leave stale jobs behind.
        var query = new JobQuery { Group = GroupMatcher<JobKey>.GroupEquals(JobSchedules.Group) };
        var page = await scheduler.QueryJobs(query, ct).ConfigureAwait(false);
        stale.AddRange(page.Items.Select(h => h.Key).Where(k => !wanted.Contains(k.Name)));

        if (page.HasMore)
        {
            throw new InvalidOperationException(
                $"More jobs in the '{JobSchedules.Group}' group than one page holds; the synchronizer " +
                "needs to page through them before it can safely remove stale ones.");
        }

        foreach (var key in stale)
        {
            await scheduler.DeleteJob(key, ct).ConfigureAwait(false);
            LogRemoved(logger, key.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Schedule {JobName} = '{Cron}' (paused: {Paused}).")]
    private static partial void LogScheduled(ILogger logger, string jobName, string cron, bool paused);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Removed schedule {JobName}: no longer a scheduled job in this build.")]
    private static partial void LogRemoved(ILogger logger, string jobName);
}
```

Information for a schedule is deliberate: it happens once per startup and is exactly what an
operator checks when a job did not run. Removal is Warning because it stops work.

- [ ] **Step 3: `QuartzRegistration`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

namespace AiFramework.Infrastructure.Jobs.Scheduling;

/// <summary>
/// The worker's scheduler. Called from src/Worker/Program.cs ONLY — the API never starts one.
/// </summary>
public static class QuartzRegistration
{
    /// <summary>
    /// Schema-qualified: Quartz's tables live in their own `quartz` schema, beside `wolverine` and
    /// `wolverine_queues`, apart from EF's `public`. Must match the AddQuartzSchema migration.
    /// </summary>
    public const string TablePrefix = "quartz.qrtz_";

    public static IServiceCollection AddJobScheduling(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddQuartz(q =>
        {
            q.UsePersistentStore(store =>
            {
                store.UsePostgres(connectionString);
                store.ConfigureStore(o =>
                {
                    o.TablePrefix = TablePrefix;

                    // Quartz's own default, set explicitly because it is load-bearing: the EF
                    // migration owns this schema, and a worker must refuse to start against a
                    // missing or stale one rather than create it behind the migration's back.
                    o.SchemaProvisioning = SchemaProvisioning.Validate;
                });

                // Forced, not chosen: a trigger must fire on exactly one worker, and a pause must
                // apply to all of them.
                store.UseClustering(c => c.Enabled = true);
            });

            q.AddQuartzHealthChecks();
        });

        services.AddSingleton<ScheduleSynchronizer>();
        services.AddHostedService<ScheduleSynchronizerService>();

        // Registered AFTER the synchronizer's hosted service, so the store matches the code before
        // the scheduler begins acquiring triggers.
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

        return services;
    }

    private sealed class ScheduleSynchronizerService(ScheduleSynchronizer synchronizer) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            synchronizer.SynchronizeAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
```

If `AddQuartzHealthChecks` does not register onto the host's health checks by itself (it is on the
Quartz builder), wire it through `builder.Services.AddHealthChecks().AddQuartz(...)` in Task 7
instead — both extension methods exist in 4.1.

- [ ] **Step 4: Build**

Run: `dotnet build src/Infrastructure -c Debug`
Expected: `Build succeeded.` Fix analyzer findings in place (no suppressions without a comment).

- [ ] **Step 5: Commit**

```bash
git add src/Infrastructure/Jobs/Scheduling
git commit -m "feat(jobs): EnqueueScheduledJob, the schedule synchronizer and Quartz registration"
```

---

## Task 5: The `AddQuartzSchema` migration

**Files:**
- Create: `src/Infrastructure/Persistence/Migrations/<ts>_AddQuartzSchema.cs` (+ Designer, generated)

**Interfaces:**
- Consumes: the SQL transformation recorded in Task 1's result.
- Produces: a `quartz` schema holding Quartz 4.1's tables under prefix `qrtz_`, created by
  `dotnet ef database update` and by the Kubernetes `migrate` Job.

- [ ] **Step 1: Scaffold an empty migration**

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef migrations add AddQuartzSchema --project src/Infrastructure --startup-project src/Infrastructure
```

Expected: a new migration with empty `Up`/`Down` (there is no model change) and an unchanged model
snapshot. It is not yet committed, so it is still yours to edit.

- [ ] **Step 2: Generate the SQL body from the installed package**

Do not hand-copy from GitHub: the script must be the one Quartz 4.1.0 itself validates against.
Extract it with the transformation Task 1 proved:

```bash
S="$(mktemp -d)"; cd "$S" && dotnet new console -f net10.0 -o . >/dev/null && dotnet add package Quartz --version 4.1.0 >/dev/null
cat > Program.cs <<'EOF'
var asm = typeof(Quartz.IJob).Assembly;
using var s = asm.GetManifestResourceStream("Quartz.Impl.AdoJobStore.Schema.create_postgres.sql")!;
var sql = new StreamReader(s).ReadToEnd()
    .Replace("{0}", "quartz.qrtz_").Replace("{1}", "qrtz_");
File.WriteAllText("quartz_4.1.0_postgres.sql", sql);
EOF
dotnet run && cp quartz_4.1.0_postgres.sql "$OLDPWD/"
```

- [ ] **Step 3: Fill in the migration**

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Quartz 4.1.0's own Postgres schema, in a `quartz` schema, prefix `qrtz_`. ADR 0017.
    /// </summary>
    /// <remarks>
    /// The SQL is COPIED from Quartz.Impl.AdoJobStore.Schema.create_postgres.sql in the 4.1.0
    /// package, with {0} = quartz.qrtz_ and {1} = qrtz_. It is copied rather than read from the
    /// package at run time on purpose: an applied migration must never change, and reading the
    /// resource would make a Quartz upgrade silently change what this migration does on a fresh
    /// database. A Quartz upgrade that changes the schema is a NEW migration, taken from Quartz's
    /// upgrade script for that version; the worker's SchemaProvisioning.Validate is what catches
    /// forgetting to write one.
    /// </remarks>
    public partial class AddQuartzSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS quartz;");

            foreach (var statement in QuartzSchema.Split("--;;", System.StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(StripComments(statement)))
                {
                    migrationBuilder.Sql(statement);
                }
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS quartz CASCADE;");
        }

        private static string StripComments(string sql) =>
            string.Join('\n', sql.Split('\n').Where(line => !line.TrimStart().StartsWith("--", System.StringComparison.Ordinal)));

        // Paste the ENTIRE contents of quartz_4.1.0_postgres.sql between the quotes, unmodified.
        private const string QuartzSchema = """
            <contents of quartz_4.1.0_postgres.sql>
            """;
    }
}
```

The `<contents …>` line is the one place the file is pasted rather than typed: replace it with the
generated file's full text (a raw string literal needs no escaping). Delete
`quartz_4.1.0_postgres.sql` from the repo root afterwards — the migration is the only copy.

If Task 1 recorded that the statements can run as one command, the split loop may be replaced by a
single `migrationBuilder.Sql(QuartzSchema)`; keep whichever Task 1 proved.

- [ ] **Step 4: Apply it and confirm Quartz accepts it**

```bash
docker compose up -d --wait
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
docker compose exec -T postgres psql -U postgres -d aiframework -c "select count(*) from information_schema.tables where table_schema='quartz';"
```

Expected: `update` succeeds; the count is `12`.

Then prove the round trip:

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef database update AddOutboxTraceParent --project src/Infrastructure --startup-project src/Infrastructure
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
```

Expected: both succeed; the `quartz` schema is dropped and recreated.

- [ ] **Step 5: Run the existing suites** — every integration fixture runs `MigrateAsync`, so this
proves the migration applies on a fresh container too.

Run: `dotnet test tests/Infrastructure.Tests -c Debug` then `dotnet test tests/Api.IntegrationTests -c Debug`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Infrastructure/Persistence/Migrations
git commit -m "feat(jobs): AddQuartzSchema migration from Quartz 4.1.0's bundled Postgres script"
```

---

## Task 6: Wire the worker, and prove scheduling end to end

**Files:**
- Modify: `src/Worker/Program.cs`
- Modify: `src/Worker/Observability/WorkerObservability.cs`
- Modify: `src/Worker/Internal/Generated/**` (regenerated)
- Test: `tests/Worker.IntegrationTests/Jobs/SchedulingTests.cs`

**Interfaces:**
- Consumes: `AddJobScheduling`, `ScheduleSynchronizer`, `JobSchedules`, `PruneProcessedOutbox` (Tasks 2–5).

- [ ] **Step 1: Write the failing tests**

`tests/Worker.IntegrationTests/Jobs/SchedulingTests.cs`:

```csharp
using AiFramework.Application.Maintenance;
using AiFramework.Infrastructure.Jobs;
using AiFramework.Infrastructure.Jobs.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Wolverine.Tracking;

namespace AiFramework.Worker.IntegrationTests.Jobs;

/// <summary>
/// The scheduler, against the real worker host and the migrated schema. That the host starts at
/// all is the first assertion: Quartz runs in SchemaProvisioning.Validate, so a vendored script that
/// does not match 4.1.0 fails WorkerFactory's startup before any test body runs.
/// </summary>
[Collection(nameof(WorkerFactoryCollection))]
public sealed class SchedulingTests(WorkerFactory factory)
{
    private static JobDescriptor Prune => JobSchedules.Find(nameof(PruneProcessedOutbox))!;

    private async Task<IScheduler> SchedulerAsync() =>
        await factory.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(CancellationToken.None);

    private ScheduleSynchronizer Synchronizer => factory.Services.GetRequiredService<ScheduleSynchronizer>();

    [Fact]
    public async Task EveryScheduledJob_HasACronTriggerAfterStartup()
    {
        var scheduler = await SchedulerAsync();

        var trigger = await scheduler.GetTrigger(JobSchedules.TriggerKeyFor(Prune), CancellationToken.None);

        trigger.Should().BeAssignableTo<ICronTrigger>()
            .Which.CronExpressionString.Should().Be("0 5 * * * ?");
    }

    [Fact]
    public async Task APausedTrigger_StaysPausedAcrossAResync()
    {
        var scheduler = await SchedulerAsync();
        var key = JobSchedules.TriggerKeyFor(Prune);
        await scheduler.PauseTrigger(key, CancellationToken.None);

        try
        {
            await Synchronizer.SynchronizeAsync(CancellationToken.None);

            (await scheduler.GetTriggerState(key, CancellationToken.None))
                .Should().Be(TriggerState.Paused, "a pause from the monitoring page must survive redeploys");
        }
        finally
        {
            await scheduler.ResumeTrigger(key, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AJobNoLongerScheduled_IsRemovedOnResync()
    {
        var scheduler = await SchedulerAsync();
        var orphan = new JobKey("RetiredJob", JobSchedules.Group);
        await scheduler.AddJob(
            JobBuilder.Create<EnqueueScheduledJob>().WithIdentity(orphan).StoreDurably().Build(),
            AddJobOptions.Replacing,
            CancellationToken.None);

        await Synchronizer.SynchronizeAsync(CancellationToken.None);

        (await scheduler.CheckExists(orphan, CancellationToken.None)).Should().BeFalse(
            "removing .Scheduled(...) from the code must actually stop the job");
    }

    [Fact]
    public async Task FiringTheTrigger_EnqueuesTheJob_AndWolverineRunsIt()
    {
        var scheduler = await SchedulerAsync();
        var host = factory.Services.GetRequiredService<IHost>();

        var tracked = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(TimeSpan.FromSeconds(30))
            .WaitForExecutionOf<PruneProcessedOutbox>()
            .ExecuteAndWaitAsync(_ => scheduler.TriggerJob(
                JobSchedules.JobKeyFor(Prune), new JobDataMap(), CancellationToken.None).AsTask());

        tracked.Executed.SingleMessage<PruneProcessedOutbox>().Should().NotBeNull(
            "Quartz only decides when; Wolverine must be the one that runs the job");
    }
}
```

`CheckExists`, `WaitForExecutionOf<T>` and `TriggerJob(...).AsTask()` are the three calls in this
file not compiled in the Task 1 probe. If a name differs in 4.1 / Wolverine 6.33, look it up in the
package XML (`~/.nuget/packages/quartz/4.1.0/lib/net10.0/Quartz.xml`,
`~/.nuget/packages/wolverinefx/6.33.0/lib/net10.0/Wolverine.xml`) and use the real one.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Worker.IntegrationTests -c Debug --filter "FullyQualifiedName~SchedulingTests"`
Expected: FAIL — `ISchedulerFactory` is not registered (the worker has no Quartz yet).

- [ ] **Step 3: Wire the worker**

In `src/Worker/Program.cs`, after `builder.Services.AddInfrastructure(connectionString);`:

```csharp
// The job CLOCK (ADR 0017). Here and only here: the API never starts a scheduler. Quartz fires a
// schedule on exactly one worker and enqueues the job; Wolverine, configured below, runs it.
builder.Services.AddJobScheduling(connectionString);
```

and add `using AiFramework.Infrastructure.Jobs.Scheduling;`.

In `src/Worker/Observability/WorkerObservability.cs`, in `ConfigureTracing`, add the source beside
`"Wolverine"`:

```csharp
            // A trigger firing and the Wolverine job it enqueues then share one trace.
            .AddSource(Quartz.Diagnostics.QuartzInstrumentation.ActivitySourceName)
```

The worker project gets `Quartz` transitively through Infrastructure; if the compiler cannot see
`Quartz.Diagnostics`, add no package reference — use the literal `"Quartz"` with a comment naming
the constant it mirrors.

- [ ] **Step 4: Regenerate the worker's adapters** (a new handler, `PruneProcessedOutboxHandler`)

```bash
ConnectionStrings__Default='Host=localhost;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false dotnet run --project src/Worker -- codegen write
```

Expected: a new `PruneProcessedOutboxHandler*.cs` under `src/Worker/Internal/Generated/WolverineHandlers`.

- [ ] **Step 5: Run the worker suite**

Run: `dotnet test tests/Worker.IntegrationTests -c Debug`
Expected: all pass — the four new tests and the existing five, including `WorkerCodegenTests`.

If `WorkerCodegenTests` fails on the Quartz registration (it builds a bare host with
`AddInfrastructure` only), that is expected to be fine because `AddJobScheduling` is not part of
`AddInfrastructure`; if it does fail, read the error before changing anything.

- [ ] **Step 6: Commit**

```bash
git add src/Worker tests/Worker.IntegrationTests/Jobs/SchedulingTests.cs
git commit -m "feat(worker): start the clustered Quartz scheduler and prove schedules fire through Wolverine"
```

---

## Task 7: Take the retention sweep out of the poll loop; keep Quartz out of the API

**Files:**
- Modify: `src/Infrastructure/Outbox/OutboxHostedServices.cs`
- Modify: `src/Infrastructure/Outbox/OutboxOptions.cs`
- Modify: `tests/Infrastructure.Tests/Outbox/OutboxRegistrationTests.cs`
- Test: `tests/Api.IntegrationTests/Jobs/ApiHasNoSchedulerTests.cs`

- [ ] **Step 1: Write the failing API test**

`tests/Api.IntegrationTests/Jobs/ApiHasNoSchedulerTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace AiFramework.Api.IntegrationTests.Jobs;

/// <summary>
/// The scheduler half of "jobs never run in the API" (ADR 0016, ADR 0017). Piece 5 will give the
/// API a scheduler it never starts, for pause/resume; until then, it has none at all.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class ApiHasNoSchedulerTests(ApiFactory factory)
{
    [Fact]
    public void TheApi_RegistersNoQuartzScheduler()
    {
        factory.Services.GetService<ISchedulerFactory>().Should().BeNull(
            "Quartz is started only in the worker; an API that fires schedules competes with requests");
    }
}
```

Run: `dotnet test tests/Api.IntegrationTests -c Debug --filter "FullyQualifiedName~ApiHasNoScheduler"`
Expected: **PASS already** — nothing in `AddInfrastructure` registers Quartz. This test pins that;
confirm it fails by temporarily adding `builder.Services.AddQuartz();` to `src/Api/Program.cs`,
then remove the line.

- [ ] **Step 2: Remove the sweep from the poll loop**

In `src/Infrastructure/Outbox/OutboxHostedServices.cs`:
- delete the `_nextPruneDueAt` field and its comment;
- delete the `DueForPrune()` method;
- in `RunPollCycleAsync`, delete the block

```csharp
            if (batch.Count == 0 && DueForPrune())
            {
                await poller.PruneAsync(stoppingToken).ConfigureAwait(false);
            }
```

  and its comment;
- remove the now-unused `IClock clock` constructor parameter from `OutboxPollerService` if nothing
  else uses it, and its `using` if that leaves it unused;
- update the class summary: the poller claims and hands off; retention is the scheduled
  `PruneProcessedOutbox` job (ADR 0017).

In `src/Infrastructure/Outbox/OutboxOptions.cs`, delete `PruneInterval` and its doc comment. Keep
`RetentionPeriod`, and change its doc to say it is read by `PruneProcessedOutbox` through
`OutboxRetention`.

In `tests/Infrastructure.Tests/Outbox/OutboxRegistrationTests.cs`, update the comment above
`services.AddSingleton<IClock, SystemClock>();` — it no longer schedules a sweep. If
`OutboxPollerService` no longer takes `IClock`, delete that registration line too and check the
test still passes.

- [ ] **Step 3: Build and run the affected suites**

Run: `dotnet build -c Debug`, then
`dotnet test tests/Infrastructure.Tests -c Debug`,
`dotnet test tests/Api.IntegrationTests -c Debug`,
`dotnet test tests/Worker.IntegrationTests -c Debug`.
Expected: all pass. `OutboxPollerTests.PruneAsync_DeletesProcessedRowsPastRetentionButKeepsDeadOnes`
still passes — `PruneAsync` itself is unchanged.

- [ ] **Step 4: Commit**

```bash
git add src/Infrastructure/Outbox tests/Infrastructure.Tests/Outbox tests/Api.IntegrationTests/Jobs/ApiHasNoSchedulerTests.cs
git commit -m "refactor(outbox): retention runs as the scheduled PruneProcessedOutbox job, not in every poller"
```

---

## Task 8: Record the decision and teach the framework

**Files:**
- Create: `docs/adr/0017-quartz-as-the-job-clock.md`
- Modify: `docs/adr/0016-jobs-in-a-worker-host.md`, `CLAUDE.md`, `src/Worker/CLAUDE.md`, `.claude/commands/job.md`

- [ ] **Step 1: ADR 0017** — use `/adr` format (Context / Decision / Consequences / Alternatives).
Content comes from the spec: Context = the recurring-pattern hole and the roadmap; Decision = Quartz
4.1 as clock only, schedules on the registration with config override, clustered Postgres store in
`quartz`, EF migration with vendored SQL, `Validate`, worker only; Consequences = the spec's costs
list plus Task 1's findings; Alternatives = self-rescheduling messages (the ADR 0016 approach and its
three weaknesses), Quartz executing jobs directly, Quartz executing all jobs, a separate SQL step for
the schema, `CreateIfMissing`, Kubernetes CronJob (no parity with the compose loop), a Wolverine
agent (no persisted pause state, no misfire handling).

- [ ] **Step 2: ADR 0016** — append a short "Superseded in part by ADR 0017" note: recurring jobs
are no longer self-rescheduling messages.

- [ ] **Step 3: `CLAUDE.md`, Jobs section** — replace the "Recurring jobs are self-rescheduling
durable messages … No Quartz, no timer `IHostedService`" sentences with:

```markdown
**Scheduled jobs use Quartz as the clock (ADR 0017).** Declare one with
`JobDescriptor.Scheduled<TJob>("0 5 * * * ?")` — a Quartz cron, seconds first — and it fires on
exactly one worker, which enqueues it; Wolverine runs it like any other job. Only parameterless jobs
can be scheduled, and the compiler enforces it. Override per environment with
`Jobs__Schedules__<JobName>`; an unknown job or invalid cron fails worker startup. Quartz's tables
live in the `quartz` schema, created by an EF migration — **a Quartz upgrade that changes its schema
is a new migration**, and the worker's `SchemaProvisioning.Validate` refuses to start until it
exists. Never start a scheduler in the API.
```

Add a sixth "thing that will cost you time": *Quartz 4 is not Quartz 3* — list the verified name
changes from this plan's Global Constraints.

- [ ] **Step 4: `src/Worker/CLAUDE.md`** — a "Scheduling" section: `AddJobScheduling`, the
synchronizer's pause-preserving rule, the `jobs` group, `DisallowConcurrentExecution`, the
`Quartz` activity source, and that `EnqueueScheduledJob` is the only Quartz job there should be.

- [ ] **Step 5: `.claude/commands/job.md`** — in step 5, replace the self-rescheduling snippet with
"to run on a schedule, register it with `JobDescriptor.Scheduled<TJob>(cron)`; the job must have a
parameterless constructor", and remove "Do not add Quartz".

- [ ] **Step 6: Commit**

```bash
git add docs/adr CLAUDE.md src/Worker/CLAUDE.md .claude/commands/job.md
git commit -m "docs(jobs): ADR 0017 and framework guidance for scheduled jobs"
```

---

## Task 9: Finish

- [ ] `dotnet build -c Debug` and `dotnet build -c Release` — both clean.
- [ ] `dotnet test` each of the five test projects, Debug **and** Release.
- [ ] `dotnet run --project src/Worker -- codegen write` and `dotnet run --project src/Api -- codegen write`
      (placeholder connection string, `Wolverine__Durable=false`) — no diff.
- [ ] `./scripts/dev.ps1`, then confirm the worker log shows
      `Schedule PruneProcessedOutbox = '0 5 * * * ?' (paused: False).`; `./scripts/stop-dev.ps1`.
- [ ] `./deploy/deploy.ps1` (the `migrate` Job applies `AddQuartzSchema`), then confirm in the worker
      log that the scheduler started clustered, and run `./deploy/e2e-k8s.ps1`.
- [ ] Dispatch `dotnet-reviewer` over `git diff main...HEAD`.
- [ ] `openapi/AiFramework.Api.json` unchanged.
