# OpenTelemetry, used properly — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the existing log-and-trace pipeline into one an operator actually uses: trace links from
the monitoring UI into the log store, production-grade resource/auth/sampling configuration, and a
metrics pipeline with dashboards and alerts on the local cluster.

**Architecture:** Four phases on one branch (`claude/opentelemetry-observability`), one PR typed
`feat(observability)`. Phase A carries a configurable trace-link template from the API to the SPA and
normalises the two trace-id shapes the backend already emits. Phase B stamps `service.version` and
`deployment.environment.name`, adds OTLP auth headers, and hardens the collector. Phase C records ADR
0027 and adds OTel metrics (ASP.NET Core, HttpClient, `System.Runtime`, Npgsql, Wolverine) exported
over the same OTLP pipe, with Prometheus + Grafana behind the existing opt-in Kustomize component.
Phase D adds head sampling in the app, tail sampling in the collector, and Prometheus alert rules.

**Tech Stack:** .NET 10 / OpenTelemetry .NET 1.19, React 19 + TanStack Query + Vitest/MSW, OTel
Collector contrib 0.160.0, Prometheus 3.x (native OTLP receiver), Grafana, kind.

**Spec:** the recommendation in this session's conversation, restated here as the source of truth:

1. **Trace links (A)** — show `TraceId` in the job-run, sign-in and admin-action tables, as a link
   built from a configured template (`{traceId}` placeholder) or plain text when none is set; show a
   500's `traceId` on the error display; normalise the two id shapes (`ProblemDetails` carries the
   full W3C `traceparent`, the three monitoring tables store the bare 32-hex trace id).
2. **Hardening (B)** — `service.version` + `deployment.environment.name` resource attributes; OTLP
   auth-header support; `memory_limiter` in the collector.
3. **Metrics (C)** — an ADR that places OTel metrics beside ADR 0021's Postgres telemetry;
   `.WithMetrics(...)` on both hosts; a `metrics` pipeline in the collector; Prometheus + Grafana on
   the cluster; RabbitMQ and Postgres metrics collected by the collector, not the app.
4. **Sampling and alerts (D)** — parent-based ratio head sampling in the app (default: keep all),
   tail sampling in the collector (keep errors and slow traces, a fraction of the rest), and
   Prometheus alert rules for error rate, p95 latency, dead letters, broker backlog and pool
   saturation.

## Investigation findings (what the plan argues from)

- **The API already returns `TraceId`** on `JobRunResponse`, `SignInEventResponse` and
  `AdminActionResponse` (`src/Api/Monitoring/MonitoringDtos.cs:51,143,303`); the SPA never renders
  it (`grep '\.traceId' frontend/src --include=*.tsx` is empty). ADR 0021 promised "one deep link".
