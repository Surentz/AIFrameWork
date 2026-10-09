# External systems monitoring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show an administrator every external system's health and traffic — a status strip on the Monitoring overview and a `/monitoring/integrations` page — backed by a status table the worker keeps current, two alert rules, and an e2e check against a real mTLS partner.

**Architecture:** The worker runs the `external`-tagged health checks every minute through ASP.NET Core's `IHealthCheckPublisher` (not a Quartz job — see Global Constraints) and upserts one row per system into `external_system_status`. The API's `GET /api/monitoring/external-systems` joins those rows with the last hour of `Outbound`/`OutboundAttempt` traffic. A new `AiFramework.ExternalSystems` meter exports a call counter and a certificate-expiry gauge for two Prometheus alert rules. The partner simulator gains a `serve` mode so e2e and `dev.ps1 -WithPartners` run a real mTLS partner.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, `Microsoft.Extensions.Diagnostics.HealthChecks` (publisher), `System.Diagnostics.Metrics` + OpenTelemetry, React 19 + TanStack Query 5 + MSW 3 + Vitest 5, Playwright, Prometheus 3 (promtool).

**Spec:** [`docs/superpowers/specs/2026-10-06-external-systems-design.md`](../specs/2026-10-06-external-systems-design.md) §3, §4 and §6's PR 3 row. PR 2's plan and ledger: [`2026-10-06-external-systems-plumbing.md`](2026-10-06-external-systems-plumbing.md).

**PR title:** `feat(monitoring): show external system health and traffic`, branch `claude/external-systems-monitoring`.

## Global Constraints

- **Decided with the user on 2026-10-09, overriding spec §3:** the status checks run through ASP.NET Core's `IHealthCheckPublisher` in the **worker only**, every `Monitoring:ExternalSystemStatusPeriod` (default 1 minute) — **not** a Quartz job. A minutely job would add ~1,440 `job_runs` rows a day (~43,000 retained) and bury the real jobs on the Jobs page. No Wolverine handler is added, so **no `codegen write` is needed**.
- Warnings are errors (Debug and Release). Fix analyzer diagnostics; suppress only with a `#pragma warning disable`/`restore` pair and a justification comment directly above.
- Never `catch (Exception)`; `throw;` never `throw ex;`. `!` needs an adjacent justification comment.
- Never hand-edit an applied migration. The new migration is generated, not written.
- Descriptions that reach the browser never contain a host name, port, file path, response body or secret. A health check that **threw** is described as `check failed: <ExceptionTypeName>`, never by its message.
- `/health/ready` in both hosts keeps excluding `external` checks (PR 2) — nothing here may add the publisher to the API.
- Status rows older than **3 minutes** read as stale (the worker is not running), never as their last status.
- Monitoring endpoints sit behind `AuthorizationPolicies.Monitoring.Read` on the controller (ADR 0020). Monitoring queries are **not** `ICacheable` (ADR 0021).
- Metric names, exactly: counter `aiframework.external_system.calls` (unit `{call}`, tags `system`, `outcome` ∈ `succeeded`/`failed`/`faulted`); observable gauge `aiframework.external_system.certificate.time_remaining` (unit `s`, tag `system`). Meter name `AiFramework.ExternalSystems`. Prometheus sees them as `aiframework_external_system_calls_total` and `aiframework_external_system_certificate_time_remaining_seconds`.
- Alert thresholds, exactly (spec §3): certificate under **14 days**; more than **20%** of a system's calls `faulted` over **5 minutes** with at least **10 calls** in that window.
- No private key is committed. e2e certificates are generated into `frontend/e2e/.certs/` at run time; dev ones into `.certs/` (both git-ignored by the existing `.certs/` pattern).
- Ports, exactly: dev simulator HTTPS **55690**, its health **55691**; e2e simulator HTTPS **55692**, its health **55693**.
- Test names `MethodName_Scenario_ExpectedOutcome`; AAA separated by blank lines; one behaviour per test.
- Commit subjects `feat(monitoring): …` (docs commits `docs(monitoring): …`), each ending with a blank line and `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Check `dotnet --list-sdks` shows 10.0.400 and `node --version` v24.20.0 before trusting a local result.

## Review Focus

1. **A worker that is down must not leave the page showing green.** Rows go stale after 3 minutes and the page says so. Pinned by Task 3 (`HandleAsync_WhenARowIsOlderThanThreeMinutes_MarksItStale`) and Task 7 (`IntegrationsPage_ForAStaleRow_SaysStaleNotHealthy`).
2. **A system removed from configuration must disappear, not linger as its last status.** Pinned by Task 1 (`ReplaceAsync_RemovesRowsForSystemsNoLongerConfigured`).
3. **A health check that throws must not leak its exception message** (it can carry a host or path) through the description to the browser. Pinned by Task 5 (`Summarize_WhenACheckThrew_DescribesTheExceptionTypeOnly`).
4. **A system with traffic but no status row (never checked yet, or only the API calls it) still appears**, with an unknown status rather than vanishing. Pinned by Task 3 (`HandleAsync_ForTrafficWithoutAStatusRow_ListsTheSystemWithNoState`).
5. **Two worker replicas publish the same rows.** The upsert must be last-write-wins and never a duplicate-key failure. Pinned by Task 1 (`ReplaceAsync_CalledTwiceForTheSameSystem_KeepsOneRowWithTheLatestValues`).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Application/Monitoring/ExternalSystemStatus.cs` | `ExternalSystemState`, the status view, `IExternalSystemStatusReader` |
| `src/Application/Monitoring/GetExternalSystemStatus.cs` | Query, row view and handler joining status with traffic |
| `src/Application/Monitoring/ITrafficReader.cs` | + `OutboundAsync` and `OutboundTrafficView` |
| `src/Infrastructure/Monitoring/ExternalSystemStatusRow.cs` | EF entity for `external_system_status` |
| `src/Infrastructure/Persistence/Configurations/ExternalSystemStatusRowConfiguration.cs` | Mapping |
| `src/Infrastructure/Persistence/Migrations/*_AddExternalSystemStatus.cs` | Generated migration |
| `src/Infrastructure/Monitoring/ExternalSystemStatusStore.cs` | Upsert + prune (the publisher's writer) and the reader |
| `src/Infrastructure/Monitoring/TrafficReader.cs` | + `OutboundAsync`, sharing one grouping query |
| `src/Infrastructure/ExternalSystems/ExternalSystemMetrics.cs` | Meter, call counter, certificate gauge |
| `src/Infrastructure/ExternalSystems/Health/ExternalSystemStatusPublisher.cs` | `IHealthCheckPublisher` → store + gauge |
| `src/Infrastructure/ExternalSystems/ExternalSystemsRegistration.cs` | + `AddExternalSystemStatusPublisher`, check timeouts |
| `src/Api/Monitoring/MonitoringExternalSystemsController.cs` + DTOs | The endpoint |
| `src/Worker/Program.cs`, both observability registrations | Publisher (worker only); `AddMeter` (both) |
| `frontend/src/features/monitoring/IntegrationsPage.tsx` (+ test) | The page |
| `frontend/src/features/monitoring/MonitoringPage.tsx` (+ test) | The overview strip |
| `k8s/components/observability/prometheus-rules.yml` (+ test) | Two alerts |
| `tests/PartnerSimulator/*` | `serve` mode with a plain-HTTP health port |
| `frontend/playwright.config.ts`, `frontend/e2e/**` | Simulator web server, worker config, the spec |
| `scripts/dev.ps1`, `scripts/stop-dev.ps1` | `-WithPartners` |
| `docs/adr/0032-*.md`, spec, skills, README | Docs |

---

### Task 0: Branch

- [ ] **Step 1:** This plan is committed on `claude/external-systems-monitoring-plan`, branched from `main` at `712e4c0`. Implement on a branch from that commit: `git switch claude/external-systems-monitoring-plan && git switch -c claude/external-systems-monitoring`. If `main` has moved, `git rebase main` first.

---

### Task 1: The status table, its writer and its reader

**Files:**
- Create: `src/Application/Monitoring/ExternalSystemStatus.cs`
- Create: `src/Infrastructure/Monitoring/ExternalSystemStatusRow.cs`
- Create: `src/Infrastructure/Persistence/Configurations/ExternalSystemStatusRowConfiguration.cs`
- Create: `src/Infrastructure/Monitoring/ExternalSystemStatusStore.cs`
- Modify: `src/Infrastructure/Persistence/AiFrameworkDbContext.cs` (DbSet)
- Modify: `src/Infrastructure/Monitoring/MonitoringRegistration.cs` (register store + reader)
- Create (generated): `src/Infrastructure/Persistence/Migrations/<timestamp>_AddExternalSystemStatus.cs` + Designer + snapshot update
- Test: `tests/Infrastructure.Tests/Monitoring/ExternalSystemStatusStoreTests.cs`

**Interfaces:**
- Produces (Application): `enum ExternalSystemState { Healthy, Degraded, Unhealthy }`; `record ExternalSystemStatusView(string Name, ExternalSystemState State, string? Description, DateTimeOffset CheckedAt, DateTimeOffset? CertificateNotAfter, bool? TokenOk)`; `interface IExternalSystemStatusReader { Task<IReadOnlyList<ExternalSystemStatusView>> ListAsync(CancellationToken) }`.
- Produces (Infrastructure, internal): `interface IExternalSystemStatusStore { Task ReplaceAsync(IReadOnlyCollection<ExternalSystemStatusView> current, CancellationToken) }` — the publisher's writer, reusing the view record as its input.

- [ ] **Step 1: Application types**

`src/Application/Monitoring/ExternalSystemStatus.cs`:

```csharp
namespace AiFramework.Application.Monitoring;

/// <summary>An external system's health as its checks last saw it. The worst of its checks.</summary>
public enum ExternalSystemState
{
    Healthy,
    Degraded,
    Unhealthy,
}

/// <summary>One row of <c>external_system_status</c>: the worker's last look at one system.</summary>
public sealed record ExternalSystemStatusView(
    string Name,
    ExternalSystemState State,
    string? Description,
    DateTimeOffset CheckedAt,
    DateTimeOffset? CertificateNotAfter,
    bool? TokenOk);

/// <summary>Reads what the worker's status publisher wrote. ADR 0031, ADR 0032.</summary>
public interface IExternalSystemStatusReader
{
    public Task<IReadOnlyList<ExternalSystemStatusView>> ListAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the failing store tests**

Read `tests/Infrastructure.Tests/Monitoring/TrafficReaderTests.cs` first and copy its class attributes, constructor and context-creation helper exactly (it joins `PostgresCollection`). Then `tests/Infrastructure.Tests/Monitoring/ExternalSystemStatusStoreTests.cs`:

```csharp
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Monitoring;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Monitoring;

[Collection(nameof(PostgresCollection))]
public sealed class ExternalSystemStatusStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    // Unique per test: the collection shares one database.
    private static string Unique(string name) => $"{name}-{Guid.NewGuid():N}"[..40];

    private static ExternalSystemStatusView Row(string name, ExternalSystemState state, DateTimeOffset at) =>
        new(name, state, $"{name} is {state}", at, at.AddDays(90), TokenOk: true);

    [Fact]
    public async Task ReplaceAsync_ThenListAsync_ReturnsWhatWasWritten()
    {
        var name = Unique("partner");
        await using var context = fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);

        await store.ReplaceAsync([Row(name, ExternalSystemState.Degraded, At)], CancellationToken.None);

        var rows = await store.ListAsync(CancellationToken.None);
        rows.Should().ContainSingle(r => r.Name == name)
            .Which.Should().Be(Row(name, ExternalSystemState.Degraded, At));
    }

    [Fact]
    public async Task ReplaceAsync_CalledTwiceForTheSameSystem_KeepsOneRowWithTheLatestValues()
    {
        var name = Unique("partner");
        await using var context = fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);

        await store.ReplaceAsync([Row(name, ExternalSystemState.Healthy, At)], CancellationToken.None);
        await store.ReplaceAsync([Row(name, ExternalSystemState.Unhealthy, At.AddMinutes(1))], CancellationToken.None);

        var rows = await store.ListAsync(CancellationToken.None);
        rows.Where(r => r.Name == name).Should().ContainSingle()
            .Which.State.Should().Be(ExternalSystemState.Unhealthy);
    }

    [Fact]
    public async Task ReplaceAsync_RemovesRowsForSystemsNoLongerConfigured()
    {
        var kept = Unique("kept");
        var removed = Unique("removed");
        await using var context = fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);
        await store.ReplaceAsync(
            [Row(kept, ExternalSystemState.Healthy, At), Row(removed, ExternalSystemState.Healthy, At)],
            CancellationToken.None);

        await store.ReplaceAsync([Row(kept, ExternalSystemState.Healthy, At)], CancellationToken.None);

        var names = (await store.ListAsync(CancellationToken.None)).Select(r => r.Name).ToList();
        names.Should().Contain(kept).And.NotContain(removed);
    }

    [Fact]
    public async Task ReplaceAsync_TruncatesALongDescription()
    {
        var name = Unique("partner");
        await using var context = fixture.CreateContext();
        var store = new ExternalSystemStatusStore(context);

        await store.ReplaceAsync(
            [Row(name, ExternalSystemState.Unhealthy, At) with { Description = new string('x', 2000) }],
            CancellationToken.None);

        var row = (await store.ListAsync(CancellationToken.None)).Single(r => r.Name == name);
        row.Description.Should().HaveLength(ExternalSystemStatusStore.MaxDescriptionLength);
    }
}
```

`ReplaceAsync_RemovesRowsForSystemsNoLongerConfigured` deletes every row not in its input — **other tests' rows too**, because the collection shares a database. That is the real behaviour (one worker owns the whole table). The other tests in this class must therefore not depend on rows surviving across tests; each writes and reads its own row within one test, which is why every test re-writes before reading.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystemStatusStoreTests"`
Expected: build FAILS — `ExternalSystemStatusStore` does not exist.

