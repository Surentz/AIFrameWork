# 0031. Outbound integrations with external systems

**Date:** 2026-10-08
**Status:** Accepted

## Context

The application will call external systems over HTTP: Danish public-sector APIs, where OCES3
certificates (mutual TLS) and OIO-profiled OAuth apply, and the organisation's own internal services
behind Keycloak. Until now there was one outbound client, the exchange-rate lookup, with no client
certificate, no token and no per-partner configuration. Every new partner would otherwise rebuild
the same plumbing, and differently.

`egdw_eghealth`'s `EGHealth.Integrations` is the prior art. Its fluent builder, shared service name,
logical-versus-attempt traffic counting and probe semantics (any status below 500 means the host
answered) are taken. Its committed certificates, in-app Vault calls, cache-forever certificate,
sync-over-async handler factory, Polly v7 policies and `catch (Exception)` are not.

## Decision

**Typed clients are Refit 16 interfaces returning `IApiResponse<T>`**, registered with the
source-generated `AddRefitGeneratedClient<T>()`. `AddRefitClient<T>()` is the reflection client in
Refit 16 and throws at resolve time without the separate `Refit.Reflection` package. Methods return
`IApiResponse<T>`, never `Task<T>`, which throws `ApiException`; the adapter converts the final
outcome to `Result<T>` (ADR 0014).

**Tokens come from Duende.AccessTokenManagement 4.2.0** (Apache 2.0; the client library, not
IdentityServer) for OAuth 2.0 client credentials against Keycloak. `private_key_jwt`, signed with
the system's client certificate, is preferred over a shared client secret. The assertion's audience
is the IdP's **issuer**, not its token endpoint, and it lives 60 seconds.

**Every client is built by `ExternalSystemsBuilder.AddClient<TApi>`, which fixes the handler chain,
outermost first:** Outbound traffic, the standard resilience handler, OutboundAttempt traffic, the
token-resend pipeline, Duende's token handler, and the certificate-presenting primary handler.
Outbound traffic is outermost so it records the logical call once, after every retry. Resilience
sits below it so one budget covers all attempts (ADR 0014). OutboundAttempt traffic sits inside the
retry so each try is counted. Token handling sits below the attempt counter so a refreshed token is
part of the attempt that needed it. The primary handler is last because it is the TLS connection.

**Options are per system**, under `ExternalSystems:Systems:<Name>`, and a secret is always a file
path (`ClientCertificate:Path`, `ClientCertificate:PasswordFile`, `Auth:ClientSecretFile`,
`ServerTrust:CaBundlePath`); no option holds a secret's value. Server trust is
`CertificateChainPolicy` with `CustomRootTrust`; there is no certificate validation callback
anywhere under `src/`, and a test scans for one.

**The probe carries the certificate but no token.** It proves the host answers and accepts our
certificate without spending a token request. Three health checks per system (probe, certificate,
token) are tagged `external` and **excluded from `/health/ready`** in both hosts by
`ExternalSystemHealth.IsNotExternal`, so a partner outage never pulls a pod out of rotation.

**Outbound traffic is recorded as `Outbound` (the logical call) and `OutboundAttempt` (each try),
and the traffic page's totals exclude both.** `TrafficReader` counts inbound kinds only.

**The application is Vault-unaware.** Vault Secrets Operator (VSO) syncs KV into a Kubernetes
Secret that is mounted as files; the application reads files and nothing else.

## The VSO contract

One Kubernetes Secret per system, mounted read-only at
`/var/run/secrets/external-systems/<system>/`, with the keys `client.pfx`, `client.pass`, and
optionally `client-secret` and `ca.pem`. The VSO resource's `rolloutRestartTargets` names the api
and worker Deployments. Configuration points `ClientCertificate:Path` and its siblings at those
files. The PFX must contain the intermediates: Linux sends only what the PFX holds.

## Probe outcomes

1. **The 401 resend.** Duende's `AddDefaultAccessTokenResiliency()` does resend once with a forced
   renewal, but its retry inherits a 2 s jittered default delay that sits inside the 3 s attempt
   timeout, and nothing outside rescues a timed-out attempt when the standard retry is off. We
   replaced it with our own zero-delay `token-resend` pipeline (one retry on a 401, calling Duende's
   public `SetForceRenewal` on the request) in the same chain position. Verified: exactly one resend
   with a fresh token, also for `WithoutRetry` clients. This drops Duende's DPoP-nonce retry; no
   system here uses DPoP.
