---
name: external-systems
description: Use when adding or changing an outbound integration with an external system - Refit clients, OCES3/mTLS client certificates, OAuth 2.0 client credentials against Keycloak (client secret or private_key_jwt), per-system retry, the probe/certificate/token health checks, outbound traffic, ExternalSystems__Systems__* configuration, and the throwaway dev PKI.
---

# External systems

`src/Infrastructure/ExternalSystems/` holds the plumbing; a partner gets its own folder beside it
only when it has a real call. ADR 0031 has the reasoning.

## Adding a partner

1. Configure it under `ExternalSystems:Systems:<Name>` (env: `ExternalSystems__Systems__<Name>__BaseAddress`).
   Secrets are FILE PATHS: `ClientCertificate:Path`, `ClientCertificate:PasswordFile`, `Auth:ClientSecretFile`,
   `ServerTrust:CaBundlePath`. There is no option that holds a secret's value — do not add one.
2. In `src/Infrastructure/ExternalSystems/<Name>/`: an `internal` Refit interface whose methods return
   `IApiResponse<T>` (never `Task<T>`, which throws `ApiException`), the partner's DTOs, and an adapter
   implementing the Application port. The adapter converts the FINAL outcome to `Result<T>` exactly as
   `ExchangeRateClient` does: `HttpRequestException`, `TimeoutRejectedException`, `BrokenCircuitException` →
   `ErrorKind.Unavailable`; a caller's cancellation propagates. Refit 16's `StatusCode` is nullable and
   `Error` is `ApiExceptionBase` (`Content` lives on `ApiException`), so check `IsReceived` first.
3. Register inside `AddExternalSystems`: `builder.AddClient<IPartnerApi>("<Name>").WithAdapter<IPort, PartnerAdapter>()`.
   `AddClient` uses the source-generated `AddRefitGeneratedClient<T>()`; one interface per system
   (a second `AddClient` with the same type throws).
4. A non-GET call runs only in a worker job and carries an idempotency key from the job's message id. If the
   partner takes none: `.WithoutRetry("why")` (the reason is required and logged).

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
  counts as ONE `OutboundAttempt`.
- **Names come only from `ExternalSystemNames`.** Never compose a client or check name by hand.
- **A missing certificate does not stop the host.** That system fails fast (`FailFastHandler`) and reports
  Unhealthy; check the `<Name>:certificate` health entry (and `/api/monitoring/external-systems` once PR 3
  lands). The failure is retried before it surfaces, except for a system that is not configured.
- **Client-secret rotation needs a restart** (Duende reads the secret once); VSO's `rolloutRestartTargets`
  provides it. Certificate rotation does not — the provider re-reads the file every two minutes.
- **Outbound traffic is not on the traffic page's totals.** `TrafficReader` counts inbound kinds only.

## Local development

`./scripts/new-dev-certs.ps1` writes a throwaway PKI to `.certs/` (git-ignored). Tests generate their own
in memory (`TestPki`) and run a real Keycloak via Testcontainers (`KeycloakFixture`), so `dotnet test` needs Docker.
On Windows, parallel test runs can leave a few test intermediates in `Cert:\CurrentUser\CA` (subject
`AiFramework ... Intermediate CA`); if chain building starts failing with "unknown chain building error",
delete those.
