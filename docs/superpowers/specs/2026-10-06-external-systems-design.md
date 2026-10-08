# Outbound integrations with external systems — design

**Date:** 2026-10-06
**Status:** Plumbing built (PR 2); monitoring (PR 3) pending. Approved 2026-10-06. Amended while planning PR 2: issuer as the assertion audience, a contract change for `TrafficKind`, an inbound-only traffic page, in-process simulator scripting, and when the partner-isolation rule arrives.
**Type:** this document is `docs(integrations)`; the build is two `feat` pull requests (§6).
**Builds on:** ADR 0014 (retry and resilience), ADR 0021/0027 (operational telemetry and
metrics), ADR 0016/0026 (jobs in the worker, RabbitMQ), all of which stay in force.

## Intent

The application will call external systems over HTTP: **Danish public-sector APIs** (where OCES3
certificates and OIO-profiled OAuth apply) and **the organisation's own internal services**
(OAuth 2.0 client credentials issued by **Keycloak**). Today the repository has exactly one
outbound client, `ExchangeRateClient`, with no authentication and no certificates.

This design builds the plumbing every such integration will share — mTLS with OCES3
certificates, OAuth 2.0 client credentials, per-system retry, health checks, traffic counting —
and proves it end to end against a **simulated partner**, before any real partner is wired in.

**Success means:**
- adding a partner is one fluent registration plus configuration, and gets mTLS, OAuth, retry,
  health and traffic without further code;
- a rotated certificate is used without a restart, and an expiring one is visible weeks ahead;
- a partner outage never takes an API or worker pod out of rotation;
- an administrator sees every external system's health and traffic on the Monitoring page;
- no certificate, private key, client secret or token is ever committed, logged, or sent to the
  browser.

### Decided in conversation

| Question | Decision |
|---|---|
| Which systems? | **Danish public sector + our own internal services**, all REST. No SOAP in scope. |
| First concrete integration | **A simulated partner** (`tests/PartnerSimulator`); real partners follow in their own PRs. |
| Frontend scope | **Status only**: an admin panel under Monitoring. Configuration and secrets never reach the UI. |
| Where calls run | **Both hosts, by rule**: reads on the API's request path; writes (non-GET) only as worker jobs, with an idempotency key. |
| Secret source | **HashiCorp Vault**, but the app is **Vault-unaware**: Vault Secrets Operator syncs KV → Kubernetes Secret → mounted files. Assumes the OCES3 key is stored exportable (PFX in KV v2). |
| Internal IdP | **Keycloak** — it issues tokens; our app is only an OAuth client of it. |
| Typed client | **Refit 16** (source-generated), returning `IApiResponse<T>`. Kiota is permitted per partner when it publishes a large, trustworthy OpenAPI document. |
| Token handling | **Duende.AccessTokenManagement 4.x** (Apache 2.0) — the client-side library, not Duende IdentityServer. |
| Structure | **Approach A**: folders inside `src/Infrastructure`, not a new project. The ADR names the trigger for splitting out later. |
| Inspiration | `egdw_eghealth`'s `EGHealth.Integrations`: its fluent builder, shared service name, logical/attempt traffic counting and probe semantics are adopted; its committed certificates, in-app Vault calls, cache-forever certificate, sync-over-async handler factory, Polly v7 policies and `catch (Exception)` are not. |

## 1. Backend structure

### Where things live

| Path | Contents |
|---|---|
| `src/Infrastructure/ExternalSystems/` | The shared plumbing: options, builder, certificate provider, assertion service, probe, health checks, traffic handler. |
| `src/Infrastructure/ExternalSystems/<Partner>/` | Per partner, **only once a real call exists**: the `internal` Refit interface, the partner's DTOs, and an adapter implementing an Application port. |
| `src/Application/<Feature>/I<Capability>.cs` | Ports stay in their feature's folder (as `Rates/IExchangeRateProvider.cs` does today), returning `Result<T>`. Application never sees Refit, Duende, or an HTTP type. |
| `src/Application/Monitoring/` | `GetExternalSystemStatus` query and its reader port (§3). |

The name is `ExternalSystems`, not `Integration`: `src/Infrastructure/Integration/` already
holds the RabbitMQ integration *events* (ADR 0026).

A new `ArchitectureTests` rule — no type under `ExternalSystems.<PartnerA>` depends on a type under
`ExternalSystems.<PartnerB>` — arrives **with the first real partner**: before then there is nothing
to scan, and a rule with nothing to scan passes vacuously.

