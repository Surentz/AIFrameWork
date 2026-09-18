---
description: Add a background job - message, handler, registration, tests, and regenerated adapters
argument-hint: <JobName>
---

Add the background job `$ARGUMENTS`.

Read root `CLAUDE.md`'s `## Jobs` section and `src/Worker/CLAUDE.md` before starting. ADR 0016 is
the reasoning behind all of it.

**Jobs run in the worker. The API listens to nothing.** Nothing below changes that, and no step
here ever adds a listener to `src/Api`.

## 1. Choose the lane, and say why

| Lane | For | Parallelism per pod |
|---|---|---|
| `Light` | Milliseconds to seconds, a round trip or two, negligible CPU | 8 |
| `Heavy` | Seconds to minutes, CPU- or memory-bound, or fanning over a large result set | 2 |

State the choice and the reason in one sentence before writing code. If it is genuinely unclear,
pick `Heavy` — the cost of a light job on the slow lane is latency; the cost of a heavy job on the
fast lane is eight of them at once on one pod.

## 2. Decide whether losing it is acceptable

This is the question that actually matters, and it decides step 5.

- **A job that must not be lost** is enqueued from an `IDomainEventHandler<T>`, never from a
  command handler. `DomainEventsInterceptor` writes the outbox row in the same `SaveChangesAsync`
  as the aggregate, so the job exists if and only if the command committed.
  `OrderPlacedConfirmationHandler` is the reference.
- **A best-effort job** may be enqueued directly through `IJobScheduler`. Be explicit that this is
  the choice; do not reach for it by default.

`IJobScheduler.EnqueueAsync` publishes immediately. A command that enqueues and then fails its
transaction has still run the job.

## 3. The job and its handler — `src/Application/<Feature>/`

In the **feature folder**, beside that feature's commands and queries. There is no `Jobs/` folder
in Application; this repo organises by feature, not by technical kind.

```csharp
public sealed record RebuildOrderReport(Guid OwnerId) : IUserScopedJob
{
    public static JobLane Lane => JobLane.Heavy;
}

public sealed class RebuildOrderReportHandler(IQueryDispatcher queries, IOrderReportWriter writer)
{
    public async Task Handle(RebuildOrderReport job, CancellationToken cancellationToken) { ... }
}
```

- Implement `IUserScopedJob` (not just `IJob`) if the job touches user-owned data, and carry the
  owner. There is no `HttpContext` in the worker; `JobUserMiddleware` populates `ICurrentUser`
  from `OwnerId`. Forgetting means the job silently reads nothing.
- Carry what the handler needs on the message where that is cheap. A light job that opens a
  database connection to look up what it was already told is not a light job.
- Go through `ICommandDispatcher`/`IQueryDispatcher` for domain state rather than reaching into a
  repository — that keeps validation and `Behaviors.LoggedAsync` on the path.
- **Throw on failure.** Wolverine's error policy decides retry versus dead-letter and can only see
  an exception; a handler that returns quietly looks identical to one that succeeded.
- **Do not log the outcome.** The pipeline and Wolverine already do. No "starting X", no
  "finished X".
- Any new port goes here as an interface, with its implementation in `src/Infrastructure/Jobs/`.

## 4. Register it — `src/Infrastructure/Jobs/JobRegistration.cs`

Two lists, and **both** are required:

- `MapJobs` — `PublishJob<TheJob>(opts);` so the job has a route. Runs on every host.
- `IncludeJobHandlers` — `.IncludeType<TheJobHandler>()` so the worker runs it. **Never called by
  the API**, which is what makes "jobs never run in the API" true.

Register any new port in `AddJobs`. `JobRegistrationTests` fails the build if an `IJob` in the
Application assembly is missing from `MapJobs`.

## 5. Enqueue it

From the domain event handler (step 2), or from the caller you chose. To run on a schedule,
register it with `JobDescriptor.Scheduled<TJob>(cron)`; the job must have a parameterless
constructor. Do not add a timer `IHostedService` — every replica would fire it.

## 6. Tests

| Project | Covers |
|---|---|
| `tests/Application.Tests` | The handler, ports substituted with NSubstitute. No Wolverine |
| `tests/Infrastructure.Tests/Jobs` | Registration completeness, lane→queue mapping |
| `tests/Worker.IntegrationTests` | The job reaching its handler on the right lane |

Two rules specific to the worker suite, both learned the hard way — see `src/Worker/CLAUDE.md`:
`IncludeExternalTransports()` is required on a tracking session, and a tracking session must never
be used to prove something did *not* happen.

## 7. Regenerate the adapters — do not skip this

```bash
dotnet run --project src/Worker -- codegen write
```

Commit the result. Debug stays green with this stale; only Release breaks, at startup. If an event
handler changed too, regenerate `src/Api`'s tree as well.

## 8. Finish

Run `/verify`, then dispatch the `dotnet-reviewer` agent over the diff.

If any step is blocked — an unclear lane, an unclear durability requirement, a port whose real
implementation does not exist yet — stop and say so rather than inventing a shape for the rest.
