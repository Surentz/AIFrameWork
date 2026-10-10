---
name: external-systems
description: Use when adding or changing an outbound integration with an external system - Refit clients, OCES3/mTLS client certificates, OAuth 2.0 client credentials against Keycloak (client secret or private_key_jwt), per-system retry, the probe/certificate/token health checks, outbound traffic, ExternalSystems__Systems__* configuration, and the throwaway dev PKI.
---

# External systems

`src/Infrastructure/ExternalSystems/` holds the plumbing; a partner gets its own folder beside it
only when it has a real call. ADR 0031 has the reasoning.

## Adding a partner

Run `/external-system <Name>`: it walks the port, the Refit client, the adapter, the registration,
the configuration keys and the tests, in that order. `ExternalSystems/StatisticsDenmark/` (the pilot:
Statistics Denmark's StatBank, behind `IPopulationStatistics`) is the worked example, its tests
`StatBankAdapterTests`. The four rules it rests on:

1. Configure it under `ExternalSystems:Systems:<Name>` (env: `ExternalSystems__Systems__<Name>__BaseAddress`),
   identically for the API and the worker. Secrets are FILE PATHS: `ClientCertificate:Path`,
   `ClientCertificate:PasswordFile`, `Auth:ClientSecretFile`, `ServerTrust:CaBundlePath`. There is no option
   that holds a secret's value — do not add one.
2. In `src/Infrastructure/ExternalSystems/<Name>/`: an `internal` Refit interface whose methods return
   `IApiResponse<T>` (never `Task<T>`, which throws `ApiException`), the partner's DTOs, and an adapter
   implementing the Application port. **Every adapter call goes through `ExternalSystemCall.SendAsync`**, which
   turns the FINAL outcome into `Result<T>`: the rejections the adapter names (status, and body when the partner
   keeps its meaning there) become its errors, everything
   else — a refused connection, a timeout, a 5xx after retries, a 401/403, a body not in the partner's shape —
   is `Unavailable` (`external_system.unavailable`), and the caller's cancellation propagates. Refit 16
   reports a failed send inside the response (`IsReceived` false), except the caller's cancellation, which it
   rethrows; `ExternalSystemCallTests` pins each case. Never hand-write that mapping again.
3. Register it in `ExternalSystemPartners.Add`, one chain per partner:
   `builder.AddClient<IPartnerApi>("<Name>").WithAdapter<IPort, PartnerAdapter>()`. `AddExternalSystems`
   calls it, so both hosts get it and neither `Program.cs` names a partner type. `AddClient` uses the
   source-generated `AddRefitGeneratedClient<T>()`; one interface per system (a second `AddClient` with the
   same type throws).
4. A non-GET call runs only in a worker job and carries an idempotency key that is a field on the job's
   message, minted once at enqueue, so every redelivery and retry sends the same one. If the partner takes
   none: `.WithoutRetry("why")` (the reason is required, and logged once when the host starts: the standard
   handler's options are `ValidateOnStart`, and building them runs our configuration).

## Things that will cost you an afternoon

- **`/health/ready` must keep its predicate** (`ExternalSystemHealth.IsNotExternal`) in BOTH hosts. Without it,
  one partner outage pulls every pod. `ReadinessTests` in both integration projects guard it.
- **Never a certificate validation callback.** Server trust is `ServerTrust:CaBundlePath` →
  `CertificateChainPolicy` with `CustomRootTrust`. `NoAcceptAnyCertificateTests` scans `src/` for callbacks.
- **The PFX must contain the intermediates.** Linux sends only what the PFX holds; Windows can fill gaps from
  its store, so a Windows green proves nothing about the chain. CI is the evidence.
- **private_key_jwt audience is the IdP's ISSUER**, not its token endpoint (`Auth:Issuer`). The JWT `kid` is
  derived the way Keycloak derives it (base64url SHA-256 of the SubjectPublicKeyInfo); an IdP that matches by
  an assigned kid would need an override that does not exist yet. After rotating a certificate, update the
  one registered at the IdP.
- **The 401 resend is ours, not Duende's.** `AddTokenResend` is a zero-delay `token-resend` pipeline that calls
  `SetForceRenewal`; Duende's `AddDefaultAccessTokenResiliency` is deliberately not used (its jittered delay
  sits inside the attempt timeout). It runs even for `WithoutRetry` clients, and a 401 plus a good resend
  counts as ONE `OutboundAttempt`. It ignores `Retry-After`, and the standard retry clears the force-renewal
  flag before each later attempt, so one call fetches at most one fresh token per 401.
- **An IdP outage can look like a partner 401.** When token acquisition fails, Duende 4.2's
  `AccessTokenRequestHandler` logs a warning and sends the request anyway: with no `Authorization` header, or,
  on the resend, still carrying the token the partner just rejected. The partner answers 401, the resend
  fails the same way, and the adapter sees a final 401. `ExternalSystemCall` maps a 401 that survived the resend to `Unavailable`,
  never to a business error; the `<Name>:token` health entry says which side is down.
- **The circuit breaker is per Refit interface, not per system.** Two interfaces for one system are two
  named clients with two breakers, and one can be open while the other keeps calling.
- **`Probe:Path` is relative: no leading `/`.** `/health` against `https://partner/api/` probes
  `https://partner/health`, discarding the base path; `health` probes `https://partner/api/health`.
- **AddClient's name is matched case-insensitively** and replaced by the configuration's own spelling, so
  `PARTNERSIM` from an env var and `AddClient("PartnerSim")` are one system with one token client.
- **A long `AttemptTimeout` widens the breaker's sampling window** to twice the attempt (the standard
  handler's own validation requires it, against a 30 s default).
- **Names come only from `ExternalSystemNames`.** Never compose a client or check name by hand.
- **A missing certificate does not stop the host.** That system fails fast (`FailFastHandler`) and reports
  Unhealthy; check the `<Name>:certificate` health entry (and `/api/monitoring/external-systems`, which shows it, ADR 0032).
  The failure is retried before it surfaces, except for a system that is not configured.
- **Client-secret rotation needs a restart** (Duende reads the secret once); VSO's `rolloutRestartTargets`
  provides it. Certificate rotation does not — the provider re-reads the file every two minutes.
- **Outbound traffic is not on the traffic page's totals.** `TrafficReader` counts inbound kinds only.

## Monitoring (ADR 0032)

- **Status table.** `external_system_status` holds one row per configured system: worst status,
  description, `checked_at` (UTC), `certificate_not_after`, `token_ok`. The worker's
  `ExternalSystemStatusPublisher` (an `IHealthCheckPublisher`, registered by
  `AddExternalSystemStatusPublisher`, **never in the API**) rewrites it every
  `Monitoring:ExternalSystemStatusPeriod` (default 1 minute; 5 s delay, 30 s timeout, `external`
  checks only): last-write-wins upsert, plus delete of systems no longer configured. It is not a
  Quartz job, so it never appears on the Jobs page.
- **Stale after 3 minutes.** An older row means the worker is not running and reads as stale, never
  as its last status. A check that threw is described as `check failed: <ExceptionType>`, never by
  its message, because descriptions reach the browser.
- **Page.** `GET /api/monitoring/external-systems` (`Monitoring.Read`) joins the rows with the last
  hour of outbound traffic; `/monitoring/integrations` and the strip on `/monitoring` refresh every
  30 s. Opening it never probes a partner.
- **Metrics and alerts.** Meter `AiFramework.ExternalSystems`: `aiframework.external_system.calls`
  and `aiframework.external_system.certificate.time_remaining`. Alerts `ExternalSystemFailing` and
  `ExternalSystemCertificateExpiring` (see the `kubernetes` skill).
- **The framework logs every Unhealthy check at Error.** The worker's `appsettings.json` sets the
  `Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService` category to `Critical`;
  as a side effect the worker's own `/health/ready` failures are not logged (the 503 still counts).
- **Seeing it locally.** `./scripts/dev.ps1 -WithPartners` runs the simulator (55690/55691) and
  points the worker at it. The e2e run's simulator is on 55692/55693 and its certificates are kept
  in `frontend/e2e/.certs/` between local runs.

## Local development

`./scripts/new-dev-certs.ps1` writes a throwaway PKI to `.certs/` (git-ignored). Tests generate their own
in memory (`TestPki`) and run a real Keycloak via Testcontainers (`KeycloakFixture`), so `dotnet test` needs Docker.
On Windows, parallel test runs can leave a few test intermediates in `Cert:\CurrentUser\CA` (subject
`AiFramework ... Intermediate CA`); if chain building starts failing with "unknown chain building error",
delete those.