- **Two id shapes.** `GlobalExceptionHandler.cs:41`, `ResultExtensions.cs:58` and `Program.cs:246`
  write `Activity.Current?.Id` — `00-<32 hex>-<16 hex>-<2 hex>` — and
  `ObservabilityRegistrationTests.AnUnhandledException_CarriesAW3CFormattedTraceId` pins that. The
  three audit writers (`JobRunMiddleware.cs:73`, `SignInAudit.cs:79`, `AdminAudit.cs:105`) store
  `Activity.Current?.TraceId.ToString()` — bare 32 hex. **Decision:** leave the backend as is (it is
  ASP.NET Core's own `ProblemDetails` convention and a pinned test); normalise in the SPA.
- **Where the template lives.** Controllers may reach Infrastructure only for DI
  (`src/Api/CLAUDE.md`), and `ObservabilityOptions` is an Infrastructure type, so the template gets an
  Api-owned options class bound from the `Monitoring` section: `Monitoring__TraceLinkTemplate`. It
  travels on `GET /api/monitoring/access`, which every monitoring page can already call and which is
  admin-only — a non-admin never learns the log store's address.
- **The SPA's error display** is ~30 copies of `<p className="alert" role="alert">{x.error.message}</p>`.
  `react-conventions` already names an `<ErrorPanel error={...} />` that was never built; building it
  is how every error gains the reference.
- **Metrics sources, confirmed from the restored packages** (`~/.nuget/packages`):
  - `OpenTelemetry.Instrumentation.Runtime` 1.19 on .NET 9+ only subscribes to the built-in
    `System.Runtime` meter (its README) — so `AddMeter("System.Runtime")` replaces it and the unused
    package reference in `src/Api/AiFramework.Api.csproj:56` goes.
  - `AddAspNetCoreInstrumentation()` / `AddHttpClientInstrumentation()` on a `MeterProviderBuilder`
    enable the built-in .NET 8+ meters (`http.server.request.duration`, `http.client.request.duration`, …).
  - `Npgsql.OpenTelemetry` 10.0.3 has `MeterProviderBuilder.AddNpgsqlInstrumentation()`; Npgsql emits
    `db.client.operation.duration`, `db.client.connection.count`, …
  - Wolverine 6.41 names its meter `Wolverine:<ServiceName>` (no `ServiceName` is set, so it is the
    entry assembly name); instruments include `wolverine-execution-time`,
    `wolverine-dead-letter-queue`, `wolverine-messages-received`. Subscribe with `AddMeter("Wolverine:*")`.
- **Seq 2026.1** (the pinned `datalust/seq:2026.1.17114`) ingests OTLP metrics at
  `/ingest/otlp/v1/metrics`, so `-WithSeq` shows metrics with no extra container.
- **Resource today:** only `service.name` and `service.instance.id`. No version is set anywhere;
  `Dockerfile.api` copies no `.git`, so container builds report `1.0.0`.
- **Collector** (`k8s/components/observability/otel-collector.yaml`) runs `batch` only, image
  `otel/opentelemetry-collector-contrib:0.160.0` (contrib, so `tail_sampling`, `rabbitmq` and
  `postgresql` receivers are available). Broker/DB credentials are in `app-secrets`
  (`RABBITMQ_DEFAULT_USER`, `RABBITMQ_DEFAULT_PASS`, `POSTGRES_PASSWORD`).
- **Tooling on this machine:** Docker 29.7, kind 0.33, kubectl 1.36, 31.7 GB RAM, SDK 10.0.400,
  Node 24.20.0 — matches CI. Every phase is verifiable end to end locally.

## Global Constraints

- Warnings are errors (compiler, analyzers, MSBuild). CA1848: logging only through `[LoggerMessage]`.
- Nullable enabled; no `!` in TypeScript, no `any`.
- Never `catch (Exception)`.
- Config keys from the environment use double underscores: `Monitoring__TraceLinkTemplate`,
  `Observability__Otlp__Headers`, `Observability__Otlp__Metrics`, `Observability__Otlp__TraceSampleRatio`.
- No secrets in `appsettings*.json`. OTLP headers carry API keys: environment/secret only.
- Controllers touch Infrastructure only via DI.
- A controller/DTO change regenerates `openapi/AiFramework.Api.json` + `frontend/src/api/schema.d.ts`
  (`regenerate` skill; three placeholder env vars).
- `Observability:Otlp:Enabled` stays **false** by default everywhere; every new signal is gated by it.
- Frontend lint runs `--max-warnings 0`.
- One PR, type `feat`, title `feat(observability): …`. Commit subjects `feat(observability): …`.
- `dotnet-reviewer` / `react-reviewer` before committing each phase.

## Review Focus

1. **A hostile or malformed template** (`javascript:alert(1)?{traceId}`, a relative path, no
   `{traceId}`) — the API refuses to start (`ValidateOnStart`), and the SPA independently never
   renders a link whose protocol is not `http:`/`https:`. Test: options validator theory (Task 1) and
   `buildTraceUrl` rejecting `javascript:` (Task 2).
2. **A traceId that is neither shape** — `HttpContext.TraceIdentifier`'s `0HN…:00000001` fallback, or
   `null` — shows no reference and no link, never a broken one. Test: `normaliseTraceId` cases (Task 2),
   `ErrorPanel` with a non-W3C id (Task 2), `TraceLink` with `null` (Task 3).
3. **A 4xx is not a support ticket** — a wrong password or a validation error must not show
   "Reference: …". Test: `ErrorPanel` shows the reference for 500 and not for 400 (Task 2).
4. **An empty `Observability__Otlp__Headers`** (an unset secret rendered as `""`) must not break
   export. Test: the exporter-configuration helper leaves `Headers` null for empty input (Task 6).
5. **A metrics receiver that is not there** — metrics are exported only when `Otlp:Enabled` *and*
   `Otlp:Metrics` are true; a host with neither still starts and still records traffic to Postgres.
   Test: the metrics pipeline test runs with export off and asserts the in-memory reader still sees
   instruments (Task 9); `HealthTests` still pass.

---

## Phase A — trace links

### Task 1: The template reaches the API contract

**Files:**
- Create: `src/Api/Monitoring/MonitoringPageOptions.cs`
- Modify: `src/Api/Monitoring/MonitoringDtos.cs` (`MonitoringAccessResponse`)
- Modify: `src/Api/Monitoring/MonitoringController.cs`
- Modify: `src/Api/Program.cs` (bind + validate beside the other `Configure<>` calls)
- Test: `tests/Api.IntegrationTests/Monitoring/TraceLinkTemplateTests.cs`
- Regenerate: `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts`

**Interfaces:**
- Produces: `MonitoringAccessResponse.TraceLinkTemplate` (`string?`, JSON `traceLinkTemplate`), null
  when unconfigured. Config key `Monitoring:TraceLinkTemplate`; placeholder `{traceId}`.
- Produces: `MonitoringPageOptions.IsValidTemplate(string? template): bool`, `MonitoringPageOptions.Placeholder`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Collection(nameof(ApiFactoryCollection))]
public sealed class TraceLinkTemplateTests(ApiFactory factory)
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("http://localhost:55341/#/events?filter=@TraceId%3D'{traceId}'", true)]
    [InlineData("https://logs.example.com/trace/{traceId}", true)]
    [InlineData("https://logs.example.com/search", false)]           // no placeholder
    [InlineData("/relative/{traceId}", false)]                       // not absolute
    [InlineData("javascript:alert(1)//{traceId}", false)]            // not http(s)
    [InlineData("ftp://logs.example.com/{traceId}", false)]
    public void IsValidTemplate(string? template, bool expected) =>
        MonitoringPageOptions.IsValidTemplate(template).Should().Be(expected);

    [Fact]
    public async Task WithNoTemplateConfigured_AccessCarriesNull() { /* admin client; body.traceLinkTemplate is null */ }

    [Fact]
    public async Task WithATemplateConfigured_AccessCarriesIt() { /* WithWebHostBuilder(UseSetting("Monitoring:TraceLinkTemplate", ...)) */ }

    [Fact]
    public void AnInvalidTemplate_StopsTheHostStarting() { /* WithWebHostBuilder(invalid).CreateClient() throws OptionsValidationException */ }
}
```

The two request tests build the admin session on the derived host (`CreateClient()`, register, then
`factory.SetRoleAsync` — same database).

- [ ] **Step 2: Run to verify they fail** — `dotnet test tests/Api.IntegrationTests --filter TraceLinkTemplateTests`; expected: compile error (`MonitoringPageOptions` missing).

- [ ] **Step 3: Implement**

```csharp
namespace AiFramework.Api.Monitoring;