- [ ] **Step 4: Entity and mapping**

`src/Infrastructure/Monitoring/ExternalSystemStatusRow.cs`:

```csharp
using AiFramework.Application.Monitoring;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// One external system's last observed health. One row per configured system, upserted by the
/// worker's status publisher every minute (ADR 0032); a system no longer configured is deleted.
/// </summary>
public sealed class ExternalSystemStatusRow
{
    public required string Name { get; init; }

    public required ExternalSystemState State { get; set; }

    public string? Description { get; set; }

    public required DateTimeOffset CheckedAt { get; set; }

    public DateTimeOffset? CertificateNotAfter { get; set; }

    public bool? TokenOk { get; set; }
}
```

`src/Infrastructure/Persistence/Configurations/ExternalSystemStatusRowConfiguration.cs`:

```csharp
using AiFramework.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class ExternalSystemStatusRowConfiguration : IEntityTypeConfiguration<ExternalSystemStatusRow>
{
    public void Configure(EntityTypeBuilder<ExternalSystemStatusRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("external_system_status");

        // The system name: 1-64 letters, digits or dashes, validated at startup (ExternalSystemsOptionsValidator).
        builder.HasKey(s => s.Name);
        builder.Property(s => s.Name).HasMaxLength(64);

        builder.Property(s => s.State).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(s => s.Description).HasMaxLength(ExternalSystemStatusStore.MaxDescriptionLength);
    }
}
```

Add to `AiFrameworkDbContext` beside `TrafficBuckets`:

```csharp
    public DbSet<ExternalSystemStatusRow> ExternalSystemStatuses => Set<ExternalSystemStatusRow>();
```

- [ ] **Step 5: Store**

`src/Infrastructure/Monitoring/ExternalSystemStatusStore.cs`:

```csharp
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>The status publisher's writer: one call replaces the table with the current picture.</summary>
internal interface IExternalSystemStatusStore
{
    public Task ReplaceAsync(IReadOnlyCollection<ExternalSystemStatusView> current, CancellationToken cancellationToken);
}

/// <summary>
/// <c>external_system_status</c>'s writer and reader. The write is an upsert per system plus a
/// delete of every other row: two worker replicas publish the same picture, so last write wins
/// and nothing can collide, and a system removed from configuration disappears rather than
/// lingering as its last status.
/// </summary>
internal sealed class ExternalSystemStatusStore(AiFrameworkDbContext context)
    : IExternalSystemStatusStore, IExternalSystemStatusReader
{
    public const int MaxDescriptionLength = 512;

    public async Task ReplaceAsync(
        IReadOnlyCollection<ExternalSystemStatusView> current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        foreach (var row in current)
        {
            var state = row.State.ToString();
            var description = row.Description is { Length: > MaxDescriptionLength }
                ? row.Description[..MaxDescriptionLength]
                : row.Description;

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO external_system_status
                    ("Name", "State", "Description", "CheckedAt", "CertificateNotAfter", "TokenOk")
                VALUES
                    ({row.Name}, {state}, {description}, {row.CheckedAt}, {row.CertificateNotAfter}, {row.TokenOk})
                ON CONFLICT ("Name") DO UPDATE SET
                    "State" = EXCLUDED."State",
                    "Description" = EXCLUDED."Description",
                    "CheckedAt" = EXCLUDED."CheckedAt",
                    "CertificateNotAfter" = EXCLUDED."CertificateNotAfter",
                    "TokenOk" = EXCLUDED."TokenOk"
                """,
                cancellationToken).ConfigureAwait(false);
        }

        var names = current.Select(row => row.Name).ToList();
        await context.ExternalSystemStatuses
            .Where(row => !names.Contains(row.Name))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExternalSystemStatusView>> ListAsync(CancellationToken cancellationToken) =>
        await context.ExternalSystemStatuses
            .AsNoTracking()
            .OrderBy(row => row.Name)
            .Select(row => new ExternalSystemStatusView(
                row.Name, row.State, row.Description, row.CheckedAt, row.CertificateNotAfter, row.TokenOk))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
```

Register in `MonitoringRegistration.AddMonitoring` beside `ITrafficReader`:

```csharp
        services.AddScoped<ExternalSystemStatusStore>();
        services.AddScoped<IExternalSystemStatusReader>(sp => sp.GetRequiredService<ExternalSystemStatusStore>());
        services.AddScoped<IExternalSystemStatusStore>(sp => sp.GetRequiredService<ExternalSystemStatusStore>());
```

- [ ] **Step 6: Generate the migration**

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef migrations add AddExternalSystemStatus \
  --project src/Infrastructure --startup-project src/Infrastructure
```

(`dotnet ef` reads only that environment variable; `src/Infrastructure` is both projects — `src/Infrastructure/CLAUDE.md`.) Expected: a migration creating `external_system_status` with `Name` (varchar 64, PK), `State` (varchar 16), `Description` (varchar 512, null), `CheckedAt`, `CertificateNotAfter` (null), `TokenOk` (null), and an updated model snapshot. Read the generated file; do not edit it unless it contains something other than this table.

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystemStatusStoreTests|FullyQualifiedName~Persistence"`
Expected: all PASS (the fixture applies migrations on start, so the new one is exercised).

- [ ] **Step 8: Commit** — `feat(monitoring): store each external system's last observed status`

---

### Task 2: Outbound traffic for the page

**Files:**
- Modify: `src/Application/Monitoring/ITrafficReader.cs`, `src/Application/Monitoring/MonitoringViews.cs`
- Modify: `src/Infrastructure/Monitoring/TrafficReader.cs`
- Test: `tests/Infrastructure.Tests/Monitoring/TrafficReaderTests.cs` (add)

**Interfaces:**
- Produces: `ITrafficReader.OutboundAsync(DateTimeOffset since, CancellationToken) : Task<IReadOnlyList<OutboundTrafficView>>`; `record OutboundTrafficView(string System, long Calls, long Failed, long Faulted, long Attempts, double? P95Ms)`.

- [ ] **Step 1: Write the failing test** — add to `TrafficReaderTests`, following the fixed-minute pattern its existing tests use (each test its own fixed minute in 2026-08, unique names):

```csharp
    [Fact]
    public async Task OutboundAsync_SumsCallsAndAttemptsPerSystem()
    {
        var minute = new DateTimeOffset(2026, 8, 25, 12, 9, 0, TimeSpan.Zero);
        var system = $"sys-{Guid.NewGuid():N}"[..20];
        var instance = $"pod-{Guid.NewGuid():N}"[..20];
        await using (var context = _fixture.CreateContext())
        {
            context.TrafficBuckets.AddRange(
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Outbound, Name = system, InstanceId = instance, Succeeded = 8, Failed = 1, Faulted = 1 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.OutboundAttempt, Name = system, InstanceId = instance, Succeeded = 8, Failed = 1, Faulted = 4 },
                new TrafficBucket { BucketStart = minute, Kind = TrafficKind.Http, Name = $"GET /{system}", InstanceId = instance, Succeeded = 50 });
            await context.SaveChangesAsync();
        }

        await using var reading = _fixture.CreateContext();
        var rows = await new TrafficReader(reading).OutboundAsync(minute.AddMinutes(-1), CancellationToken.None);

        var row = rows.Should().ContainSingle(r => r.System == system).Subject;
        row.Calls.Should().Be(10);
        row.Failed.Should().Be(1);
        row.Faulted.Should().Be(1);
        row.Attempts.Should().Be(13);
        rows.Should().NotContain(r => r.System == $"GET /{system}");
    }
```

`_fixture` is the class's existing `PostgresFixture` field.

- [ ] **Step 2: Run to verify it fails** — `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~TrafficReaderTests"`. Expected: build FAILS (`OutboundAsync` missing).

- [ ] **Step 3: Implement**

Append to `src/Application/Monitoring/MonitoringViews.cs`:

```csharp
/// <summary>One external system's outbound traffic over a window. Calls include retries in their duration.</summary>
public sealed record OutboundTrafficView(
    string System, long Calls, long Failed, long Faulted, long Attempts, double? P95Ms);
```

Add to `ITrafficReader`:

```csharp
    /// <summary>
    /// Per external system: calls (<c>Outbound</c>) and physical attempts (<c>OutboundAttempt</c>),
    /// summed across instances. The only read that sees outbound kinds; the others exclude them.
    /// </summary>
    public Task<IReadOnlyList<OutboundTrafficView>> OutboundAsync(
        DateTimeOffset since, CancellationToken cancellationToken);
```

In `TrafficReader`: **extract** the existing `SummarizeAsync` `GroupBy(new { Kind, Name }).Select(new Totals { … })…ToListAsync` into one private method both use, so the 15-line projection exists once:

```csharp
    private static readonly TrafficKind[] OutboundKinds = [TrafficKind.Outbound, TrafficKind.OutboundAttempt];

    private static Task<List<Totals>> TotalsByKindAndNameAsync(
        IQueryable<TrafficBucket> buckets, CancellationToken cancellationToken) =>
        buckets
            .GroupBy(bucket => new { bucket.Kind, bucket.Name })
            .Select(group => new Totals
            {
                // … the exact projection SummarizeAsync has today, moved here unchanged …
            })
            .ToListAsync(cancellationToken);
```

`SummarizeAsync` becomes `var grouped = await TotalsByKindAndNameAsync(context.TrafficBuckets.AsNoTracking().Where(bucket => bucket.BucketStart >= since && InboundKinds.Contains(bucket.Kind)), cancellationToken).ConfigureAwait(false);` — behaviour unchanged. Then:

```csharp
    public async Task<IReadOnlyList<OutboundTrafficView>> OutboundAsync(
        DateTimeOffset since, CancellationToken cancellationToken)
    {
        var grouped = await TotalsByKindAndNameAsync(
                context.TrafficBuckets.AsNoTracking()
                    .Where(bucket => bucket.BucketStart >= since && OutboundKinds.Contains(bucket.Kind)),
                cancellationToken)
            .ConfigureAwait(false);

        var attempts = grouped
            .Where(totals => totals.Kind == TrafficKind.OutboundAttempt)
            .ToDictionary(totals => totals.Name, totals => totals.Total, StringComparer.Ordinal);

        return grouped
            .Where(totals => totals.Kind == TrafficKind.Outbound)
            .Select(totals => new OutboundTrafficView(
                totals.Name,
                totals.Total,
                totals.Failed,
                totals.Faulted,
                attempts.GetValueOrDefault(totals.Name),
                TrafficHistogram.Percentile(totals.Histogram, 0.95)))
            .OrderBy(view => view.System, StringComparer.Ordinal)
            .ToList();
    }
```

If EF cannot translate the anonymous-key projection once it is a separate method (it should — the expression tree is identical), keep the method but inline the filtered `IQueryable` construction at each call site; never duplicate the projection.

- [ ] **Step 4: Run** — `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~Traffic"`. Expected: all PASS, including the existing summary/series tests (the refactor must not change them).

- [ ] **Step 5: Commit** — `feat(monitoring): read outbound calls and attempts per external system`

---

### Task 3: The query

**Files:**
- Create: `src/Application/Monitoring/GetExternalSystemStatus.cs`
- Modify: `src/Infrastructure/InfrastructureRegistration.cs` (`AddQuery`)
- Test: `tests/Application.Tests/Monitoring/GetExternalSystemStatusHandlerTests.cs`

**Interfaces:**
- Consumes: `IExternalSystemStatusReader`, `ITrafficReader.OutboundAsync`, `IClock`.
- Produces: `record GetExternalSystemStatus : IQuery<ExternalSystemsView>`; `record ExternalSystemsView(DateTimeOffset TrafficSince, IReadOnlyList<ExternalSystemRowView> Systems)`; `record ExternalSystemRowView(string Name, ExternalSystemState? State, string? Description, DateTimeOffset? CheckedAt, bool Stale, DateTimeOffset? CertificateNotAfter, bool? TokenOk, long Calls, long Failed, long Faulted, long Attempts, double? P95Ms)`; `GetExternalSystemStatusHandler.StaleAfter = TimeSpan.FromMinutes(3)`, `.TrafficWindow = TimeSpan.FromHours(1)`.

- [ ] **Step 1: Write the failing tests**

`tests/Application.Tests/Monitoring/GetExternalSystemStatusHandlerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Monitoring;

public sealed class GetExternalSystemStatusHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly IExternalSystemStatusReader _statuses = Substitute.For<IExternalSystemStatusReader>();
    private readonly ITrafficReader _traffic = Substitute.For<ITrafficReader>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public GetExternalSystemStatusHandlerTests() => _clock.UtcNow.Returns(Now);

    private async Task<ExternalSystemsView> HandleAsync(
        IReadOnlyList<ExternalSystemStatusView> statuses, IReadOnlyList<OutboundTrafficView> traffic)
    {
        _statuses.ListAsync(Arg.Any<CancellationToken>()).Returns(statuses);
        _traffic.OutboundAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(traffic);
        var result = await new GetExternalSystemStatusHandler(_statuses, _traffic, _clock)
            .HandleAsync(new GetExternalSystemStatus(), CancellationToken.None);
        return result.Value;
    }

    private static ExternalSystemStatusView Status(string name, DateTimeOffset checkedAt) =>
        new(name, ExternalSystemState.Healthy, "reachable (401)", checkedAt, Now.AddDays(60), TokenOk: true);

    [Fact]
    public async Task HandleAsync_JoinsStatusWithTrafficByName()
    {
        var view = await HandleAsync(
            [Status("Partner", Now.AddSeconds(-30))],
            [new OutboundTrafficView("Partner", Calls: 10, Failed: 1, Faulted: 2, Attempts: 13, P95Ms: 250)]);

        var row = view.Systems.Should().ContainSingle().Subject;
        row.State.Should().Be(ExternalSystemState.Healthy);
        row.Calls.Should().Be(10);
        row.Attempts.Should().Be(13);
        row.P95Ms.Should().Be(250);
        row.Stale.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_WhenARowIsOlderThanThreeMinutes_MarksItStale()
    {
        var view = await HandleAsync([Status("Partner", Now - TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1))], []);

        view.Systems.Single().Stale.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_ForTrafficWithoutAStatusRow_ListsTheSystemWithNoState()
    {
        var view = await HandleAsync([], [new OutboundTrafficView("Partner", 3, 0, 0, 3, 40)]);

        var row = view.Systems.Should().ContainSingle().Subject;
        row.State.Should().BeNull();
        row.CheckedAt.Should().BeNull();
        row.Stale.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ForAStatusWithoutTraffic_ReportsZeroCalls()
    {
        var view = await HandleAsync([Status("Partner", Now)], []);

        view.Systems.Single().Calls.Should().Be(0);
        view.Systems.Single().P95Ms.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ReadsTrafficForTheLastHour()
    {
        await HandleAsync([], []);

        await _traffic.Received(1).OutboundAsync(Now - TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run to verify they fail** — `dotnet test tests/Application.Tests --filter "FullyQualifiedName~GetExternalSystemStatusHandlerTests"`. Expected: build FAILS.

- [ ] **Step 3: Implement** `src/Application/Monitoring/GetExternalSystemStatus.cs`:

```csharp
using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>Every external system's last health and its last hour of traffic. Not cached (ADR 0021).</summary>
public sealed record GetExternalSystemStatus : IQuery<ExternalSystemsView>;

public sealed record ExternalSystemsView(DateTimeOffset TrafficSince, IReadOnlyList<ExternalSystemRowView> Systems);

/// <summary>
/// One system. <see cref="State"/> is null when the worker has never checked it (only traffic is
/// known). <see cref="Stale"/> means the worker stopped writing: the last status is history, not health.
/// </summary>
public sealed record ExternalSystemRowView(
    string Name,
    ExternalSystemState? State,
    string? Description,
    DateTimeOffset? CheckedAt,
    bool Stale,
    DateTimeOffset? CertificateNotAfter,
    bool? TokenOk,
    long Calls,
    long Failed,
    long Faulted,
    long Attempts,
    double? P95Ms);

public sealed class GetExternalSystemStatusHandler(
    IExternalSystemStatusReader statuses, ITrafficReader traffic, IClock clock)
    : IQueryHandler<GetExternalSystemStatus, ExternalSystemsView>
{
    /// <summary>Three missed one-minute checks: the worker is not running, or not reaching the database.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan TrafficWindow = TimeSpan.FromHours(1);

    public async Task<Result<ExternalSystemsView>> HandleAsync(
        GetExternalSystemStatus query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var now = clock.UtcNow;
        var since = now - TrafficWindow;
        var rows = await statuses.ListAsync(cancellationToken).ConfigureAwait(false);
        var calls = await traffic.OutboundAsync(since, cancellationToken).ConfigureAwait(false);

        var statusByName = rows.ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
        var trafficByName = calls.ToDictionary(row => row.System, StringComparer.OrdinalIgnoreCase);

        var systems = statusByName.Keys
            .Union(trafficByName.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var status = statusByName.GetValueOrDefault(name);
                var used = trafficByName.GetValueOrDefault(name);
                return new ExternalSystemRowView(
                    status?.Name ?? used!.System, // one of the two exists: name came from their union.
                    status?.State,
                    status?.Description,
                    status?.CheckedAt,
                    status is not null && now - status.CheckedAt > StaleAfter,
                    status?.CertificateNotAfter,
                    status?.TokenOk,
                    used?.Calls ?? 0,
                    used?.Failed ?? 0,
                    used?.Faulted ?? 0,
                    used?.Attempts ?? 0,
                    used?.P95Ms);
            })
            .ToList();

        return Result.Success(new ExternalSystemsView(since, systems));
    }
}
```

Register in `InfrastructureRegistration` beside the traffic queries:

```csharp
        services.AddQuery<GetExternalSystemStatus, ExternalSystemsView, GetExternalSystemStatusHandler>();
```

- [ ] **Step 4: Run** — the handler tests plus `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~RegistrationCompleteness"` (it fails on an unregistered query). Expected: PASS.

- [ ] **Step 5: Commit** — `feat(monitoring): join each external system's status with its traffic`

---

### Task 4: The meter

**Files:**
- Create: `src/Infrastructure/ExternalSystems/ExternalSystemMetrics.cs`
- Modify: `src/Infrastructure/ExternalSystems/Http/OutboundTrafficHandler.cs`, `ExternalSystemsBuilder.cs`, `ExternalSystemsRegistration.cs`
- Modify: `src/Api/Observability/ObservabilityRegistration.cs`, `src/Worker/Observability/WorkerObservability.cs` (`AddMeter`)
- Test: `tests/Infrastructure.Tests/ExternalSystems/ExternalSystemMetricsTests.cs`