2. **Linux sends the full chain.** Verified by CI's Linux `backend (Debug)` and
   `backend (Release)` jobs on PR #107 (commit `0a090ab`): `Send_WithALeafIssuedByTheIntermediate_Succeeds`
   passes against a server that trusts only the root, and `Ping_WithALeafSentWithoutItsIntermediate_FailsTheHandshake`
   is rejected. A Windows pass proves nothing, because Windows fills gaps from its certificate store.
   That bit the first CI run in the other direction: the simulator's *server* leaf was issued by the
   intermediate and sent without it, so every handshake failed on Linux with `PartialChain` while
   Windows found the intermediate in `CurrentUser\CA`. The simulator's server certificate is now
   issued by the root. Sending the intermediate instead would have let a Windows server complete a
   chainless client chain from its own context and hollowed out the test above. A real partner
   that omits its intermediates fails the same way, and is the partner's configuration to fix.
3. **Keycloak and `private_key_jwt`.** Keycloak 26.4 accepts the assertion with the audience set to
   the realm issuer, once the JWT `kid` equals base64url(SHA-256(SubjectPublicKeyInfo DER)). That is
   Keycloak's `KeyUtils.createKeyId` for a certificate registered on the client; the default SHA-1
   thumbprint `kid` is answered with `invalid_client` ("Unable to load public key"). An operator can
   alternatively pin a kid with Keycloak's `jwt.credential.kid` client attribute.
4. **The worker records traffic.** `AddMonitoring` runs in every host, so no worker change was needed.

## Consequences

- **`TrafficKind` gains `Outbound` and `OutboundAttempt`**, a contract change: API consumers see the
  new enum values.
- **The `kid` is fixed to Keycloak's derivation.** An IdP that looks keys up by an operator-assigned
  kid would need an `Auth.KeyId` override, which is not built. A rotated certificate gets a new kid
  automatically, but the certificate registered at the IdP must be updated in step with it.
- **Client-secret rotation needs a restart**, because Duende reads the options once; VSO's
  `rolloutRestartTargets` provides it. Certificate rotation does not: the provider re-reads the file.
- **A `FailFastHandler` failure is retried before it surfaces.** A missing or expired certificate
  fails fast, but the standard handler retries it first. This is harmless inside the total timeout
  and delays the 503. A system that is not configured skips retry.
- **A 401 resend happens inside the `OutboundAttempt` counter**, so a 401 followed by a successful
  resend counts as one successful attempt.
- **Breakers are per pod.** One pod can open its breaker while another keeps calling.
- **Breakers are per Refit interface, not per system.** Two interfaces for one system get two
  breakers.
- **An IdP outage can surface as a partner 401.** When token acquisition fails, Duende 4.2 logs a
  warning and sends the request anyway, without a token (or, on the resend, with the rejected one).
  Adapters map a 401 that survived the resend to `Unavailable`, not to a business error.
- **`Resilience:Enabled=false` reaches every system**, disabling retry for all of them.
- **On Windows, parallel test runs can leave a few test intermediates in `CurrentUser\CA`.**
  `TestPki.Dispose` removes its own, but a store race leaks a handful per many runs. Linux CI is
  unaffected.
- **A missing certificate file does not stop the host.** The system reports Unhealthy instead.

## Alternatives considered

**Kiota or NSwag.** Generated clients are heavier than a small hand-declared interface and need a
trustworthy OpenAPI document per partner. Kiota remains permitted per partner when one publishes a
large one.

**A hand-written token handler (the egdw approach).** Caching, expiry, concurrent refresh and
client authentication are exactly what Duende already does and tests.

**The application reading Vault directly (VaultSharp).** Its last release targets .NET 8, and it
would tie the application to Vault's API and authentication.

**A Vault Agent sidecar.** One more container per pod and a second sync mechanism beside VSO.

**A separate `src/Integrations` project.** Not yet: the plumbing is a folder inside Infrastructure.
Revisit at four or more partners, or when a partner package brings dependencies Infrastructure
should not carry.
