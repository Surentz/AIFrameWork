---
description: Add an external system (partner) integration - Application port, Refit client, adapter, registration, configuration and tests
argument-hint: <Name>
---

Add the external system `$ARGUMENTS`.

Load the `external-systems` skill before starting, and the `jobs` skill if any call writes to the
partner. ADR 0031 is the reasoning behind all of it. Monitoring needs nothing from you: the status
table, the `/monitoring/integrations` page, the call metric and both alerts pick a system up from
its configuration (ADR 0032).

## 0. Settle these first, and say them, or stop

- **The partner's contract.** The endpoints you need, their request and response shapes, and the
  error statuses the partner documents. Without one, stop and ask: an invented contract becomes an
  adapter that maps the wrong things confidently.
- **The name.** 1–64 letters, digits or dashes, never `external`. It becomes the configuration key,
  the health checks, the traffic rows, the metric's `system` tag and the token client, so choose
  it once. Below it is `<Name>`.
- **How we authenticate.** `Auth:Kind` is `None`, `ClientSecret` or `PrivateKeyJwt` (OAuth 2.0
  client credentials against Keycloak); a client certificate (OCES3) or none; the partner's server
  certificate trusted by the default store or by its own CA bundle.
- **Each call is a read or a write.** A read runs on the request path through the port. A write
  runs as a worker job (`/job`) and carries an idempotency key that survives redelivery, or, if the
  partner takes none, the client is registered `.WithoutRetry("<why>")`.

## 1. The port — `src/Application/<Feature>/`

An interface in the feature folder that needs it, beside its commands and queries, in the use
case's own terms: its own records, never the partner's DTOs, and no `HttpClient`, Refit or Polly
type (the dependency-rule hook blocks them). Every method returns `Result<T>` and takes a
`CancellationToken`; say in its doc comment which failures it returns. `Unavailable` is always one
of them. `src/Application/Rates/IExchangeRateProvider.cs` is the shape to copy.

## 2. The client and adapter — `src/Infrastructure/ExternalSystems/<Name>/`

Three files, all `internal`:

```csharp
// I<Name>Api.cs — the partner's endpoints, nothing else.
internal interface I<Name>Api
{
    [Get("/things/{id}")]
    public Task<IApiResponse<<Name>Thing>> GetThingAsync(string id, CancellationToken cancellationToken);

    [Post("/things")]
    public Task<IApiResponse> CreateThingAsync(
        [Body] <Name>NewThing thing, [Header("Idempotency-Key")] string idempotencyKey, CancellationToken cancellationToken);
}

// <Name>Dtos.cs — the partner's shapes, exactly as it sends them.
internal sealed record <Name>Thing([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("name")] string Name);

// <Name>Adapter.cs — the port, through ExternalSystemCall.
internal sealed class <Name>Adapter(I<Name>Api api, ILogger<<Name>Adapter> logger) : I<Port>
{
    public const string SystemName = "<Name>";

    public Task<Result<Thing>> GetThingAsync(string id, CancellationToken cancellationToken) =>
        ExternalSystemCall.SendAsync(
            SystemName,
            logger,
            token => api.GetThingAsync(id, token),
            body => Result.Success(new Thing(body.Id, body.Name)),
            status => status == HttpStatusCode.NotFound
                ? new Error(ErrorKind.NotFound, "<feature>.thing_not_found", $"No thing '{id}'.")
                : null,
            cancellationToken);
}
```

- **Every Refit method returns `Task<IApiResponse<T>>`**, or `Task<IApiResponse>` when success has
  no body, never `Task<T>`, which throws instead of answering.
