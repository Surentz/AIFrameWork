# Api

HTTP boundary and composition root.

## Belongs here

Controllers, request/response DTOs, the SignalR hub, the global `IExceptionHandler`, middleware,
and DI wiring. FluentValidation validators are not here — they validate commands, in
`Application`.

`Program.cs` also registers what only this host can provide, and that must never move into
`AddInfrastructure`: `ICurrentUser` and `IClientContext` (bound to `HttpContext`; the worker has
its own), `INotificationPush` (SignalR), and the outbox pumps via `AddOutboxPumps()` — delivered
only here because only this host can push (ADR 0028). `ApiFactory` refuses to build a host that
lost that call.

It also calls `AddExternalSystems(configuration.GetSection("ExternalSystems"))` itself rather than
through `AddInfrastructure`, because the set of named clients must be known at registration time,
and maps `/health/ready` with the `ExternalSystemHealth.IsNotExternal` predicate, so a partner's
outage never fails this pod's readiness. `ReadinessTests` guards both (ADR 0031).

## Rules

- **Controllers are thin.** Bind, delegate to an Application handler, map the
  `Result` to a status code. No business logic, no EF queries, no `if` chains
  over domain state.
- **DTOs use `required` + `init`**, and are separate types from Domain entities.
  Never return an entity directly.
- `[Required]` and other DataAnnotations are legitimate here for genuine HTTP-shape
  concerns (e.g. binding). They must **not** duplicate a business rule a FluentValidation
  validator already owns: `[ApiController]` runs ModelState validation before the action
  and auto-returns 400, so a duplicated annotation silently pre-empts the validation
  behavior and returns a differently-shaped response than `result.Problem()` would.
- **`Infrastructure` may only be referenced for DI registration** in the composition
  root. A controller reaching into a repository is a violation. The hook cannot tell the two
  apart; `ArchitectureTests` can — it reads the IL with ArchUnitNET and fails on any type outside
  `CompositionTypes` (`Program`, `ObservabilityRegistration`, and the generated adapters) that
  depends on an Infrastructure type. A new composition file joins that list deliberately, with a
  reason; a controller never does.
- **Authorize by capability, never by role.** A privileged action takes a policy from
  `Auth/AuthorizationPolicies.cs` (`Orders.Fulfil`, `Catalogue.Manage`, `Monitoring.Read`, …),
  never `[Authorize(Roles = "Admin")]`. A new one is listed in `AuthorizationPolicies.All` or
  `AuthorizationPolicyTests` fails. The `auth` skill and ADRs 0024/0025 have the reasoning.

## Exception handling

Two separate paths reach 400/404/409 — do not confuse them.

`GlobalExceptionHandler` (`IExceptionHandler`) maps *thrown* exceptions to RFC 9457
`ProblemDetails`:

| Exception | Status |
|---|---|
| `DomainException` | 400 |
| anything else | 500, logged, message not leaked |

`IExceptionHandler.TryHandleAsync` receives the exception as a parameter, so this handler needs
no `catch (Exception)` of its own — and could not have one without a suppression. CA1031 is
`error` globally in `.editorconfig`, with no exemption for this file.

Expected failures never throw. A handler returns a failed `Result`, and `ResultExtensions.Problem`
maps `Result.Error.Kind` to a status code:

| `ErrorKind` | Status |
|---|---|
| `Validation` | 400 |
| `Unauthorized` | 401 |
| `NotFound` | 404 |
| `Conflict` | 409 |
| `Unavailable` | 503, with `Retry-After` |
| anything else | 500 |

`Unavailable` is a dependency that stayed down after its own retry budget (ADR 0014) — an
anticipated failure, so never a 500.

## Tests

`tests/Api.IntegrationTests`, via `WebApplicationFactory<Program>`. Assert status
codes, `ProblemDetails` shape, and validation responses.