### Registration

Each host's `Program.cs` calls `builder.Services.AddExternalSystems(builder.Configuration.GetSection("ExternalSystems"))`.
It is not inside `AddInfrastructure` because the **set of named clients must be known at
registration time** — the same reason the Worker reads `Jobs` straight off configuration. Both
hosts call it, so both get every client.

`AddExternalSystems` registers, for every configured system, the probe client and the three
health checks (§3). A partner with business calls adds its typed client in code:

```csharp
builder.AddClient<IPartnerApi>("Partner")   // builder = AddExternalSystems(...); name = config key = health name = traffic name
    .WithAdapter<IPartnerLookup, PartnerAdapter>();
```

`.WithoutRetry("reason")` disables retry for a client whose calls are not idempotent, by the
ADR 0014 mechanism (`ShouldHandle` rejects every outcome — never `MaxRetryAttempts = 0`). The
reason is mandatory and appears in the startup log.

### Handler chain (outermost first, fixed by the builder)

1. `OutboundTrafficHandler` (**Logical**) — one record per call, retries included in its
   duration; sees timeouts and open circuits as exceptions.
2. `AddStandardResilienceHandler()` — this system's own options and its own circuit breaker.
   Order inside it is the standard handler's and is not rearranged (ADR 0014).
3. `OutboundTrafficHandler` (**Attempt**) — one record per physical attempt.
4. Duende's client-credentials token handler — only when `Auth.Kind != None` — behind Duende's
   `AddDefaultAccessTokenResiliency()`. Each attempt reads the token from the cache; a 401 forces
   one refresh and one resend.
5. Primary handler: `SocketsHttpHandler` whose `SslOptions` present the system's certificate
   (§2) and validate the server against `ServerTrust` when configured.

A unit test builds the chain and reads it back, asserting this order. A name that is not configured
still registers: its primary handler fails fast as "not configured", which the adapter maps to
`Unavailable`, so an environment without a partner degrades one feature instead of refusing to start.

### Configuration

```json
"ExternalSystems": {
  "Systems": {
    "PartnerSimulator": {
      "BaseAddress": "https://localhost:55690/",
      "Probe": { "Method": "GET", "Path": "ping", "Timeout": "00:00:05" },
      "Resilience": { "TotalRequestTimeout": "00:00:10", "AttemptTimeout": "00:00:03", "MaxRetryAttempts": 2, "BaseDelay": "00:00:00.500" },
      "Auth": {
        "Kind": "PrivateKeyJwt",
        "TokenEndpoint": "http://localhost:55691/realms/aiframework/protocol/openid-connect/token",
        "Issuer": "http://localhost:55691/realms/aiframework",
        "ClientId": "aiframework-partner-sim",
        "Scope": "partner.read",
        "ClientSecretFile": null,
        "CredentialStyle": "PostBody"
      },
      "ClientCertificate": { "Path": ".certs/client.pfx", "PasswordFile": ".certs/client.pass" },
      "ServerTrust": { "CaBundlePath": ".certs/ca.pem" },
      "CertificateExpiryWarning": "30.00:00:00"
    }
  }
}
```

- **Secrets are always file paths, never values.** No option holds a secret's content, so no
  `appsettings*.json` can hold one.
- From the environment: `ExternalSystems__Systems__PartnerSimulator__BaseAddress` (double
  underscores).
- Validated with `ValidateOnStart`, like `ResilienceOptions`: `BaseAddress` absolute http(s);
  `AttemptTimeout <= TotalRequestTimeout`; `MaxRetryAttempts >= 1`; `TokenEndpoint` and
  `ClientId` required unless `Kind = None`; `ClientSecretFile` required iff `Kind = ClientSecret`;
  `ClientCertificate` and an absolute `Issuer` required iff `Kind = PrivateKeyJwt`.
- **Shape is validated at startup; files are not.** A missing certificate file is a runtime
  health failure for that one system (§2), never a host that refuses to start.
- The global `Resilience:Enabled=false` test switch applies to every system's retry as well.

The ports `55690` (simulator) and `55691` (Keycloak) are proposed for the dev profile, following
the repository's `556xx` convention; the e2e simulator takes `55692`.

### Adapters

An adapter calls its Refit interface, whose methods return `IApiResponse<T>` and never throw
`ApiException`, and converts the **final** outcome — after retry, breaker and both timeouts — to
`Result<T>`, exactly as `ExchangeRateClient` does: `HttpRequestException`,
`TimeoutRejectedException` and `BrokenCircuitException` become `ErrorKind.Unavailable`; a caller's
`OperationCanceledException` propagates. Nothing inside a Polly-wrapped delegate returns a failed
`Result` (`Infrastructure/CLAUDE.md`).