- **Paths start with `/` and are relative to the base address, path included.** A base address of
  `https://partner/api/v1/` and `[Get("/things")]` call `https://partner/api/v1/things`. (This is
  Refit; the probe's `Probe:Path` is the opposite: no leading `/`.)
- **The adapter goes through `ExternalSystemCall.SendAsync`, always.** It turns the final outcome
  into a `Result`: the statuses you name in `expected` become your errors; everything else (a
  refused connection, a timeout, a 5xx after retries, a 401/403, a body that is not the partner's
  shape) becomes `Unavailable` with code `external_system.unavailable`, and the caller's
  cancellation propagates. It logs the reason (a type or a status, never the body or URI), so pass
  the adapter's own `ILogger<T>`. Do not catch anything around it.
- **Expected statuses are the partner's documented answers about this request** (404, 409, 422).
  `ExternalSystemCall` ignores you for 401/403, 408, 429 and every 5xx: those are always `Unavailable`.
- **The DTOs are the contract, and they are enforced.** Partner clients deserialize with
  `RespectNullableAnnotations` and `RespectRequiredConstructorParameters`, so a non-nullable field
  the partner omits or sends as null makes the answer `Unavailable` (logged at Warning), not a
  record holding a null. Make a field nullable only when the partner documents it as optional.
- **Map in the adapter, not in the port.** The partner's DTOs stop here.

## 3. Register it — `src/Infrastructure/ExternalSystems/ExternalSystemPartners.cs`

One chain in `Add`, in name order:

```csharp
builder.AddClient<I<Name>Api>(<Name>Adapter.SystemName).WithAdapter<I<Port>, <Name>Adapter>();
```

Add `.WithoutRetry("<why>")` if a write has no idempotency key. Nothing goes in either host's
`Program.cs`: `AddExternalSystems` calls this, so both hosts get the partner, and an environment
without its configuration fails its calls fast as `Unavailable` instead of refusing to start.

## 4. Configuration — never in `appsettings.json`

`ExternalSystems:Systems:<Name>:…`, from environment variables (`ExternalSystems__Systems__<Name>__BaseAddress`)
or user-secrets in development. **The API and the worker need the same values**: both host every
client. Secrets are file paths; there is no key that holds a secret's value.

| Key | Default | When |
|---|---|---|
| `BaseAddress` | — (required) | Always. Include the partner's base path. |
| `Probe:Path`, `Probe:Method`, `Probe:Timeout` | `""`, `GET`, 5 s | A cheap endpoint for the health probe. **No leading `/`.** Any answer below 500, a 401 included, is reachable; a 5xx is Unhealthy. |
| `ClientCertificate:Path`, `ClientCertificate:PasswordFile` | — | mTLS / OCES3. The PFX must hold the intermediates. |
| `ServerTrust:CaBundlePath`, `ServerTrust:CheckRevocation` | —, `true` | The partner's own CA; omit to use the default store. |
| `Auth:Kind` | `None` | `ClientSecret` or `PrivateKeyJwt`. |
| `Auth:TokenEndpoint`, `Auth:ClientId`, `Auth:Scope` | — | Any `Auth:Kind` but `None`. |
| `Auth:ClientSecretFile` | — | `ClientSecret`. |
| `Auth:CredentialStyle` | `AuthorizationHeader` | `ClientSecret`: `PostBody` when the IdP wants the secret in the form body. |
| `Auth:Issuer` | — | `PrivateKeyJwt`: the JWT audience is the issuer, not the token endpoint. Needs `ClientCertificate`. |
| `Resilience:TotalRequestTimeout`, `AttemptTimeout`, `MaxRetryAttempts`, `BaseDelay` | 10 s, 3 s, 2, 500 ms | Per partner; keep the total above attempts × attempt timeout. |
| `CertificateExpiryWarning` | 30 days | When the certificate check turns Degraded. |

`ExternalSystemsOptionsValidator` refuses to start on a missing required value, with the system's
name in the message.

## 5. Tests

| Project | Covers |
|---|---|
| `tests/Application.Tests` | The use case, the port substituted with NSubstitute, including what it does with `Unavailable` |
| `tests/Infrastructure.Tests/ExternalSystems/<Name>/<Name>AdapterTests.cs` | The adapter, through the real chain against the simulator serving the partner's routes |

The adapter tests start `PartnerSimulatorApp` with `Endpoints` mapping the partner's routes, write
a client PFX and the CA from a `TestPki`, call `AddExternalSystems(...)` with this system's
configuration (it registers the partner itself; do not `AddClient` again), and resolve the port.
`ExternalSystemCallTests.cs` is the template to copy, except that it calls `AddClient` itself: its
`IThingsApi` is a test-only interface that no partner list registers. Per call, cover:

- the success, mapped into the port's records, with the partner's real JSON field names;
- each status you named in `expected`;
- one undocumented failure (a 500) becoming `Unavailable`;
- for a write, that the idempotency key arrives in the header and is the same on a retry.

Do not re-test what `ExternalSystemCallTests` already pins (timeouts, refused connections, 401,
unreadable bodies, cancellation), nor tokens (`ExternalSystemClientTests` runs a real Keycloak):
adapter tests run with `Auth:Kind` `None`.

## 6. Writes — `/job`

A write runs in a job handler in `src/Application/<Feature>/`, which calls the port. Its
idempotency key is a field on the job message, minted once when the job is enqueued (the
aggregate's id plus the operation, or a fresh `Guid`), so every redelivery and every retry sends
the same key. A handler that receives `Unavailable` throws, so Wolverine's retry and dead-letter
policy applies. Any other failure is the partner refusing this request: decide whether it is
recorded or dead-lettered, and say which.

## 7. Seeing it run

`./scripts/dev.ps1 -WithPartners` points the worker at the simulator, which serves only `/ping`
and `/echo`, so it shows the plumbing (certificate, health, the page), not your adapter. Against
the real partner, configure the system in user-secrets and start the dev loop as usual.

## 8. Finish

Run `/verify`, then dispatch the `dotnet-reviewer` agent over the diff. If any step is blocked (no
contract, an unclear auth method, a write with neither an idempotency key nor a reason to skip
retry), stop and say so rather than inventing a shape for the rest.
