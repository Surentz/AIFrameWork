---
name: regenerate
description: Use after changing a controller, a DTO, a [ProducesResponseType], or any Wolverine or job handler - which committed artifact to rebuild, the exact commands, and why the order matters. Also for a red codegen or contract CI job.
---

# Regenerating committed artifacts

Three artifacts in this repo are generated, **committed**, and re-checked by CI. Each exists
because the failure it prevents is invisible in a normal build: `dotnet build` succeeds, the
tests pass, and the breakage surfaces either in Release at startup or as a CI diff.

CI's `codegen` and `contract` jobs fail on a **diff**, not on a build error. A tree that builds,
tests and lints clean can still be rejected.

## What did you change?

| Change | Regenerate |
|---|---|
| A controller, a DTO, a `[ProducesResponseType]`, an enum crossing the wire | The API contract |
| A Wolverine handler on the event path (`IDomainEventHandler<T>`) | `src/Api`'s tree |
| A job handler, `JobUserMiddleware`, or an inbound broker handler (`ShipmentConfirmedHandler`) | `src/Worker`'s tree |
| A handler both hosts see, or you are unsure | **Both** trees |

When in doubt, regenerate everything and let `git diff` decide — the commands are idempotent and
an unchanged tree produces no diff.

## The API contract

`openapi/AiFramework.Api.json` is generated from the running application, and
`frontend/src/api/schema.d.ts` is generated from that.

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false Admin__ReconcileOnStart=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
```

Then commit both files.

Three things about that incantation, each of which cost someone an afternoon:

- **`dotnet restore` is a separate first step.** `dotnet msbuild` — unlike `dotnet build` — does
  not restore implicitly, so a fresh clone fails with NETSDK1004.
- **It cannot be folded in as `-t:"Restore;Build;..."`.** MSBuild evaluates the project once,
  before Restore writes NuGet's props, and the OpenAPI XML-comment source generator then fails
  with CS9137 about interceptors.
- **All three environment variables are required.** Generation runs the whole application:
  without a connection string it dies on `Program.cs`'s startup guard; with one but still durable,
  Wolverine's startup migration dials PostgreSQL; and with `Admin__ReconcileOnStart` left on,
  `AdminReconciler` dials it too and the generator dies with an `ObjectDisposedException` that
  names nothing useful (ADR 0020). The connection string is never actually opened — it only has
  to be non-empty. No database needs to be running. **Any new startup path that would open it
  needs its own switch here**, and `HealthTests` is the canary that catches one.

RabbitMQ is configured only when Wolverine is durable, so `Wolverine__Durable=false` covers it:
no broker variable is needed, and no broker need be running, for `codegen write` or the contract.

Generation is an explicit MSBuild target and deliberately **not** part of `dotnet build`. Running
it on every build was tried and reverted: it made a plain `dotnet build` of `src/Api` fail
without those variables even with the database up, breaking every developer's build.

## Wolverine adapters — there are TWO trees

Wolverine builds handler adapters with Roslyn, and Release ships without the compiler because it
costs 33MB. Release instead loads adapters generated ahead of time.

```bash
dotnet run --project src/Api -- codegen write      # event-path handlers
dotnet run --project src/Worker -- codegen write   # job handlers, JobUserMiddleware, ShipmentConfirmedHandler
```

Then commit the result.

**The worker cannot share the API's tree.** `TypeLoadMode.Static` resolves pre-built types out of
each host's own `opts.ApplicationAssembly`, and sharing would need the `Worker → Api` reference
the dependency rule forbids. ADR 0016.

**Debug does not need this, which is exactly the trap.** Debug still compiles adapters at
startup, so a stale tree leaves Debug green and the build succeeding, and breaks only in Release,
at startup. `WolverineCodegenTests` and `WorkerCodegenTests` catch a *missing* adapter in the
Debug suite; CI catches one that merely drifted.

`codegen write` needs only `ConnectionStrings__Default` and `Wolverine__Durable=false` — a
JasperFx command never starts hosted services, so the admin reconciler does not run.

**Why `codegen write` is reachable at all.** `Program.cs` routes to `RunJasperFxCommands(args)`
when args are present and to plain `RunAsync()` when they are not, so an ordinary `dotnet run`
or F5 skips JasperFx's command discovery. `WebApplicationFactory` passes args of its own, so
tests take the JasperFx branch and need `JasperFxEnvironment.AutoStartHost` — set once in
`tests/Api.IntegrationTests/JasperFxTestEnvironment.cs`. Without it nearly every integration test
fails with "The server has not been started". ADR 0005.

**Restart the worker afterwards.** New adapters do not take effect in a running worker.
`./scripts/worker.ps1` restarts just that window, leaving a working API and Vite alone.

## Two codegen failures that compile perfectly well

Both surface only when you run the command, and both were found building the job framework:

- **JasperFx will not upcast a concrete message to an interface for a middleware parameter.** It
  matches chain variables by exact type, so `Before(IUserScopedJob job, ...)` fails with "unable
  to resolve a variable of type IUserScopedJob". `JobUserMiddleware` takes the `Envelope` and
  pattern-matches instead.
- **Service location is refused under Wolverine 6's `NotAllowed` default.** A job handler
  injecting `ICommandDispatcher`/`IQueryDispatcher` triggers it, since ADR 0003's reflection-free
  dispatchers take `IServiceProvider`. `JobRegistration.IncludeJobHandlers` sets
  `ServiceLocationPolicy.AlwaysAllowed` **on the worker only**; the API keeps the strict default.

## If Windows and Linux disagree

**CI's Linux output is the authority.** On the Quartz branch, `codegen write` on Windows put
`RebuildOrderReportHandler`'s `jobCurrentUser` resolution after `queryDispatcher`, and CI on
Linux put it before — stable on each platform, not culture-dependent, and the two orders do the
same thing.

If a Windows regeneration reorders statements in a handler you did not change, **keep the
committed version of that file**. CI's "generated code is current" job is what decides.

## Checking without committing

```bash
git diff --exit-code -- src/Api/Internal src/Worker/Internal
git diff --exit-code -- openapi/ frontend/src/api/schema.d.ts
```

A non-zero exit means the committed artifact is stale. `/verify` step 3 runs both.

## The trap that no backend test can see

Enums cross the wire as names, via a `JsonStringEnumConverter` registered on **both**
`AddJsonOptions` (what controllers serialize with) and `ConfigureHttpJsonOptions` (what
`AddOpenApi`'s schema generator reads). Configure only the first and the API sends
`"OrderPlaced"` while the generated contract still says `type: integer` — so `schema.d.ts` types
it `number` and every client is wrong, with every backend test still green.