Writes: a non-GET call happens only inside a worker job (ADR 0016) and carries an idempotency key
derived from the job's message id, so a redelivered job and a retried attempt both reuse it. If
the partner does not support idempotency keys, the client is registered `.WithoutRetry(...)`.

## 2. Authentication and certificates

### OAuth 2.0

`Auth.Kind`:

| Kind | Client authentication at the token endpoint |
|---|---|
| `None` | No token. |
| `ClientSecret` | Secret read from `ClientSecretFile`; sent per `CredentialStyle` (header or body). |
| `PrivateKeyJwt` (preferred) | `PrivateKeyJwtAssertionService : IClientAssertionService` signs a short-lived JWT (`iss`=`sub`=client id, `aud`=`Auth.Issuer` — the authorization server's **issuer URL, not its token endpoint**, per Duende's guidance after CVE-2025-27370/27371 — `jti`, `iat`, `exp` ≤ 60 s) with the system's certificate. Keycloak's "Signed JWT" authenticator verifies it against the registered certificate. |

- When a system has a `ClientCertificate`, its **token endpoint client presents it too**. That
  covers Keycloak's X.509 client authenticator and RFC 8705 certificate-bound tokens without a
  fourth kind.
- The token cache is Duende's own, per process, keyed per system. It is unrelated to
  `ICacheable`, which keeps its own rules.
- Tokens, assertions, client secrets and certificate passwords are never logged. Subject,
  thumbprint and `NotAfter` may be.
- A partner with profile-specific requirements (OIO claims, a token exchange) gets its own handler
  in its own partner folder. The shared plumbing stays plain OAuth 2.0.

### Certificates

`ICertificateProvider.GetCurrent(systemName)` returns the system's certificate, its
`SslStreamCertificateContext` (built with the intermediates from the PFX, so Linux sends the full
chain), and its `NotAfter`.

- Loaded with `X509CertificateLoader.LoadPkcs12FromFile` (the obsolete `X509Certificate2`
  constructors raise SYSLIB0057, an error here).
- **Rotation by polling**, not file events: every 2 minutes the provider compares the file's
  last-write time and content hash. Kubernetes updates a mounted Secret by swapping a symlink,
  which `FileSystemWatcher` does not reliably report. `IHttpClientFactory` rebuilds primary
  handlers every 2 minutes by default, so a new certificate reaches new connections without a
  restart. VSO's `rolloutRestartTargets` remains the backstop.
- **Failure is per system.** A missing, unreadable or expired certificate leaves the host running;
  that system's health is Unhealthy and its calls fail fast as `Unavailable` (503). Within
  `CertificateExpiryWarning` of `NotAfter` it reports Degraded.
- **Server trust.** `ServerTrust.CaBundlePath` makes that one system validate its server against a
  custom root store (`X509ChainTrustMode.CustomRootTrust`). "Accept any certificate" does not
  exist; a test asserts no validation callback returns `true` unconditionally.

### Development and tests

- `scripts/new-dev-certs.ps1` creates a test CA, a client certificate and a partner server
  certificate in `.certs/` (git-ignored; the script adds the ignore entry if missing).
- Integration tests create their certificates in memory with `CertificateRequest`.
- No private key is ever committed, test keys included.

## 3. Health, traffic and monitoring

### Health checks

Per system, three checks, all tagged `external`, named `{System}`, `{System}:token`,
`{System}:certificate`:

1. **Probe** — a dedicated client carrying the **certificate but no token**, no retry, no traffic
   handler, its own short timeout. A 401 therefore proves DNS, TCP *and* the mTLS handshake.
   Reading, adopted from `egdw_eghealth`: any status below 500 is Healthy (the host answered —
   401/403/404/405 are the expected ones for a probe with no token); 5xx Unhealthy; own timeout
   Degraded; `HttpRequestException` Unhealthy with its `HttpRequestError` category only.
   Descriptions never contain response bodies or host names.
2. **Token** — can a token be obtained (from the cache when one is live). Absent for `Kind = None`.
3. **Certificate** — days until `NotAfter`. Absent when no certificate is configured.

**`/health/ready` must exclude them.** Today both hosts map `/health/ready` with no predicate, so
it would run every registered check — the first partner check would let a partner outage pull
every pod. Both hosts gain `Predicate = c => !c.Tags.Contains("external")`, asserted by a test in
each host.