**Interfaces:**
- Produces: `public sealed class ExternalSystemMetrics { const string MeterName = "AiFramework.ExternalSystems"; internal void RecordCall(string system, TrafficOutcome outcome); internal void SetCertificateNotAfter(string system, DateTimeOffset? notAfter); }`; `OutboundTrafficHandler` gains a 5th optional constructor parameter `ExternalSystemMetrics? metrics = null` (existing 4-argument call sites keep compiling).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Diagnostics.Metrics;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class ExternalSystemMetricsTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _provider;
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _seen = [];

    public ExternalSystemMetricsTests()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<ExternalSystemMetrics>();
        _provider = services.BuildServiceProvider();

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ExternalSystemMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, value, tags, _) => Record(i, value, tags));
        _listener.SetMeasurementEventCallback<double>((i, value, tags, _) => Record(i, value, tags));
        _listener.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _seen.Add((instrument.Name, value, new Dictionary<string, object?>(tags.ToArray())));

    public void Dispose()
    {
        _listener.Dispose();
        _provider.Dispose();
    }

    [Theory]
    [InlineData(TrafficOutcome.Succeeded, "succeeded")]
    [InlineData(TrafficOutcome.Failed, "failed")]
    [InlineData(TrafficOutcome.Faulted, "faulted")]
    public void RecordCall_CountsOneCallTaggedWithSystemAndOutcome(TrafficOutcome outcome, string expected)
    {
        var metrics = _provider.GetRequiredService<ExternalSystemMetrics>();

        metrics.RecordCall("Partner", outcome);

        var call = _seen.Should().ContainSingle(s => s.Instrument == "aiframework.external_system.calls").Subject;
        call.Value.Should().Be(1);
        call.Tags["system"].Should().Be("Partner");
        call.Tags["outcome"].Should().Be(expected);
    }

    [Fact]
    public void CertificateGauge_ReportsSecondsUntilNotAfter()
    {
        var metrics = _provider.GetRequiredService<ExternalSystemMetrics>();
        metrics.SetCertificateNotAfter("Partner", _clock.GetUtcNow().AddDays(10));

        _listener.RecordObservableInstruments();

        var gauge = _seen.Should().ContainSingle(s => s.Instrument == "aiframework.external_system.certificate.time_remaining").Subject;
        gauge.Value.Should().BeApproximately(TimeSpan.FromDays(10).TotalSeconds, 1);
        gauge.Tags["system"].Should().Be("Partner");
    }

    [Fact]
    public void CertificateGauge_ForASystemWhoseCertificateWasCleared_ReportsNothing()
    {
        var metrics = _provider.GetRequiredService<ExternalSystemMetrics>();
        metrics.SetCertificateNotAfter("Partner", _clock.GetUtcNow().AddDays(10));
        metrics.SetCertificateNotAfter("Partner", notAfter: null);

        _listener.RecordObservableInstruments();

        _seen.Should().NotContain(s => s.Instrument == "aiframework.external_system.certificate.time_remaining");
    }
}
```

Add one test to `OutboundTrafficHandlerTests`: an `Outbound` handler constructed with an `ExternalSystemMetrics` (resolved as above) records one call on a 503 with outcome `faulted`, and an `OutboundAttempt` handler records none. Name them `Send_AsTheOutboundCounter_RecordsTheCallMetric` and `Send_AsTheAttemptCounter_RecordsNoCallMetric`.

- [ ] **Step 2: Run to verify they fail** — build FAILS (`ExternalSystemMetrics` missing). If `AddMetrics` is not resolvable, add `<PackageReference Include="Microsoft.Extensions.Diagnostics" Version="10.0.12" />` to Infrastructure (aligning with NU1605 as the csproj comments describe).

- [ ] **Step 3: Implement** `src/Infrastructure/ExternalSystems/ExternalSystemMetrics.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// The external systems' OpenTelemetry instruments, for the two alert rules (ADR 0032): a call
/// counter by system and outcome, and seconds until each client certificate expires. Prometheus
/// sees <c>aiframework_external_system_calls_total</c> and
/// <c>aiframework_external_system_certificate_time_remaining_seconds</c>.
/// </summary>
public sealed class ExternalSystemMetrics
{
    public const string MeterName = "AiFramework.ExternalSystems";

    private readonly Counter<long> _calls;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _certificateNotAfter =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    public ExternalSystemMetrics(IMeterFactory meters, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _time = time;

        var meter = meters.Create(MeterName);
        _calls = meter.CreateCounter<long>(
            "aiframework.external_system.calls", unit: "{call}",
            description: "Calls to an external system, counted outside retry, by outcome.");
        meter.CreateObservableGauge(
            "aiframework.external_system.certificate.time_remaining", ObserveCertificates, unit: "s",
            description: "Seconds until an external system's client certificate expires.");
    }

    internal void RecordCall(string system, TrafficOutcome outcome) =>
        _calls.Add(
            1,
            new KeyValuePair<string, object?>("system", system),
            new KeyValuePair<string, object?>("outcome", outcome switch
            {
                TrafficOutcome.Succeeded => "succeeded",
                TrafficOutcome.Failed => "failed",
                _ => "faulted",
            }));

    /// <summary>Set by the worker's status publisher each run; null removes the system's series.</summary>
    internal void SetCertificateNotAfter(string system, DateTimeOffset? notAfter)
    {
        if (notAfter is { } value)
        {
            _certificateNotAfter[system] = value;
        }
        else
        {
            _certificateNotAfter.TryRemove(system, out _);
        }
    }

    private IEnumerable<Measurement<double>> ObserveCertificates()
    {
        var now = _time.GetUtcNow();
        return _certificateNotAfter.Select(pair => new Measurement<double>(
            (pair.Value - now).TotalSeconds, new KeyValuePair<string, object?>("system", pair.Key)));
    }
}
```

`OutboundTrafficHandler`: add `ExternalSystemMetrics? metrics = null` as the last constructor parameter, and in its `finally`, when recording, also call `metrics?.RecordCall(systemName, outcome)` **only when `kind == TrafficKind.Outbound`**. In `ExternalSystemsBuilder.AddClient`, pass `sp.GetRequiredService<ExternalSystemMetrics>()` to the Outbound handler only. In `AddExternalSystems`: `services.AddMetrics(); services.TryAddSingleton<ExternalSystemMetrics>();`.

In both hosts' meter registration (`ObservabilityRegistration.cs` and `WorkerObservability.cs`), add beside `.AddMeter("Wolverine:*")`:

```csharp
            .AddMeter(ExternalSystemMetrics.MeterName)
```

(Both files are on their host's `CompositionTypes` allowlist, so naming an Infrastructure type there is permitted.)

- [ ] **Step 4: Run** — `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystems"`, then both hosts' `ArchitectureTests`. Expected: PASS.

- [ ] **Step 5: Commit** — `feat(monitoring): export external system calls and certificate expiry as metrics`

---

### Task 5: The worker's status publisher

**Files:**
- Create: `src/Infrastructure/ExternalSystems/Health/ExternalSystemStatusPublisher.cs`
- Modify: `src/Infrastructure/ExternalSystems/ExternalSystemsRegistration.cs` (`AddExternalSystemStatusPublisher`; token-check timeout)
- Modify: `src/Infrastructure/ExternalSystems/Health/ProbeHealthCheck.cs` (quieter log)
- Modify: `src/Infrastructure/Monitoring/MonitoringOptions.cs` (`ExternalSystemStatusPeriod`)
- Modify: `src/Worker/Program.cs`
- Modify: `tests/Worker.IntegrationTests/WorkerFactory.cs` (period 1 s)
- Test: `tests/Infrastructure.Tests/ExternalSystems/ExternalSystemStatusPublisherTests.cs`
- Test: `tests/Worker.IntegrationTests/ExternalSystemStatusTests.cs`

**Interfaces:**
- Consumes: `IExternalSystemStatusStore` (Task 1), `ExternalSystemMetrics.SetCertificateNotAfter` (Task 4), `ExternalSystemHealth.Tag`/`CertificateNotAfterKey`, `ExternalSystemNames.CertificateCheck`/`TokenCheck`.
- Produces: `ExternalSystemsRegistration.AddExternalSystemStatusPublisher(this IServiceCollection)`; `internal static ExternalSystemStatusPublisher.Summarize(HealthReport report, DateTimeOffset checkedAt) : IReadOnlyList<ExternalSystemStatusView>`; `MonitoringOptions.ExternalSystemStatusPeriod` (TimeSpan, default 1 minute).

- [ ] **Step 1: Write the failing unit tests** (pure — `Summarize` takes a `HealthReport` and returns rows)

```csharp
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.ExternalSystems.Health;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class ExternalSystemStatusPublisherTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static HealthReportEntry Entry(
        string system, HealthStatus status, string description, Exception? exception = null,
        IReadOnlyDictionary<string, object>? data = null) =>
        new(status, description, TimeSpan.FromMilliseconds(5), exception, data, [ExternalSystemHealth.Tag, system]);

    private static HealthReport Report(params (string Name, HealthReportEntry Entry)[] entries) =>
        new(entries.ToDictionary(e => e.Name, e => e.Entry), TimeSpan.FromMilliseconds(20));

    [Fact]
    public void Summarize_TakesTheWorstCheckOfEachSystem()
    {
        var rows = ExternalSystemStatusPublisher.Summarize(Report(
            ("Partner", Entry("Partner", HealthStatus.Healthy, "Partner reachable (401)")),
            ("Partner:certificate", Entry("Partner", HealthStatus.Degraded, "client certificate expires in 10 days"))), At);

        var row = rows.Should().ContainSingle().Subject;
        row.State.Should().Be(ExternalSystemState.Degraded);
        row.Description.Should().Be("client certificate expires in 10 days");
        row.CheckedAt.Should().Be(At);
    }

    [Fact]
    public void Summarize_ReadsNotAfterFromTheCertificateCheck()
    {
        var notAfter = At.AddDays(200);

        var rows = ExternalSystemStatusPublisher.Summarize(Report(
            ("Partner", Entry("Partner", HealthStatus.Healthy, "ok")),
            ("Partner:certificate", Entry("Partner", HealthStatus.Healthy, "valid",
                data: new Dictionary<string, object> { [ExternalSystemHealth.CertificateNotAfterKey] = notAfter }))), At);

        rows.Single().CertificateNotAfter.Should().Be(notAfter);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, true)]
    [InlineData(HealthStatus.Unhealthy, false)]
    public void Summarize_ReportsWhetherTheTokenCheckPassed(HealthStatus token, bool expected)
    {
        var rows = ExternalSystemStatusPublisher.Summarize(Report(
            ("Partner", Entry("Partner", HealthStatus.Healthy, "ok")),
            ("Partner:token", Entry("Partner", token, "token"))), At);

        rows.Single().TokenOk.Should().Be(expected);
    }

    [Fact]
    public void Summarize_ForASystemWithoutATokenCheck_LeavesTokenUnknown()
    {
        var rows = ExternalSystemStatusPublisher.Summarize(
            Report(("Partner", Entry("Partner", HealthStatus.Healthy, "ok"))), At);

        rows.Single().TokenOk.Should().BeNull();
    }

    [Fact]
    public void Summarize_WhenACheckThrew_DescribesTheExceptionTypeOnly()
    {
        var rows = ExternalSystemStatusPublisher.Summarize(Report(
            ("Partner:token", Entry("Partner", HealthStatus.Unhealthy,
                "Connection refused (idp.internal:8443)",
                new InvalidOperationException("No TokenEndpoint configured for idp.internal")))), At);

        rows.Single().Description.Should().Be("check failed: InvalidOperationException");
    }

    [Fact]
    public void Summarize_GroupsEachSystemSeparately()
    {
        var rows = ExternalSystemStatusPublisher.Summarize(Report(
            ("A", Entry("A", HealthStatus.Healthy, "ok")),
            ("B", Entry("B", HealthStatus.Unhealthy, "B unreachable: ConnectionError"))), At);

        rows.Select(r => (r.Name, r.State)).Should().BeEquivalentTo(
            [("A", ExternalSystemState.Healthy), ("B", ExternalSystemState.Unhealthy)]);
    }
}
```