/// Configuration the monitoring page reads. Api-owned rather than a member of Infrastructure's
/// ObservabilityOptions because a controller may reach Infrastructure only through DI.
public sealed class MonitoringPageOptions
{
    public const string Placeholder = "{traceId}";

    public string? TraceLinkTemplate { get; set; }

    public static bool IsValidTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return true;
        if (!template.Contains(Placeholder, StringComparison.Ordinal)) return false;
        var sample = template.Replace(Placeholder, "0123456789abcdef0123456789abcdef", StringComparison.Ordinal);
        return Uri.TryCreate(sample, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
```

`Program.cs`:

```csharp
builder.Services.AddOptions<MonitoringPageOptions>()
    .Bind(builder.Configuration.GetSection("Monitoring"))
    .Validate(o => MonitoringPageOptions.IsValidTemplate(o.TraceLinkTemplate),
        "Monitoring:TraceLinkTemplate must be an absolute http(s) URL containing {traceId}.")
    .ValidateOnStart();
```

Controller: inject `IOptions<MonitoringPageOptions>`, set
`TraceLinkTemplate = string.IsNullOrWhiteSpace(t) ? null : t`. DTO: `public string? TraceLinkTemplate { get; init; }`
with a doc comment naming the placeholder and why it is here (admin-only endpoint).

- [ ] **Step 4: Run tests to verify they pass**, then the whole `Monitoring` folder.
- [ ] **Step 5: Regenerate the contract** (regenerate skill) and confirm the diff is the one new property.
- [ ] **Step 6: Commit** — `feat(observability): carry a trace-link template on the monitoring access response`

### Task 2: Trace-id helpers and `ErrorPanel`

**Files:**
- Create: `frontend/src/api/traceId.ts`, `frontend/src/api/traceId.test.ts`
- Create: `frontend/src/components/ErrorPanel.tsx`, `frontend/src/components/ErrorPanel.test.tsx`
- Modify: `frontend/src/styles/controls.css` (`.alert__reference`)
- Modify: every `<p className="alert" role="alert">{…error.message}</p>` site (AppLayout, auth pages,
  RequireAuth/RequireRole, fulfilment, orders/products, monitoring pages) → `<ErrorPanel error={…} />`

**Interfaces:**
- Produces: `normaliseTraceId(value: string | null | undefined): string | undefined` — 32 lowercase hex, from either shape.
- Produces: `buildTraceUrl(template: string | null | undefined, traceId: string | null | undefined): string | undefined` — only `http:`/`https:`.
- Produces: `ErrorPanel({ error }: { readonly error: Error })` — renders `<p className="alert" role="alert">`; appends
  `Reference: <code>{id}</code>` only for `ApiError` with `status >= 500` and a normalisable id.

- [ ] **Step 1: Failing tests** — `traceId.test.ts`:

```ts
describe('normaliseTraceId', () => {
  it.each([
    ['0af7651916cd43dd8448eb211c80319c', '0af7651916cd43dd8448eb211c80319c'],
    ['00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01', '0af7651916cd43dd8448eb211c80319c'],
    ['0AF7651916CD43DD8448EB211C80319C', '0af7651916cd43dd8448eb211c80319c'],
  ])('reads %s', (input, expected) => { expect(normaliseTraceId(input)).toBe(expected); });

  it.each([null, undefined, '', '0HN7GLM2S8B6J:00000001', '00000000000000000000000000000000', 'abc123'])(
    'rejects %s', (input) => { expect(normaliseTraceId(input)).toBeUndefined(); });
});

describe('buildTraceUrl', () => {
  const id = '0af7651916cd43dd8448eb211c80319c';
  it('substitutes every placeholder', () => {
    expect(buildTraceUrl('https://x.test/t/{traceId}?q={traceId}', id)).toBe(`https://x.test/t/${id}?q=${id}`);
  });
  it('accepts a full traceparent', () => {
    expect(buildTraceUrl('https://x.test/{traceId}', `00-${id}-b7ad6b7169203331-01`)).toBe(`https://x.test/${id}`);
  });
  it('refuses a non-http protocol', () => { expect(buildTraceUrl('javascript:alert(1)//{traceId}', id)).toBeUndefined(); });
  it('needs both a template and an id', () => {
    expect(buildTraceUrl(undefined, id)).toBeUndefined();
    expect(buildTraceUrl('https://x.test/{traceId}', null)).toBeUndefined();
  });
});
```

`ErrorPanel.test.tsx`: 500 with traceparent → alert contains message and `Reference: 0af7…319c`;
400 with traceId → no "Reference"; 500 with `0HN…:1` → no "Reference"; plain `Error` → message only.

- [ ] **Step 2: Run** `npm test --prefix frontend -- traceId ErrorPanel` → fail (modules missing).
- [ ] **Step 3: Implement**

```ts
// frontend/src/api/traceId.ts
const Bare = /^[0-9a-f]{32}$/;
const TraceParent = /^[0-9a-f]{2}-([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$/;
const Invalid = '0'.repeat(32);

export function normaliseTraceId(value: string | null | undefined): string | undefined {
  if (!value) return undefined;
  const lower = value.trim().toLowerCase();
  const id = Bare.test(lower) ? lower : TraceParent.exec(lower)?.[1];
  return id === undefined || id === Invalid ? undefined : id;
}

export function buildTraceUrl(template: string | null | undefined, traceId: string | null | undefined): string | undefined {
  const id = normaliseTraceId(traceId);
  if (!template || id === undefined) return undefined;
  const candidate = template.replaceAll('{traceId}', encodeURIComponent(id));
  try {
    const url = new URL(candidate);
    return url.protocol === 'http:' || url.protocol === 'https:' ? candidate : undefined;
  } catch {
    return undefined; // not a URL at all: render no link rather than a broken one
  }
}
```

```tsx
// frontend/src/components/ErrorPanel.tsx
export function ErrorPanel({ error }: ErrorPanelProps): React.JSX.Element {
  const reference = error instanceof ApiError && error.status >= 500 ? normaliseTraceId(error.traceId) : undefined;
  return (
    <p className="alert" role="alert">
      {error.message}
      {reference !== undefined && (
        <span className="alert__reference"> Reference: <code>{reference}</code></span>
      )}
    </p>
  );
}
```

(The `catch {}` above is the one place a failure is deliberately turned into "no link" — `new URL`
throwing *is* the validity answer; comment it as such so lint and review read it that way.)

- [ ] **Step 4: Replace the call sites.** Mechanical; keep each surrounding conditional exactly as it is.
      Sites that render a non-error `.alert` (UsersPage `Confirm`'s warning) stay untouched.
- [ ] **Step 5: Run** `npm run lint --prefix frontend && npm test --prefix frontend && npm run build --prefix frontend` — all green, existing `toHaveTextContent('Something went wrong.')` assertions still pass (substring match).
- [ ] **Step 6: Commit** — `feat(observability): show a 500's trace reference on every error`

### Task 3: Trace links in the monitoring tables

**Files:**
- Create: `frontend/src/features/monitoring/TraceLink.tsx`, `TraceLink.test.tsx`
- Modify: `frontend/src/features/monitoring/queries.ts` (`useTraceLinkTemplate`)
- Modify: `JobsPage.tsx`, `LoginsPage.tsx`, `UsersPage.tsx` (a `Trace` column), `monitoring.css` (`.trace-id`)
- Modify: `frontend/src/test/handlers.ts` (fixtures: real-shaped ids; access carries `traceLinkTemplate`)
- Modify: `JobsPage.test.tsx`, `LoginsPage.test.tsx`, `UsersPage.test.tsx`

**Interfaces:**
- Consumes: `buildTraceUrl`, `normaliseTraceId` (Task 2); `MonitoringAccess.traceLinkTemplate` (Task 1).
- Produces: `useTraceLinkTemplate(): string | undefined`; `TraceLink({ traceId, template })`.

- [ ] **Step 1: Failing tests** — `TraceLink.test.tsx`: with template → `link` named
  `Open trace 0af7… in the log store` whose `href` is the built URL and opens in a new tab; without →
  the id as text, no link; `null` → `—`. Page tests: the jobs table has a link to the fixture's URL.
- [ ] **Step 2: Run** → fail.
- [ ] **Step 3: Implement**

```tsx
export function TraceLink({ traceId, template }: TraceLinkProps): React.JSX.Element {
  const id = normaliseTraceId(traceId);
  if (id === undefined) return <span aria-label="No trace">—</span>;
  const href = buildTraceUrl(template, id);
  const code = <code className="trace-id">{id}</code>;
  return href === undefined ? code : (
    <a href={href} target="_blank" rel="noreferrer" aria-label={`Open trace ${id} in the log store`}>{code}</a>
  );
}

export function useTraceLinkTemplate(): string | undefined {
  return useMonitoringAccess().data?.traceLinkTemplate ?? undefined;
}
```

A `Trace` column (`<th scope="col">Trace</th>`) at the end of each of the three tables.
- [ ] **Step 4: Run** lint + tests + build.
- [ ] **Step 5: Commit** — `feat(observability): link each monitoring row to its trace`

### Task 4: Every environment gets a working link

**Files:**
- Modify: `scripts/dev.ps1` (`-WithSeq` sets `Monitoring__TraceLinkTemplate` for the API process, and clears it after)
- Modify: `k8s/components/observability/kustomization.yaml` (patch `Monitoring__TraceLinkTemplate` into `app-config`)
- Modify: `.claude/skills/observability/SKILL.md`, `.claude/skills/local-dev/SKILL.md` if it lists `-WithSeq`'s env vars

- [ ] **Step 1: Determine Seq's URL empirically.** `docker compose --profile observability up -d seq`;
  export one trace by running the API with `-WithSeq`-equivalent env vars and hitting a 500 route;
  find the SPA route that filters by `@TraceId` (inspect the Seq UI bundle / try
  `http://localhost:55341/#/events?filter=@TraceId%20%3D%20'<id>'`), confirm it shows the request's
  events. Record the exact template in the observability skill.
- [ ] **Step 2: Determine OpenSearch Dashboards' URL** — deferred to Task 10's cluster run (same
  bring-up); the kustomization patch lands there with the verified value.
- [ ] **Step 3: Wire `dev.ps1`.** Same shape as the existing `Observability__Otlp__*` set/clear pairs.
- [ ] **Step 4: Verify** `./scripts/dev.ps1 -WithSeq` → monitoring page link opens the matching events.
- [ ] **Step 5: Commit** — `feat(observability): point dev's trace links at Seq`

## Phase B — hardening

### Task 5: `service.version` and `deployment.environment.name`

**Files:**
- Create: `src/Infrastructure/Observability/ObservabilityResource.cs`
- Modify: `src/Api/Observability/ObservabilityRegistration.cs`, `src/Worker/Observability/WorkerObservability.cs`
- Modify: `Dockerfile.api` (`ARG SOURCE_REVISION`, `-p:SourceRevisionId=`), `deploy/deploy.ps1` (`--build-arg SOURCE_REVISION=$(git rev-parse --short HEAD)`)
- Test: `tests/Infrastructure.Tests/Observability/ObservabilityResourceTests.cs`

**Interfaces:**
- Produces: `ObservabilityResource.ServiceVersionOf(Assembly assembly): string` (informational version, `+sha` kept);
  `ObservabilityResource.DeploymentAttributes(string environmentName): IEnumerable<KeyValuePair<string, object>>`.

- [ ] **Step 1: Failing tests** — version of an assembly carrying `AssemblyInformationalVersion` is that string;
  of one without, falls back to `AssemblyName.Version`; attributes contain `deployment.environment.name`.
- [ ] **Step 2: Run** → fail. **Step 3: Implement** (pure; no SDK types — Infrastructure only has `OpenTelemetry.Api`).
  Hosts: `.AddService(serviceName, serviceVersion: ObservabilityResource.ServiceVersionOf(typeof(Program).Assembly), serviceInstanceId: …)
  .AddAttributes(ObservabilityResource.DeploymentAttributes(builder.Environment.EnvironmentName))`.
- [ ] **Step 4: Verify** in Seq: a record's resource shows `service.version` (`1.0.0+<sha>` locally) and `deployment.environment.name=Development`.
  `docker build --build-arg SOURCE_REVISION=abc1234 --target runtime` → the image reports `1.0.0+abc1234`.
- [ ] **Step 5: Commit** — `feat(observability): stamp service version and environment on telemetry`

### Task 6: OTLP auth headers

**Files:**
- Modify: `src/Infrastructure/Observability/ObservabilityOptions.cs` (`OtlpOptions.Headers`)
- Modify: both hosts — one private-to-host `ConfigureExporter(OtlpExporterOptions, OtlpOptions, string signalPath)` used by every exporter
- Test: `tests/Api.IntegrationTests/Observability/ObservabilityRegistrationTests.cs`, a Worker counterpart

**Interfaces:** `OtlpOptions.Headers: string?` in OTLP's `k=v,k2=v2` format; `ObservabilityRegistration.ConfigureExporter` (internal/public static, testable).

- [ ] **Step 1: Failing tests** — helper sets `Protocol = HttpProtobuf`, `Endpoint = root + path`, and `Headers` when set; leaves `Headers` null for `null`/`""`/whitespace.
- [ ] **Step 2–4:** implement, run, pass. Doc comment: **a secret — environment or secret store only, never appsettings**.
- [ ] **Step 5: Commit** — `feat(observability): send OTLP auth headers to hosted backends`

### Task 7: Collector `memory_limiter`

**Files:** `k8s/components/observability/otel-collector.yaml`

```yaml
processors:
  memory_limiter:
    check_interval: 1s
    limit_percentage: 80
    spike_limit_percentage: 25
  batch: {}
# every pipeline: processors: [memory_limiter, batch]  (memory_limiter first, per its README)
```

- [ ] **Verify** in Task 10's cluster run (`kubectl logs deploy/otel-collector` shows the limiter's start line; pipelines still deliver).
- [ ] **Commit** — `feat(observability): bound the collector's memory`

## Phase C — metrics

### Task 8: ADR 0027 — OpenTelemetry metrics beside operational telemetry

**Files:** `docs/adr/0027-opentelemetry-metrics.md` (via the `adr` skill's template)

Decision: ADR 0021's Postgres tables stay the source for the monitoring page (they work with no
collector); OTel metrics are for runtime/infrastructure signals and alerting, exported over the same
OTLP pipe (ADR 0015's "the store is configuration"), gated by `Otlp:Enabled` + `Otlp:Metrics`. On the
cluster they land in Prometheus (native OTLP receiver, not scraping — the app never opens a metrics
port) and are shown in Grafana; OpenSearch keeps logs and traces. RabbitMQ and Postgres are measured
by the collector, not the app. Rejected: Prometheus scrape endpoint in the app (a second export path
and a port per pod), OpenSearch for metrics (no PromQL, no alerting), replacing `traffic_buckets`.

- [ ] **Commit** with Task 9.

### Task 9: Metrics on both hosts

**Files:**
- Modify: `src/Infrastructure/Observability/InfrastructureTracing.cs` → add `AddInfrastructureMetrics(this MeterProviderBuilder)` (Npgsql)
- Modify: `src/Infrastructure/Observability/ObservabilityOptions.cs` (`OtlpOptions.Metrics = true`), `OtlpEndpoint.MetricsPath = "v1/metrics"`
- Modify: both hosts (`.WithMetrics(...)`), `src/Api/AiFramework.Api.csproj` (drop `OpenTelemetry.Instrumentation.Runtime`)
- Modify: `src/Api/appsettings.json`, `src/Worker/appsettings.json` (`"Metrics": true` beside `"Traces": true`)
- Test: `tests/Api.IntegrationTests/Observability/MetricsPipelineTests.cs` (+ `OpenTelemetry.Exporter.InMemory` 1.19.1 test-only)

```csharp
.WithMetrics(metrics =>
{
    metrics.AddAspNetCoreInstrumentation()          // Api only
        .AddHttpClientInstrumentation()
        .AddMeter("System.Runtime")
        .AddMeter("Wolverine:*")
        .AddInfrastructureMetrics();
    if (options.Otlp.Enabled && options.Otlp.Metrics)
        metrics.AddOtlpExporter(e => ConfigureExporter(e, options.Otlp, OtlpEndpoint.MetricsPath));
});
```

- [ ] **Step 1: Failing test** — a derived host adds an in-memory metric reader
  (`ConfigureOpenTelemetryMeterProvider(m => m.AddInMemoryExporter(list))`), makes an authenticated
  request, `ForceFlush`es, and asserts `http.server.request.duration`, a `dotnet.*` runtime metric and
  a `db.client.*` metric are present — with `Otlp:Enabled=false`.
- [ ] **Step 2–4:** implement; test passes; `HealthTests` still pass; contract generation still runs.
- [ ] **Step 5: Verify in Seq** (`-WithSeq`): metrics explorer lists `http.server.request.duration`,
  `dotnet.gc.collections`, `db.client.connection.count`, a `wolverine-*` instrument from each host.
- [ ] **Step 6: Commit** — `feat(observability): export runtime, HTTP, database and Wolverine metrics`

### Task 10: Metrics on the cluster — Prometheus, Grafana, broker and database receivers

**Files (all under `k8s/components/observability/`):**
- Modify: `otel-collector.yaml` — `metrics` pipeline: receivers `[otlp, rabbitmq, postgresql]`,
  processors `[memory_limiter, batch]`, exporter `otlphttp/prometheus`
  (`metrics_endpoint: http://prometheus:9090/api/v1/otlp/v1/metrics`); credentials from `app-secrets` via env.
- Create: `prometheus.yaml` (ConfigMap `prometheus.yml` with `otlp.promote_resource_attributes`,
  Deployment with `--web.enable-otlp-receiver`, 7d retention, Service 9090)
- Create: `grafana.yaml` (provisioned Prometheus datasource + one dashboard ConfigMap, anonymous viewer, Service 3000)
- Modify: `kustomization.yaml` (resources; `Monitoring__TraceLinkTemplate` patch with the verified Dashboards URL)
- Modify: `deploy/deploy.ps1` (port-forward hints for Grafana and Prometheus)

- [ ] **Step 1:** bring the cluster up — `./deploy/start-cluster.ps1`, `./deploy/deploy.ps1 -WithObservability`.
- [ ] **Step 2:** verify in Prometheus (`/api/v1/label/__name__/values`) the exact metric and label names
  for HTTP, runtime, Npgsql, Wolverine, `rabbitmq_*`, `postgresql_*`; fix the dashboard's queries to them.
- [ ] **Step 3:** verify OpenSearch Dashboards' trace URL against a real trace id; land the kustomization patch.
- [ ] **Step 4:** Grafana dashboard renders every panel with data.
- [ ] **Step 5: Commit** — `feat(observability): Prometheus and Grafana for the cluster's metrics`

## Phase D — sampling and alerts

### Task 11: Head sampling in the app

**Files:** `ObservabilityOptions.cs` (`OtlpOptions.TraceSampleRatio = 1.0`), both hosts (`SetSampler`), tests.

```csharp
tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.Otlp.TraceSampleRatio)));
```

Validated `0 < ratio <= 1` at startup. Logs are never sampled — they carry the trace id either way, so a
trace link still finds the request's logs. Test: a host with ratio `1.0` still produces a W3C
`traceId` (existing test) and the helper rejects `0`, `-1`, `1.5`.

- [ ] **Commit** — `feat(observability): configurable head sampling`

### Task 12: Tail sampling and alert rules

- `otel-collector.yaml`: `tail_sampling` in the traces pipeline — `decision_wait: 10s`; policies: status
  `ERROR`, latency `threshold_ms: 1000`, probabilistic `sampling_percentage: 20`. Single collector
  replica is a requirement (a trace's spans must meet in one collector) — say so in a comment.
- `prometheus.yaml`: `rule_files` + a rules ConfigMap: `ApiHighServerErrorRate` (5xx > 5% for 5m),
  `ApiSlowRequests` (p95 > 1s for 10m), `MessagesDeadLettered` (`increase(wolverine dead-letter counter[15m]) > 0`),
  `BrokerBacklog` (ready messages > 1000 for 10m), `DatabasePoolSaturated` (used/max > 0.8 for 5m) —
  metric names from Task 10's verified list.
- [ ] **Verify:** `promtool check rules` (in the Prometheus pod), rules visible at `/api/v1/rules`;
  force a 5xx burst (`/api/test/throw/unexpected` is test-only — use an e2e-style bad request loop or a
  temporarily failing dependency) and watch `ApiHighServerErrorRate` go pending → firing; confirm a
  trace for a slow/error request survives tail sampling while most fast ones do not.
- [ ] **Commit** — `feat(observability): tail sampling and alert rules on the cluster`

### Task 13: Documentation, full verification, review

- `observability` skill: trace links, template key and verified URLs, metrics (what, where, the gate),
  sampling, headers-are-secrets. `kubernetes` skill: the component's new members and ports.
  Root `CLAUDE.md` skill table line for `observability` if its trigger list changes.
- `/verify` (both stacks, codegen + contract diffs). `dotnet-reviewer` + `react-reviewer` on the branch.
- Tear the cluster down (`./deploy/teardown.ps1`) and stop dev containers.
- [ ] **Commit** — `docs(observability)` is a different type: fold these doc edits into the phase commits
  they describe instead, so the branch stays `feat` only.