### Who runs them

A worker job, scheduled by Quartz every minute, runs the `external`-tagged checks and upserts one
row per system into a new table **`external_system_status`**:

| Column | |
|---|---|
| `name` (PK) | System name |
| `status` | Healthy / Degraded / Unhealthy — the worst of its checks |
| `description` | From the worst check; bounded length |
| `checked_at` | UTC |
| `certificate_not_after` | Nullable |
| `token_ok` | Nullable |

Rows for systems no longer configured are deleted by the same job. The page treats a row older
than 3 minutes as **stale** (the worker is not running), rather than trusting it — failure by
staleness, as ADR 0029 does for exports. Opening the page never probes a partner.

### Traffic

`TrafficKind` gains `Outbound` (logical calls) and `OutboundAttempt` (physical attempts), with
`Name` = system name. `Kind` is stored as text (≤ 16 chars), so **no migration** is needed for
traffic. Outcomes: 2xx `Succeeded`; 4xx `Failed`; 5xx, timeout, open circuit `Faulted`. The
duration of an `Outbound` row includes retries.

Two consequences found while planning:
- `TrafficKind` is on the wire (`TrafficRowDto.Kind`), so its two new members **regenerate the API
  contract in PR 2** — an additive enum change, nothing else.
- The traffic page's overall rate, error rate and series sum every kind. `TrafficReader` is
  restricted to the inbound kinds (`Http`, `Command`, `Query`), or every request that calls a
  partner would be counted twice, and three times with attempts.

The worker already records traffic: `AddMonitoring`, which registers the recorder and its flush,
runs in every host.

### Metrics and alerts

The standard resilience handler's metrics are already emitted. Added: a gauge
`aiframework.external_system.certificate.days_remaining{system}`, and two rules in
`k8s/components/observability/prometheus-rules.yml` with promtool tests: certificate under 14 days;
more than 20% of a system's `Outbound` calls `Faulted` over 5 minutes, with at least 10 calls in
that window (so one failed call at night does not page anyone).

### API

`GET /api/monitoring/external-systems` on a new `MonitoringExternalSystemsController`, policy
`Monitoring.Read` on the controller (ADR 0020). It sends `GetExternalSystemStatus`, which joins the
status rows with the last hour of `Outbound`/`OutboundAttempt` traffic. Not `ICacheable`. The API
contract (`openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts`) is regenerated.

### Frontend (status only)

- **Monitoring overview:** a strip with one chip per system (worst status; stale shown as such).
- **`/monitoring/integrations`:** a table — status and description, last checked, certificate
  days remaining (highlighted under 30), token status, and calls / attempts / faulted / p95 over
  the last hour.
- TanStack Query with a 30-second refetch, typed from `schema.d.ts`, under `features/monitoring`.
- No secret, host name or response body is ever in a response the browser receives.

## 4. Simulated partner, dev loop, tests

### `tests/PartnerSimulator`

A small ASP.NET Core app, never deployed. Kestrel requires a client certificate issued by the
test CA; JWT bearer validation against a configured Keycloak realm. Endpoints: `GET /ping` (probe
target, no token required) and `GET /echo` (token required). In PR 2 it runs in-process and tests
script `/echo`'s next statuses directly (503s, 401s) and read back what it received; HTTP `/chaos/*`
endpoints are added in PR 3 only if the containerised e2e run needs them.

### Tests

| Where | What |
|---|---|
| `Infrastructure.Tests` (unit) | Handler order; options validation; assertion claims and signature; probe status mapping; no accept-all validation callback; `Resilience:Enabled=false` reaching every system; certificate provider load, reload on change, expiry. |
| `Infrastructure.Tests` (integration) | Simulator in-process on a random port with **real TLS** (`TestServer` bypasses TLS); Keycloak via Testcontainers with a realm import defining one `ClientSecret` and one `PrivateKeyJwt` client. mTLS success, and failure with no or wrong certificate; both token kinds; 401 → exactly one refresh; 503 retried with attempts > calls in traffic; rotated certificate picked up; expired certificate Unhealthy while another system stays Healthy. |
| `Api.IntegrationTests` | `/health/ready` excludes `external`; Monitoring endpoint 403 for a non-admin and its response shape. |
| `Worker.IntegrationTests` | `/health/ready` excludes `external`; the status job writes and prunes rows. |
| Frontend (Vitest + MSW) | The strip and the page, including stale and expiring states. |
| e2e (Playwright) | `docker-compose.e2e.yml` adds the simulator, mTLS only, no Keycloak (its start-up stays out of every run). Two systems configured — the simulator and an address nobody listens on — and the page shows one Healthy, one Unhealthy. |