- [ ] **Step 2: Write the failing worker end-to-end test**

In `WorkerFactory.ConfigureWebHost`, beside the "Unreachable" setting:

```csharp
        // The status publisher (ADR 0032) checks every second here rather than every minute, so a
        // test sees the Unreachable system's row without waiting a minute.
        builder.UseSetting("Monitoring:ExternalSystemStatusPeriod", "00:00:01");
```

`tests/Worker.IntegrationTests/ExternalSystemStatusTests.cs` — open the database through a host scope, the way `Jobs/JobRunRecordingTests.cs` does:

```csharp
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Monitoring;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Worker.IntegrationTests;

[Collection(nameof(WorkerFactoryCollection))]
public sealed class ExternalSystemStatusTests(WorkerFactory factory)
{
    [Fact]
    public async Task Publisher_WritesTheUnreachableSystemAsUnhealthy()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        ExternalSystemStatusRow? row = null;
        while (row is null && DateTimeOffset.UtcNow < deadline)
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
                row = await context.ExternalSystemStatuses.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.Name == "Unreachable");
            }

            if (row is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }

        row.Should().NotBeNull("the worker's publisher writes every configured system");
        row!.State.Should().Be(ExternalSystemState.Unhealthy); // asserted non-null on the line above.
    }
}
```

`factory.Services` starts the host on first access, which starts the publisher. A real-time wait is right here: the publisher is the host's own timer, exactly as `tests/CLAUDE.md` allows for the broker.

- [ ] **Step 3: Run to verify they fail** — unit: build FAILS (`ExternalSystemStatusPublisher` missing); worker: the row never appears.

- [ ] **Step 4: Implement**

`MonitoringOptions` — add:

```csharp
    /// <summary>
    /// How often the worker re-runs the external systems' health checks and rewrites
    /// external_system_status (ADR 0032). The page calls a row stale after three minutes, so keep
    /// this at a minute or less.
    /// </summary>
    public TimeSpan ExternalSystemStatusPeriod { get; set; } = TimeSpan.FromMinutes(1);
```

`src/Infrastructure/ExternalSystems/Health/ExternalSystemStatusPublisher.cs`:

```csharp
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

/// <summary>
/// Runs in the WORKER only (ADR 0032): ASP.NET Core's health-check publisher hands it the
/// <c>external</c> checks' report every period, and it rewrites <c>external_system_status</c> and
/// the certificate gauge from it. Not a Quartz job, so the Jobs page is not buried in minutely runs.
/// </summary>
internal sealed class ExternalSystemStatusPublisher(
    IServiceScopeFactory scopes, ExternalSystemMetrics metrics, TimeProvider time) : IHealthCheckPublisher
{
    public async Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = Summarize(report, time.GetUtcNow());
        foreach (var row in rows)
        {
            metrics.SetCertificateNotAfter(row.Name, row.CertificateNotAfter);
        }

        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IExternalSystemStatusStore>()
            .ReplaceAsync(rows, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static IReadOnlyList<ExternalSystemStatusView> Summarize(HealthReport report, DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.Entries
            .Select(entry => (Name: entry.Key, Entry: entry.Value,
                System: entry.Value.Tags.FirstOrDefault(tag => tag != ExternalSystemHealth.Tag)))
            .Where(check => check.System is not null)
            .GroupBy(check => check.System!, StringComparer.OrdinalIgnoreCase) // filtered to non-null above.
            .Select(system =>
            {
                // HealthStatus orders Unhealthy < Degraded < Healthy, so the minimum is the worst.
                var worst = system.MinBy(check => check.Entry.Status);
                var certificate = system.FirstOrDefault(check => check.Name == ExternalSystemNames.CertificateCheck(system.Key));
                var token = system.FirstOrDefault(check => check.Name == ExternalSystemNames.TokenCheck(system.Key));

                return new ExternalSystemStatusView(
                    system.Key,
                    ToState(worst.Entry.Status),
                    Describe(worst.Entry),
                    checkedAt,
                    certificate.Entry.Data?.TryGetValue(ExternalSystemHealth.CertificateNotAfterKey, out var notAfter) == true
                        ? notAfter as DateTimeOffset?
                        : null,
                    token.Name is null ? null : token.Entry.Status == HealthStatus.Healthy);
            })
            .ToList();
    }

    /// <summary>A check that THREW is described by its type: its message can carry a host or a path.</summary>
    private static string? Describe(HealthReportEntry entry) =>
        entry.Exception is { } exception ? $"check failed: {exception.GetType().Name}" : entry.Description;

    private static ExternalSystemState ToState(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => ExternalSystemState.Healthy,
        HealthStatus.Degraded => ExternalSystemState.Degraded,
        _ => ExternalSystemState.Unhealthy,
    };
}
```

`HealthReportEntry` is a struct, so `certificate`/`token` are default tuples when absent — the `token.Name is null` and `Data?` checks handle that; adjust if the analyzer prefers explicit `default` comparisons.

Add to `ExternalSystemsRegistration`:

```csharp
    /// <summary>
    /// The worker's status loop (ADR 0032): the external checks every
    /// <see cref="MonitoringOptions.ExternalSystemStatusPeriod"/>, published into
    /// external_system_status. Called by the WORKER's Program.cs only — the API never publishes.
    /// </summary>
    public static IServiceCollection AddExternalSystemStatusPublisher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IHealthCheckPublisher, ExternalSystemStatusPublisher>();
        services.AddOptions<HealthCheckPublisherOptions>()
            .Configure<IOptions<MonitoringOptions>>((publisher, monitoring) =>
            {
                publisher.Delay = TimeSpan.FromSeconds(5);
                publisher.Period = monitoring.Value.ExternalSystemStatusPeriod;
                publisher.Timeout = TimeSpan.FromSeconds(30);
                publisher.Predicate = registration => registration.Tags.Contains(ExternalSystemHealth.Tag);
            });
        return services;
    }
```

In `src/Worker/Program.cs`, directly after its `AddExternalSystems(...)` line:

```csharp
// The worker alone runs the external checks on a timer and writes external_system_status, which
// the API's monitoring page reads (ADR 0032). The API registers the same checks but never publishes.
builder.Services.AddExternalSystemStatusPublisher();
```

PR 2 carry-overs, in the same task because they change what the publisher reports:
- **Token-check timeout:** in `AddExternalSystems`, give the token `HealthCheckRegistration` a `timeout: TimeSpan.FromSeconds(10)` (the backchannel `HttpClient` otherwise waits its 100 s default on a hung IdP).
- **Quieter probe logging:** `ProbeHealthCheck`'s `LogUnreachable` becomes `LogLevel.Debug` — the status table and the page now carry the failure, and a partner down for hours would otherwise write a Warning with a stack trace every minute.

- [ ] **Step 5: Run** — `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~ExternalSystems"` and `dotnet test tests/Worker.IntegrationTests --filter "FullyQualifiedName~ExternalSystemStatusTests|FullyQualifiedName~ReadinessTests|FullyQualifiedName~ArchitectureTests"`. Expected: PASS. Then the whole Worker suite once — the 1-second publisher runs during all of it and must not disturb other tests.

- [ ] **Step 6: Commit** — `feat(monitoring): keep external_system_status current from the worker`

---

### Task 6: The endpoint

**Files:**
- Create: `src/Api/Monitoring/MonitoringExternalSystemsController.cs`
- Modify: `src/Api/Monitoring/MonitoringDtos.cs`
- Modify (regenerated): `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts`
- Test: `tests/Api.IntegrationTests/Monitoring/ExternalSystemsTests.cs`

**Interfaces:**
- Produces: `GET /api/monitoring/external-systems` → `ExternalSystemsResponse { TrafficSince; Systems: ExternalSystemRowResponse[] }`, `ExternalSystemRowResponse { Name; State?; Description?; CheckedAt?; Stale; CertificateNotAfter?; TokenOk?; Calls; Failed; Faulted; Attempts; P95Ms? }`, `State` a string enum `Healthy|Degraded|Unhealthy`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Monitoring;

