# 0014. Centralized logging with Microsoft.Extensions.Logging and OpenTelemetry

**Date:** 2026-09-13
**Status:** Accepted

## Context

Before this work, logging in this repository was incidental. Handlers wrote nothing themselves,
`GlobalExceptionHandler` logged an unhandled exception at `Error` and nothing else, and the outbox
pumps produced no record of what they delivered, retried, or dropped. There was no way to answer
"what did user X's request actually do" from the log store, because there was no log store and no
per-request record to put in one — no correlation id tied a request to the work it caused, and
nothing exported logs anywhere durable. `docs/superpowers/plans/2026-09-13-centralized-logging.md`
is the design document this ADR formalizes; this records the decision, not the day-by-day
implementation.

Two things were asked for explicitly: that using the log be **seamless** (no handler should have
to remember to call it), and that an external store be plannable for later even though none was
running yet. Two choices followed from that: what writes the log records, and what they are
exported to.

### Writing API: Serilog vs. `Microsoft.Extensions.Logging`, and a base class vs. a pipeline behavior

Serilog's real advantages here — `LogContext.PushProperty` enrichment, its sink ecosystem, and
`appsettings`-driven sink wiring — are each undercut by something already true of this codebase:

- The sink ecosystem is the thing worth avoiding. A Serilog sink writing straight to OpenSearch
  couples `AiFramework.Api.dll` to the log store; changing stores would be a package change, a
  rebuild, and a redeploy, where exporting OTLP makes it a config-map edit.
- A file sink is not available regardless: `k8s/base/api.yaml` sets `readOnlyRootFilesystem: true`
  on purpose, and rolling files — Serilog's most common reason for existing — needs a volume that
  guarantee gives up.
- `Log.Logger` is a static singleton bootstrap, and this repository has no ambient statics on the
  request path (`IClock` exists specifically so `DateTimeOffset.UtcNow` is never called directly).
- It buys nothing on the code-volume axis that motivated "seamless": CA1848 is `error` here
  regardless of which logging library implements `ILogger`, so `[LoggerMessage]` source-generated
  partials get written either way.

A logging base class — the other option floated — fails for reasons specific to this codebase
rather than to base classes generally: CA1848 makes a convenience `LogInfo(string)` method on a
base class trip the same analyzer it exists to avoid, tripping a `#pragma` pair on a path every
handler uses; every handler in `Application` is `sealed` with a primary constructor, so introducing
inheritance there is a bigger change than logging itself; and a base class only makes each call
*shorter* — a handler can still forget to call it, which is not what "seamless" was asking for.

The dispatch pipeline already solves exactly this problem for validation, commit, and caching:
`MessagingRegistration.AddCommand` wraps every command in `ValidateAsync → handler → CommitAsync →
EvictAsync`, and `AddQuery` wraps every query in `CachedAsync` (ADR 0009). One more wrapper in that
same seam gives every command and query structured, correlated logging with zero handler edits —
which is the "seamless" outcome a base class was reaching for, in the idiom this codebase already
uses for the same shape of problem.

### Export target: a store chosen by configuration, not by code

No log store existed yet, and dev and the kind cluster (ADR 0010) have different constraints: dev
wants something a developer can start and stop without thinking about it, and the cluster is
where a real, queryable store belongs. OpenSearch was the store already planned for the cluster.
Coupling the API directly to it — a Serilog OpenSearch sink, or hand-rolled HTTP calls to its
bulk API — would mean the write path knows the store's shape, its index naming, its auth model.
OTLP is the alternative: a vendor-neutral wire format that both a lightweight dev tool (Seq) and
a store that does not speak OTLP natively (OpenSearch, fronted by an OpenTelemetry Collector) can
receive, without either shape ever appearing in application code.

## Decision

**Write logs through plain `Microsoft.Extensions.Logging`, using `[LoggerMessage]`
source-generated partials as the only writing API; make logging automatic by wrapping every
command and query dispatch in a pipeline behavior, not by adding a base class; and export over
OTLP so the receiving store is a deployment/configuration choice, never a code dependency.**

Concretely:

- `Behaviors.LoggedAsync` (`src/Infrastructure/Messaging/Behaviors.cs`) is the outermost wrapper
  around both `AddCommand`'s and `AddQuery`'s pipelines. No handler in `Application` writes a
  logging call to report its own outcome, and none should — the behavior already does, logging
  `typeof(TRequest).Name` rather than the request instance so a request carrying a plaintext
  password (`SignIn`, `RegisterUser`, `ChangePassword`) can never reach the log store.
  `SensitiveCommandLoggingTests` exists to catch a regression of exactly that. Success logs at
  `Debug`; a failed `Result` is levelled by its `ErrorKind` (`Validation`/`NotFound` stay `Debug`,
  `Conflict`/`Unauthorized` are `Information`, anything else is `Warning`); a thrown exception logs
  `Faulted` at `Warning` and rethrows unchanged, because `GlobalExceptionHandler` still owns
  turning it into a 500 and logging the exception object itself — `LoggedAsync` never logs the
  exception, or the same failure would be recorded twice.
- `OutboxWorkItemProcessor` follows the identical shape by hand, because outbox delivery sits
  outside the command/query pipeline: dispatched (Debug), retry scheduled (Information),
  dead-lettered (Warning), each inside a `logger.BeginScope` carrying `MessageId`/`EventType`/
  `Attempt`.