Retry testing follows `ExchangeRateClientTests.AdvanceUntilCompleteAsync` (`FakeTimeProvider`,
real-delay loop bounded by real time).

### Dev loop

`scripts/new-dev-certs.ps1` as above. `dev.ps1 -WithPartners` starts the simulator and Keycloak
under a compose profile; a bare `dev.ps1` is unchanged, as with Seq and Redis.

### Kubernetes

The kind overlay gets a Secret created by `deploy.ps1` from `.certs/`, standing in for what VSO
would produce, mounted read-only into the API and worker pods. **VSO manifests are not written
now** — there is no Vault to test them against. ADR 0031 records the contract they must meet: one
Secret per system, mounted at `/var/run/secrets/external-systems/<system>/`, with keys
`client.pfx`, `client.pass`, optionally `client-secret` and `ca.pem`, and `rolloutRestartTargets`
naming the api and worker Deployments.

## 5. Probes to run before building on an assumption

Each is checked in the first task that depends on it, with the result written into the ADR:

1. Duende.AccessTokenManagement's handler refreshes and resends exactly once on a 401, and the
   token-endpoint client it uses can be given our primary handler (for mTLS at the token endpoint).
2. `SslStreamCertificateContext` built from the PFX's intermediates makes a Linux container send
   the full chain (run in the `backend` CI job, not on Windows).
3. Keycloak's "Signed JWT" authenticator accepts our assertion with the certificate registered
   (rather than a JWKS URL).
4. ~~Whether the worker records traffic today (§3).~~ Answered while planning: it does.

If any probe fails, the design section it supports is revisited before that task continues.

Outcomes (ADR 0031 has the full text):

1. **Done.** Duende's retry resends once with a forced renewal, but its inherited 2 s jittered delay sits inside the 3 s attempt timeout. We replaced it with our own zero-delay `token-resend` pipeline using Duende's public `SetForceRenewal`; exactly one resend with a fresh token, also for `WithoutRetry` clients. Duende's DPoP-nonce retry is dropped (no DPoP in use).
2. **Pending the PR's first Linux CI run** (`backend` job, `Send_WithALeafIssuedByTheIntermediate_Succeeds`).
3. **Done.** Keycloak 26.4 accepts `private_key_jwt` with the realm issuer as audience once the JWT `kid` is base64url(SHA-256(SubjectPublicKeyInfo DER)), Keycloak's `KeyUtils.createKeyId`; the default thumbprint `kid` fails with "Unable to load public key". Alternative: the client attribute `jwt.credential.kid`.
4. **Done.** The worker records traffic: `AddMonitoring` runs in every host.

## 6. Delivery

| PR | Title | Contents |
|---|---|---|
| 1 | `docs(integrations): design for outbound external systems` | This spec. |
| 2 | `feat(integrations): call external systems over mTLS and OAuth 2.0` | `ExternalSystems/` plumbing, Duende and Refit, per-system resilience, `Outbound`/`OutboundAttempt` traffic kinds, the three health checks, the `/health/ready` filter in both hosts, `tests/PartnerSimulator`, the Keycloak realm, unit and integration tests, `new-dev-certs.ps1`. ADR 0031. A new `external-systems` skill, a row in root `CLAUDE.md`'s skills table, a section in `Infrastructure/CLAUDE.md`. API contract regenerated for the two new `TrafficKind` members only; no migration. Plan: [`2026-10-06-external-systems-plumbing.md`](../plans/2026-10-06-external-systems-plumbing.md). |
| 3 | `feat(monitoring): show external system health and traffic` | Status job, `external_system_status` table (new migration), Monitoring endpoint, regenerated API contract and Wolverine adapters, the overview strip and `/monitoring/integrations`, the e2e case, alert rules with promtool tests, `dev.ps1 -WithPartners`. |

## Out of scope

- Moving `ExchangeRateClient` into `ExternalSystems/` — a later `refactor`.
- The first real partner — its own `feat`.
- VSO manifests — with the first environment that has a Vault.
- SOAP / WS-Security partners — a separate design when one appears.
- Any UI for editing configuration or uploading certificates.
- A separate `src/Integrations` project — ADR 0031 records the trigger (several partners, or
  partner packages whose dependencies Infrastructure should not carry).