/// <summary>The gate and the shape. What the rows contain is Infrastructure's and Application's to prove.</summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class ExternalSystemsTests(ApiFactory factory)
{
    private static readonly Uri Endpoint = new("/api/monitoring/external-systems", UriKind.Relative);

    [Fact]
    public async Task GetExternalSystems_AsAMember_IsForbidden()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync(Endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetExternalSystems_AsAnAdministrator_ReturnsTheSystemsList()
    {
        var client = await factory.CreateAdminClientAsync();

        var response = await client.GetAsync(Endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("systems").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        body.TryGetProperty("trafficSince", out _).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run to verify they fail** — 404 instead of 403/200.

- [ ] **Step 3: Implement** — DTOs appended to `MonitoringDtos.cs`:

```csharp
/// <summary>Every external system's last health and last hour of traffic.</summary>
public sealed record ExternalSystemsResponse
{
    public required DateTimeOffset TrafficSince { get; init; }

    public required IReadOnlyList<ExternalSystemRowResponse> Systems { get; init; }
}

/// <summary>One external system. <see cref="State"/> is null until the worker has checked it once.</summary>
public sealed record ExternalSystemRowResponse
{
    public required string Name { get; init; }

    public ExternalSystemState? State { get; init; }

    /// <summary>The worst check's description. Never a host, path, body or secret.</summary>
    public string? Description { get; init; }

    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>The worker has not rewritten this row for three minutes: it is history, not health.</summary>
    public required bool Stale { get; init; }

    public DateTimeOffset? CertificateNotAfter { get; init; }

    /// <summary>Null when the system uses no token.</summary>
    public bool? TokenOk { get; init; }

    public required long Calls { get; init; }

    public required long Failed { get; init; }

    public required long Faulted { get; init; }

    public required long Attempts { get; init; }

    public double? P95Ms { get; init; }
}
```

`src/Api/Monitoring/MonitoringExternalSystemsController.cs`:

```csharp
using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// The external systems' health, as the worker last saw it, and their last hour of traffic.
/// Reads only: the checks themselves run in the worker (ADR 0032), never on this request.
/// </summary>
[ApiController]
[Route("api/monitoring/external-systems")]
[Authorize(Policy = AuthorizationPolicies.Monitoring.Read)]
public sealed class MonitoringExternalSystemsController(IQueryDispatcher queries) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ExternalSystemsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> List(CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetExternalSystemStatus(), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new ExternalSystemsResponse
            {
                TrafficSince = result.Value.TrafficSince,
                Systems =
                [
                    .. result.Value.Systems.Select(row => new ExternalSystemRowResponse
                    {
                        Name = row.Name,
                        State = row.State,
                        Description = row.Description,
                        CheckedAt = row.CheckedAt,
                        Stale = row.Stale,
                        CertificateNotAfter = row.CertificateNotAfter,
                        TokenOk = row.TokenOk,
                        Calls = row.Calls,
                        Failed = row.Failed,
                        Faulted = row.Faulted,
                        Attempts = row.Attempts,
                        P95Ms = row.P95Ms,
                    }),
                ],
            })
            : result.Problem(HttpContext);
    }
}
```

- [ ] **Step 4: Regenerate the contract** (bash, repo root):

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false Admin__ReconcileOnStart=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
git diff --stat openapi frontend/src/api
```

Expected: only the new path, `ExternalSystemsResponse`, `ExternalSystemRowResponse` and `ExternalSystemState` added.

- [ ] **Step 5: Run** — `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~ExternalSystemsTests|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~OpenApi"`; `npm run build --prefix frontend`. Expected: PASS.

- [ ] **Step 6: Commit** — `feat(monitoring): serve external system status to administrators`

---

### Task 7: The page and the overview strip

**Files:**
- Modify: `frontend/src/features/monitoring/types.ts`, `frontend/src/api/monitoring.ts`, `frontend/src/features/monitoring/queries.ts`
- Create: `frontend/src/features/monitoring/IntegrationsPage.tsx`, `IntegrationsPage.test.tsx`
- Modify: `frontend/src/features/monitoring/MonitoringPage.tsx`, `MonitoringPage.test.tsx`
- Modify: `frontend/src/routes.tsx`, `frontend/src/test/handlers.ts`

Load the `react-conventions` and `react-testing` skills before starting.

**Interfaces:**
- Produces: `type ExternalSystems = components['schemas']['ExternalSystemsResponse']`, `ExternalSystemRow`, `ExternalSystemState`; `getExternalSystems(): Promise<ExternalSystems>`; `useExternalSystems(): UseQueryResult<ExternalSystems, ApiError>`; route `/monitoring/integrations`.

- [ ] **Step 1: Plumbing**

`types.ts`:

```ts
export type ExternalSystems = components['schemas']['ExternalSystemsResponse'];
export type ExternalSystemRow = components['schemas']['ExternalSystemRowResponse'];
export type ExternalSystemState = components['schemas']['ExternalSystemState'];
```

`api/monitoring.ts` (add `ExternalSystems` to the type import):

```ts
export function getExternalSystems(): Promise<ExternalSystems> {
  return request<ExternalSystems>('/api/monitoring/external-systems');
}
```

`queries.ts` — add the key `externalSystems: () => [...monitoringKeys.all, 'external-systems'] as const,` and:

```ts
/**
 * The worker rewrites external_system_status once a minute (ADR 0032), so asking more often than
 * every thirty seconds returns the same rows.
 */
const ExternalSystemsRefreshMs = 30_000;

export function useExternalSystems(): UseQueryResult<ExternalSystems, ApiError> {
  return useQuery({
    queryKey: monitoringKeys.externalSystems(),
    queryFn: getExternalSystems,
    refetchInterval: ExternalSystemsRefreshMs,
    retry: false,
  });
}
```

`test/handlers.ts` — default handler beside the traffic ones:

```ts
  http.get('/api/monitoring/external-systems', () =>
    HttpResponse.json({
      trafficSince: '2026-10-09T11:00:00+00:00',
      systems: [
        {
          name: 'PartnerSimulator', state: 'Healthy', description: 'PartnerSimulator reachable (200)',
          checkedAt: '2026-10-09T11:59:30+00:00', stale: false,
          certificateNotAfter: '2027-10-09T00:00:00+00:00', tokenOk: null,
          calls: 120, failed: 2, faulted: 1, attempts: 124, p95Ms: 85,
        },
        {
          name: 'Unreachable', state: 'Unhealthy', description: 'Unreachable unreachable: ConnectionError',
          checkedAt: '2026-10-09T11:59:30+00:00', stale: false,
          certificateNotAfter: null, tokenOk: null,
          calls: 0, failed: 0, faulted: 0, attempts: 0, p95Ms: null,
        },
      ],
    }),
  ),
```

- [ ] **Step 2: Write the failing page tests**

`IntegrationsPage.test.tsx` (pattern: `MonitoringPage.test.tsx` — `MemoryRouter`, `withQueryClient`, `server.use` overrides):

```tsx
import { render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { IntegrationsPage } from './IntegrationsPage';

function renderPage(): void {
  render(
    <MemoryRouter>
      <IntegrationsPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

function rowFor(name: string): HTMLElement {
  const cell = screen.getByRole('rowheader', { name });
  const row = cell.closest('tr');
  if (!row) {
    throw new Error(`no row for ${name}`);
  }
  return row;
}

describe('IntegrationsPage', () => {
  it('lists each system with its status and last hour of calls', async () => {
    renderPage();

    await screen.findByRole('rowheader', { name: 'PartnerSimulator' });
    expect(within(rowFor('PartnerSimulator')).getByText('Healthy')).toBeInTheDocument();
    expect(within(rowFor('PartnerSimulator')).getByText('120')).toBeInTheDocument();
    expect(within(rowFor('Unreachable')).getByText('Unhealthy')).toBeInTheDocument();
  });

  it('IntegrationsPage_ForAStaleRow_SaysStaleNotHealthy', async () => {
    server.use(
      http.get('/api/monitoring/external-systems', () =>
        HttpResponse.json({
          trafficSince: '2026-10-09T11:00:00+00:00',
          systems: [{
            name: 'Partner', state: 'Healthy', description: 'ok', checkedAt: '2026-10-09T10:00:00+00:00',
            stale: true, certificateNotAfter: null, tokenOk: null,
            calls: 0, failed: 0, faulted: 0, attempts: 0, p95Ms: null,
          }],
        }),
      ),
    );

    renderPage();

    const row = await screen.findByRole('rowheader', { name: 'Partner' });
    expect(within(row.closest('tr') ?? document.body).getByText(/Stale/)).toBeInTheDocument();
    expect(within(row.closest('tr') ?? document.body).queryByText('Healthy')).not.toBeInTheDocument();
  });

  it('flags a certificate expiring within thirty days', async () => {
    const soon = new Date(Date.now() + 10 * 24 * 60 * 60 * 1000).toISOString();
    server.use(
      http.get('/api/monitoring/external-systems', () =>
        HttpResponse.json({
          trafficSince: '2026-10-09T11:00:00+00:00',
          systems: [{
            name: 'Partner', state: 'Degraded', description: 'expires soon', checkedAt: new Date().toISOString(),
            stale: false, certificateNotAfter: soon, tokenOk: true,
            calls: 0, failed: 0, faulted: 0, attempts: 0, p95Ms: null,
          }],
        }),
      ),
    );

    renderPage();

    expect(await screen.findByText(/in (9|10) days/)).toHaveClass('attention');
  });

  it('says so when no external system is configured', async () => {
    server.use(
      http.get('/api/monitoring/external-systems', () =>
        HttpResponse.json({ trafficSince: '2026-10-09T11:00:00+00:00', systems: [] }),
      ),
    );

    renderPage();

    expect(await screen.findByText(/No external system is configured/)).toBeInTheDocument();
  });

  it('renders the error state', async () => {
    server.use(
      http.get('/api/monitoring/external-systems', () =>
        HttpResponse.json({ title: 'Boom', status: 500 }, { status: 500 }),
      ),
    );

    renderPage();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
```

Test names here follow the existing Vitest `it('…')` sentence style; keep the Review Focus phrase in the stale test's name as written so it is greppable. Use the real `ErrorPanel` role (read `components/ErrorPanel.tsx`; if it is not `role="alert"`, assert what it actually renders).

Add to `MonitoringPage.test.tsx`:

```tsx
  it('shows one status per external system and links to the integrations page', async () => {
    renderPage();

    const systems = within(await screen.findByRole('list', { name: 'External systems' }));
    expect(systems.getByText(/PartnerSimulator/)).toBeInTheDocument();
    expect(systems.getByText(/Unhealthy/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /External system health and traffic/ })).toHaveAttribute(
      'href',
      '/monitoring/integrations',
    );
  });
```

- [ ] **Step 3: Run to verify they fail** — `npm test --prefix frontend -- --run src/features/monitoring`. Expected: FAIL (module/page missing).

- [ ] **Step 4: Implement**

`IntegrationsPage.tsx`:

```tsx
import { Link } from 'react-router-dom';
import { useExternalSystems } from './queries';
import type { ExternalSystemRow } from './types';
import { ErrorPanel } from '../../components/ErrorPanel';
import './monitoring.css';

const DayMs = 24 * 60 * 60 * 1000;
const ExpiryWarningDays = 30;

/**
 * External systems: what the worker last saw of each partner (ADR 0032), and the last hour of
 * calls to it. A stale row means the worker stopped checking — its last status is history.
 */
export function IntegrationsPage(): React.JSX.Element {
  const systems = useExternalSystems();

  return (
    <section>
      <h1>External systems</h1>
      <p>
        <Link to="/monitoring">Back to monitoring</Link>
      </p>

      {systems.isPending && <p role="status">Loading external systems…</p>}
      {systems.error && <ErrorPanel error={systems.error} />}

      {systems.isSuccess && systems.data.systems.length === 0 && (
        <p>No external system is configured.</p>
      )}

      {systems.isSuccess && systems.data.systems.length > 0 && (
        <table className="runs">
          <caption className="muted">Status as of the last check; calls over the last hour</caption>
          <thead>
            <tr>
              <th scope="col">System</th>
              <th scope="col">Status</th>
              <th scope="col">Checked</th>
              <th scope="col">Certificate</th>
              <th scope="col">Token</th>
              <th scope="col">Calls</th>
              <th scope="col">Attempts</th>
              <th scope="col">Errors</th>
              <th scope="col">p95</th>
            </tr>
          </thead>
          <tbody>
            {systems.data.systems.map((row) => (
              <tr key={row.name}>
                <th scope="row">{row.name}</th>
                <td className={statusClass(row)} title={row.description ?? undefined}>
                  {statusText(row)}
                </td>
                <td>{row.checkedAt ? new Date(row.checkedAt).toLocaleTimeString() : '—'}</td>
                <CertificateCell notAfter={row.certificateNotAfter} />
                <td>{row.tokenOk === null || row.tokenOk === undefined ? '—' : row.tokenOk ? 'OK' : 'Failing'}</td>
                <td>{Number(row.calls)}</td>
                <td>{Number(row.attempts)}</td>
                <td>{Number(row.failed) + Number(row.faulted)}</td>
                <td>{row.p95Ms === null || row.p95Ms === undefined ? '—' : `${Math.round(Number(row.p95Ms))} ms`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function statusText(row: ExternalSystemRow): string {
  if (row.stale) {
    return 'Stale — the worker has stopped checking';
  }
  return row.state ?? 'Not checked yet';
}

function statusClass(row: ExternalSystemRow): string {
  return row.stale || row.state === 'Unhealthy' || row.state === 'Degraded' ? 'attention' : '';
}

function CertificateCell({ notAfter }: { readonly notAfter: string | null | undefined }): React.JSX.Element {
  if (!notAfter) {
    return <td>—</td>;
  }
  const days = Math.floor((new Date(notAfter).getTime() - Date.now()) / DayMs);
  if (days < 0) {
    return <td className="attention">Expired</td>;
  }
  return <td className={days < ExpiryWarningDays ? 'attention' : ''}>{`in ${String(days)} days`}</td>;
}
```

Match the generated types: if a nullable field is typed `string | null` only (no `undefined`), simplify the null checks accordingly, and coerce numeric fields with `Number(...)` as the other monitoring pages do (`frontend/CLAUDE.md` on `number | string`). If `monitoring.css` has no `attention` class, add `.attention { color: var(--danger, #b42318); font-weight: 600; }` there; colour is never the only signal — the text says the status.

Overview strip in `MonitoringPage.tsx`, after the Traffic section (`const external = useExternalSystems();` at the top):

```tsx
      <h2>External systems</h2>

      {external.error && <ErrorPanel error={external.error} />}

      {external.isSuccess &&
        (external.data.systems.length === 0 ? (
          <p className="muted">No external system is configured.</p>
        ) : (
          <ul className="tiles" aria-label="External systems">
            {external.data.systems.map((row) => (
              <li
                key={row.name}
                className={row.stale || row.state !== 'Healthy' ? 'tile tile--attention' : 'tile'}
              >
                <span className="tile__value">{row.stale ? 'Stale' : (row.state ?? 'Not checked')}</span>
                <span className="tile__label">{row.name}</span>
              </li>
            ))}
          </ul>
        ))}

      <p>
        <Link to="/monitoring/integrations">External system health and traffic</Link>
      </p>
```

`routes.tsx`: import `IntegrationsPage` and add `<Route path="/monitoring/integrations" element={<IntegrationsPage />} />` beside the other monitoring routes (inside the same `RequireRole allow="Admin"` block).

- [ ] **Step 5: Run** — `npm test --prefix frontend -- --run`, `npm run lint --prefix frontend`, `npm run build --prefix frontend`. Expected: all pass, zero lint warnings.

- [ ] **Step 6: Commit** — `feat(monitoring): show external system health on the monitoring page`

---

### Task 8: Alert rules

**Files:**
- Modify: `k8s/components/observability/prometheus-rules.yml`, `prometheus-rules.test.yml`

- [ ] **Step 1: Write the failing promtool tests** — append to `prometheus-rules.test.yml`'s `tests:` (same shape as the existing cases; `job="aiframework-worker"` because that host makes the writes, but the rule aggregates across jobs):

```yaml
  # 3 of every 10 calls fault: 30%, over 20% with more than 10 calls per 5 minutes.
  - interval: 1m
    input_series:
      - series: 'aiframework_external_system_calls_total{job="aiframework-worker", system="Partner", outcome="succeeded"}'
        values: '0+7x20'
      - series: 'aiframework_external_system_calls_total{job="aiframework-worker", system="Partner", outcome="faulted"}'
        values: '0+3x20'
    alert_rule_test:
      - eval_time: 12m
        alertname: ExternalSystemFailing
        exp_alerts:
          - exp_labels: { severity: warning, system: Partner }
            exp_annotations:
              summary: Partner is failing.
              description: 30% of calls to Partner faulted over the last 5 minutes. The monitoring page's External systems view shows its last health check.

  # 3 faulted of 4 calls in 5 minutes: a high ratio but under the 10-call floor, so quiet.
  - interval: 1m
    input_series:
      - series: 'aiframework_external_system_calls_total{job="aiframework-worker", system="Quiet", outcome="succeeded"}'
        values: '0+0.2x20'
      - series: 'aiframework_external_system_calls_total{job="aiframework-worker", system="Quiet", outcome="faulted"}'
        values: '0+0.6x20'
    alert_rule_test:
      - eval_time: 12m
        alertname: ExternalSystemFailing
        exp_alerts: []

  # 10 days left: under the 14-day line.
  - interval: 1m
    input_series:
      - series: 'aiframework_external_system_certificate_time_remaining_seconds{job="aiframework-worker", system="Partner"}'
        values: '864000x90'
    alert_rule_test:
      - eval_time: 70m
        alertname: ExternalSystemCertificateExpiring
        exp_alerts:
          - exp_labels: { severity: warning, system: Partner }
            exp_annotations:
              summary: Partner's client certificate expires in under 14 days.
              description: Renew it in Vault; Vault Secrets Operator mounts the new file and the pods restart onto it (ADR 0031).

  # 60 days left: quiet.
  - interval: 1m
    input_series:
      - series: 'aiframework_external_system_certificate_time_remaining_seconds{job="aiframework-worker", system="Partner"}'
        values: '5184000x90'
    alert_rule_test:
      - eval_time: 70m
        alertname: ExternalSystemCertificateExpiring
        exp_alerts: []
```

Run: `docker run --rm --entrypoint promtool -v "$PWD/k8s/components/observability:/rules" prom/prometheus:v3.15.0 test rules /rules/prometheus-rules.test.yml` (use the image tag the file's own header names). Expected: FAIL (no such alerts).

- [ ] **Step 2: Add the rules** to the existing rule group in `prometheus-rules.yml`:

```yaml
      - alert: ExternalSystemFailing
        # Spec §3: over 20% of a system's calls faulted in 5 minutes, with at least 10 calls in
        # that window so one failed call at night pages nobody. Calls are counted outside retry,
        # so a call that failed after its retries counts once.
        expr: |
          (
            sum by (system) (increase(aiframework_external_system_calls_total{outcome="faulted"}[5m]))
            / sum by (system) (increase(aiframework_external_system_calls_total[5m]))
          ) > 0.2
          and
          sum by (system) (increase(aiframework_external_system_calls_total[5m])) >= 10
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: '{{ $labels.system }} is failing.'
          description: >-
            {{ $value | humanizePercentage }} of calls to {{ $labels.system }} faulted over the last
            5 minutes. The monitoring page's External systems view shows its last health check.

      - alert: ExternalSystemCertificateExpiring
        # Spec §3: under 14 days. min across pods: the API and the worker read the same file.
        expr: min by (system) (aiframework_external_system_certificate_time_remaining_seconds) < 14 * 86400
        for: 1h
        labels:
          severity: warning
        annotations:
          summary: "{{ $labels.system }}'s client certificate expires in under 14 days."
          description: >-
            Renew it in Vault; Vault Secrets Operator mounts the new file and the pods restart onto
            it (ADR 0031).
```

The `description` text in the tests must match what the templates render exactly; if promtool reports a mismatch, adjust the **test's** expected text to the rendered value (`humanizePercentage` renders `30%`; use whatever promtool prints), never the threshold. Note the first test's ratio rendering in its expected description.

- [ ] **Step 3: Run** promtool again. Expected: `SUCCESS`. Also update the kubernetes skill's rule count (“Six rules” → “Eight rules”) in Task 10.

- [ ] **Step 4: Commit** — `feat(monitoring): alert on failing external systems and expiring certificates`

---

### Task 9: A real partner for e2e and the dev loop

**Files:**
- Modify: `tests/PartnerSimulator/PartnerSimulatorApp.cs`, `Program.cs`
- Modify: `frontend/e2e/support/env.ts`, `frontend/e2e/setup/prepare-database.ts`, `frontend/playwright.config.ts`
- Create: `frontend/e2e/specs/monitoring/integrations.spec.ts`; modify `frontend/e2e/screens/monitoring.ts`
- Modify: `scripts/dev.ps1`, `scripts/stop-dev.ps1`
- Test: `tests/Infrastructure.Tests/ExternalSystems/PartnerSimulatorTests.cs` (add)

**Interfaces:**
- Produces: `PartnerSimulatorOptions.HttpsPort` (int, 0 = random) and `.HealthPort` (int?, null = none); `PartnerSimulatorApp.WaitForShutdownAsync()`; CLI `serve <certDirectory> <httpsPort> <healthPort>`.

- [ ] **Step 1: Write the failing simulator test**

```csharp
    [Fact]
    public async Task Health_OnTheHealthPort_AnswersOverPlainHttpWithoutACertificate()
    {
        using var pki = TestPki.Create();
        var healthPort = FreeTcpPort();
        await using var simulator = await PartnerSimulatorApp.StartAsync(
            new PartnerSimulatorOptions
            {
                ServerCertificate = TestPki.Usable(pki.IssueServer()),
                TrustedClientRoot = pki.Root,
                HealthPort = healthPort,
            },
            CancellationToken.None);
        using var client = new HttpClient();

        var response = await client.GetAsync(new Uri($"http://127.0.0.1:{healthPort}/health"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        simulator.BaseAddress.Scheme.Should().Be("https");
    }

    private static int FreeTcpPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
```

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~PartnerSimulatorTests"`. Expected: build FAILS (`HealthPort` missing).

- [ ] **Step 2: Implement the options and `serve` mode**

`PartnerSimulatorOptions`:

```csharp
    /// <summary>The HTTPS (mTLS) port. 0 picks a free one — what the in-process tests use.</summary>
    public int HttpsPort { get; init; }

    /// <summary>
    /// A plain-HTTP port serving only <c>/health</c>, for a readiness check that holds no client
    /// certificate (Playwright's webServer). Null: none.
    /// </summary>
    public int? HealthPort { get; init; }
```

In `ConfigureTls`, listen on `options.HttpsPort` instead of `0`, and when `HealthPort` is set add `kestrel.Listen(IPAddress.Loopback, options.HealthPort.Value);` (no HTTPS). Map `app.MapGet("/health", () => Results.Text("ok")).RequireHost($"*:{options.HealthPort}")` only when it is set. `BaseAddress` must now pick the HTTPS address: `Addresses.Single(address => address.StartsWith("https", StringComparison.Ordinal))`. Add `public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();`.

`Program.cs` — add before the usage fallback:

```csharp
// The dev loop (dev.ps1 -WithPartners) and the e2e run start a real mTLS partner from the files
// `generate-certs` wrote. Health on a plain-HTTP port, so a readiness probe needs no certificate.
if (args is ["serve", var certDirectory, var httpsPort, var healthPort])
{
    var server = X509CertificateLoader.LoadPkcs12FromFile(
        Path.Combine(certDirectory, "server.pfx"),
        File.ReadAllText(Path.Combine(certDirectory, "server.pass")).Trim());
    var roots = new X509Certificate2Collection();
    roots.ImportFromPemFile(Path.Combine(certDirectory, "ca.pem"));

    await using var simulator = await PartnerSimulatorApp.StartAsync(
        new PartnerSimulatorOptions
        {
            ServerCertificate = server,
            TrustedClientRoot = roots[0],
            HttpsPort = int.Parse(httpsPort, CultureInfo.InvariantCulture),
            HealthPort = int.Parse(healthPort, CultureInfo.InvariantCulture),
        },
        CancellationToken.None);
    Console.WriteLine($"Partner simulator: {simulator.BaseAddress} (health http://127.0.0.1:{healthPort}/health)");
    await simulator.WaitForShutdownAsync();
    return 0;
}
```

Update the usage message to name both commands. Run the simulator tests: PASS.

- [ ] **Step 3: e2e wiring**

`frontend/e2e/support/env.ts`:

```ts
// The e2e partner simulator (tests/PartnerSimulator `serve`): HTTPS with mTLS, and a plain-HTTP
// health port Playwright can probe without a client certificate.
export const SIMULATOR_PORT = process.env.SIMULATOR_PORT ?? '55692';
export const SIMULATOR_HEALTH_PORT = process.env.SIMULATOR_HEALTH_PORT ?? '55693';
// Relative to frontend/, where every e2e npm script runs. Generated each run; git-ignored by the
// repository-wide `.certs/` pattern. Never committed.
export const E2E_CERT_DIR = path.resolve('e2e', '.certs');
```

(`import path from 'node:path';` at the top.)

`prepare-database.ts`: add `'../tests/PartnerSimulator'` to the build loop, then after it:

```ts
// A fresh throwaway PKI per run for the simulator and the worker's client certificate.
execFileSync(
  'dotnet',
  ['run', '--project', '../tests/PartnerSimulator', '--no-build', '--', 'generate-certs', E2E_CERT_DIR],
  { stdio: 'inherit' },
);
```

`playwright.config.ts` — a fourth `webServer` entry:

```ts
          {
            // A real mTLS partner for the External systems page (ADR 0032). Built by
            // prepare-database.ts; certificates generated there too.
            command: `dotnet run --project ../tests/PartnerSimulator --no-build -- serve "${E2E_CERT_DIR}" ${SIMULATOR_PORT} ${SIMULATOR_HEALTH_PORT}`,
            url: `http://127.0.0.1:${SIMULATOR_HEALTH_PORT}/health`,
            timeout: 60_000,
            reuseExistingServer: false,
            stdout: 'pipe',
            stderr: 'pipe',
          },
```

and in the **worker's** `env` block:

```ts
              // Two external systems for the External systems page: the simulator over mTLS, and
              // an address nothing listens on. Checked every 5 s instead of every minute (ADR 0032).
              ExternalSystems__Systems__PartnerSimulator__BaseAddress: `https://127.0.0.1:${SIMULATOR_PORT}/`,
              ExternalSystems__Systems__PartnerSimulator__Probe__Path: 'ping',
              ExternalSystems__Systems__PartnerSimulator__ClientCertificate__Path: path.join(E2E_CERT_DIR, 'client.pfx'),
              ExternalSystems__Systems__PartnerSimulator__ClientCertificate__PasswordFile: path.join(E2E_CERT_DIR, 'client.pass'),
              ExternalSystems__Systems__PartnerSimulator__ServerTrust__CaBundlePath: path.join(E2E_CERT_DIR, 'ca.pem'),
              ExternalSystems__Systems__PartnerSimulator__ServerTrust__CheckRevocation: 'false',
              ExternalSystems__Systems__Unreachable__BaseAddress: 'https://127.0.0.1:1/',
              Monitoring__ExternalSystemStatusPeriod: '00:00:05',
```

`frontend/e2e/screens/monitoring.ts` — add locators following the file's existing style:

```ts
export const integrationsHeading = (page: Page): Locator =>
  page.getByRole('heading', { name: 'External systems', level: 1 });

export const systemRow = (page: Page, name: string): Locator =>
  page.getByRole('row').filter({ has: page.getByRole('rowheader', { name }) });
```

`frontend/e2e/specs/monitoring/integrations.spec.ts`:

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as monitoring from '../../screens/monitoring.ts';

/**
 * The External systems page against a real mTLS partner and a dead address (ADR 0032). The worker
 * checks every 5 s in this run, so both rows settle within a few checks.
 */
// @local-only: needs the administrator role (see monitoring.spec.ts) and the worker and simulator
// only the managed stack starts.
test.describe('external systems', { tag: '@local-only' }, () => {
  test('shows the simulator healthy and the dead address unhealthy', async ({ adminPage }) => {
    await adminPage.goto('/monitoring/integrations');
    await expect(monitoring.integrationsHeading(adminPage)).toBeVisible();

    await expect(monitoring.systemRow(adminPage, 'PartnerSimulator')).toContainText('Healthy', {
      timeout: 45_000,
    });
    await expect(monitoring.systemRow(adminPage, 'Unreachable')).toContainText('Unhealthy', {
      timeout: 45_000,
    });
  });
});
```

The page refetches every 30 s; if the first render predates the first check, `toContainText` waits through the next refetch — 45 s covers one refetch plus the worker's 5 s period. If that proves tight in CI, reload inside an `expect.poll` rather than raising the timeout blindly.

Run: `npm run e2e --prefix frontend -- --grep "external systems"` (dev loop stopped first). Expected: PASS. Then the full `npm run e2e --prefix frontend` once.

- [ ] **Step 4: Dev loop**

`scripts/dev.ps1` — add a `[switch]$WithPartners` parameter beside `$WithSeq` (with a comment in the same style), and before the worker launch:

```powershell
# -WithPartners: a real mTLS partner for the External systems page (ADR 0032), on 55690 with its
# health on 55691, from the throwaway PKI in .certs/ (generated here if missing).
if ($WithPartners) {
    $certs = Join-Path $repoRoot '.certs'
    if (-not (Test-Path (Join-Path $certs 'client.pfx'))) {
        & (Join-Path $PSScriptRoot 'new-dev-certs.ps1')
    }
    Invoke-Step 'Launching the partner simulator' {
        Start-Process powershell -WorkingDirectory $repoRoot -ArgumentList @(
            '-NoExit', '-Command',
            "dotnet run --project tests/PartnerSimulator -- serve `"$certs`" 55690 55691"
        )
        $global:LASTEXITCODE = 0
    }
}
```

and inside the worker's `Invoke-Step`, when `$WithPartners`, set (and remove in its `finally`, like the OTLP variables):

```powershell
        $env:ExternalSystems__Systems__PartnerSimulator__BaseAddress = 'https://127.0.0.1:55690/'
        $env:ExternalSystems__Systems__PartnerSimulator__Probe__Path = 'ping'
        $env:ExternalSystems__Systems__PartnerSimulator__ClientCertificate__Path = (Join-Path $certs 'client.pfx')
        $env:ExternalSystems__Systems__PartnerSimulator__ClientCertificate__PasswordFile = (Join-Path $certs 'client.pass')
        $env:ExternalSystems__Systems__PartnerSimulator__ServerTrust__CaBundlePath = (Join-Path $certs 'ca.pem')
        $env:ExternalSystems__Systems__PartnerSimulator__ServerTrust__CheckRevocation = 'false'
```

`scripts/stop-dev.ps1`: add `Stop-PortOwner -Port 55690 -Name 'Partner simulator'` after the other three (it reports "not running" harmlessly when `-WithPartners` was not used).

Manual check: `./scripts/dev.ps1 -WithPartners`, sign in as an administrator, open `/monitoring/integrations`, see `PartnerSimulator` Healthy within a minute. `./scripts/stop-dev.ps1` stops the simulator window.

- [ ] **Step 5: Commit** — `feat(monitoring): run a real mTLS partner in e2e and the dev loop`

---

### Task 10: ADR, spec and docs

**Files:**
- Create: `docs/adr/0032-external-system-status-is-published-by-the-worker.md`
- Modify: `docs/superpowers/specs/2026-10-06-external-systems-design.md`
- Modify: `.claude/skills/external-systems/SKILL.md`, `.claude/skills/observability/SKILL.md`, `.claude/skills/kubernetes/SKILL.md`, `.claude/skills/local-dev/SKILL.md`
- Modify: `README.md`, `frontend/e2e/CLAUDE.md`, `docs/local-development.md`

- [ ] **Step 1: ADR 0032** in the house style of `docs/adr/0031-*.md` (read it first; check open branches before taking the number, per the README). Context: spec §3 chose a Quartz job; `job_runs` records every run for 30 days, so a minutely job would bury the Jobs page. Decision: `IHealthCheckPublisher` in the worker only, period `Monitoring:ExternalSystemStatusPeriod` (1 min), last-write-wins upsert plus delete of unconfigured systems, stale after 3 min, sanitised descriptions; the `AiFramework.ExternalSystems` meter and the two alerts. Consequences: two worker replicas both publish (harmless); the API's own view of a partner is not measured separately (same cluster network); per-pod gauges are `min`-aggregated. Alternatives: the Quartz job (rejected for the reason above); a `BackgroundService` of our own (rejected — the publisher already owns timing, timeouts and the predicate); the API publishing (rejected — every API replica would probe every partner).

- [ ] **Step 2: Spec** — §3 "Who runs them": replace the Quartz job with the publisher and cite ADR 0032; §3 metrics: the meter, the two instrument names and the call counter; §4: simulator `serve` mode via Playwright's `webServer` rather than `docker-compose.e2e.yml`, ports 55690/55691 dev and 55692/55693 e2e, Keycloak not started in the dev loop (OAuth stays covered by the Testcontainers tests); §6 PR 3 row: no Wolverine adapters regenerated, and the kind-overlay Secret mount **not** done (nothing in the cluster calls a partner yet — the VSO contract in ADR 0031 stands); Status line: monitoring built.

- [ ] **Step 3: Skills and README**
  - `external-systems`: a "Monitoring" section — the status table and publisher (worker only, period, stale after 3 min), the page, the meter and the two alerts, `dev.ps1 -WithPartners`.
  - `observability`: the `AiFramework.ExternalSystems` meter (both hosts' `AddMeter`), instrument names and their Prometheus names.
  - `kubernetes`: "Six rules" → "Eight rules", naming the two new ones.
  - `local-dev`: `-WithPartners` in the command block and control-panel notes; ports 55690/55691.
  - `README.md`: API table row `GET /api/monitoring/external-systems` (`Monitoring.Read`); frontend route `/monitoring/integrations`; ports 55690/55691 (dev, `-WithPartners`) and 55692/55693 (e2e); the `/health/ready` paragraph's "next planned step" sentence now points to the page; ADR 0032 in the table.
  - `frontend/e2e/CLAUDE.md`: the Monitoring coverage row gains "external systems (simulator healthy, dead address unhealthy)"; the port list gains the simulator ports and `SIMULATOR_PORT`/`SIMULATOR_HEALTH_PORT`.
  - `docs/local-development.md`: `-WithPartners` in the external-systems section and the port table.

- [ ] **Step 4: Verify** — `dotnet format whitespace AiFramework.slnx`, then the `/verify` checks (build Debug and Release, every suite, frontend lint/test/build, codegen and contract diffs). Expected: all green; the codegen diff empty.

- [ ] **Step 5: Commit** — `docs(monitoring): record ADR 0032 and document external system monitoring`