- `builder.AddObservability()` (`src/Api/Observability/ObservabilityRegistration.cs`) registers
  OpenTelemetry logging export and a tracer provider unconditionally — tracing itself is always
  on, so `Activity.Current` is never null and every `ProblemDetails.traceId` resolves to something
  real, whether or not export is configured. Export is the one thing gated on configuration
  (`Observability:Otlp:Enabled`, default `false`), so a developer with nothing running, and every
  CI job, still gets a green build with nothing attempting a network call.
- The store is never named in code. `Observability:Otlp:Endpoint` is an OTLP/HTTP receiver root;
  `docker-compose.yml`'s opt-in `seq` profile and `./scripts/dev.ps1 -WithSeq` point it at Seq for
  local development, and `k8s/components/observability`'s OTel Collector — the bridge OpenSearch
  needs, since it does not ingest OTLP natively — points it at OpenSearch for the kind cluster.
  Both are opt-in; neither is required for an ordinary `dotnet run` or a green `/verify`.
- W3C trace context crosses the one asynchronous boundary in the system that would otherwise break
  it: `OutboxMessage.TraceParent` captures `Activity.Current?.Id` at the point `DomainEventsInterceptor`
  still has the request's own `Activity`, and `OutboxWorkItemProcessor` restores it as the parent
  of the delivery `Activity` it starts. A delivery's own logs, and anything a handler logs from
  inside it, therefore carry the same `TraceId` as the HTTP request that caused them.

## Consequences

**No handler can forget to log its own outcome, and no handler can log a secret by accident.**
Both follow from the same structural fact: the behavior, not the handler, decides what gets
written. A new command or query gets correct logging for free the moment it is registered through
`AddCommand`/`AddQuery`; nothing about writing the handler itself changes.

**The application never depends on where logs end up.** Swapping Seq for OpenSearch, or either for
something else that speaks OTLP, is a configuration and deployment change — a compose profile, a
Kustomize component, an endpoint value — never a `.csproj` reference or a rebuild. That was the
whole point of routing through OTLP instead of a store-specific sink.

**A handler that wants to say something only it knows still can.** `Application` may inject
`ILogger<T>` for something genuinely domain-meaningful that the behavior has no way to know (see
`src/Application/CLAUDE.md`'s own Logging section) — never for control flow the behavior already
reports. That line is convention-and-review-enforced, not backed by an architecture test, the same
posture the "DI only" cell in root `CLAUDE.md`'s dependency table takes.

**Tracing is always on; export is the only switch.** A request gets a real, correlatable
`TraceId` in its `ProblemDetails` response whether or not `Observability:Otlp:Enabled` is `true`
anywhere. That is deliberate — the tracer provider costs nothing meaningful to run and gates one
of the exception handler's existing fields — but it means "tracing" and "logging export" are two
different switches, and disabling export does not disable instrumentation.

**Two silent-failure footguns exist in the OTLP exporter and are now covered.**
`OtlpExporterOptions.Protocol` defaults to gRPC when left unset, and the SDK appends nothing to an
explicitly-set `Endpoint` — both confirmed empirically while implementing this (see
`ObservabilityRegistration.BuildOtlpEndpoint`'s own remarks), and both fail with no exception and
no log line anywhere, just an empty store. `BuildOtlpEndpoint` is the one place that gets both
right; nothing should construct an OTLP endpoint by hand elsewhere.

**Nothing on the auth path is cached, and nothing about this ADR changes that.** ADR 0008's
lockout state and ADR 0011's security-stamp read must happen on every request regardless of what
gets logged about them; `Behaviors.LoggedAsync` wraps the same pipeline `CachedAsync` does, but
logging and caching remain independent opt-ins per query.

## Alternatives considered

**Serilog**, on the strength of its enrichment model and sink ecosystem — rejected for the reasons
in Context: the sink ecosystem is the coupling being avoided, a file sink cannot be used under
`readOnlyRootFilesystem: true`, `Log.Logger` reintroduces an ambient static this codebase has
otherwise eliminated, and CA1848 taxes it exactly as much as `Microsoft.Extensions.Logging`. It
remains a reasonable choice for a codebase without these specific constraints; it is not the
cheaper one here.

**A logging base class**, per the original request — rejected because it does not make logging
automatic, only shorter (a handler can still forget to call it), because every handler in
`Application` is `sealed` with a primary constructor and gains nothing from an inheritance
hierarchy introduced solely to carry a logging method, and because the pipeline-behavior seam
already existed and already solved this exact shape of problem for validation, commit, and
caching.

**A Serilog (or hand-rolled) sink writing directly to OpenSearch** — rejected because it makes
"which log store" a compile-time, deployable-artifact decision instead of a configuration one, and
because OpenSearch does not ingest OTLP or a bespoke format natively either way; something has to
bridge the two regardless, and an OpenTelemetry Collector is the standard bridge rather than a
bespoke one this repository would own.

**Application Insights / a hosted SaaS log platform** — not pursued. The user had already settled
on OpenSearch as the eventual store, self-hosted alongside the rest of the kind cluster stack; a
hosted platform would add an external account, billing, and network dependency this repository has
avoided everywhere else (ADR 0006's own username/password choice over an external identity
provider follows the same instinct).

**Metrics and dashboards, alongside logging, in the same effort** — deliberately out of scope.
`.WithMetrics()` is a few lines away given the OpenTelemetry packages this ADR already adds, but
folding an SLO conversation into a logging decision would have widened this past what one ADR
should decide. Left for a future ADR if and when it is needed.
