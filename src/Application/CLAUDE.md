# Application

Use cases. Orchestrates Domain objects; owns no infrastructure.

## Belongs here

Use-case handlers, `ICommand<T>` / `IQuery<T>` requests and their handlers, **ports**
(the interfaces Infrastructure implements — `IOrderRepository`, `IUnitOfWork`, `IClock`,
`IEmailSender`), request and response models, FluentValidation validators, and `Result<T>`.

## Messaging rules

- A type implements `ICommand<T>` / `IQuery<T>` **exactly once**. `TResponse` is inferred
  from the argument at the call site; a second implementation makes that ambiguous and
  breaks every caller. A test enforces this.
- **Handlers never call `SaveChangesAsync`.** The unit-of-work behavior commits exactly
  once after a successful command. A handler that commits turns one command into two
  transactions.
- Never inject a `DbContext`; depend on the port.
- `Result.Success(value)` infers `T` from the argument. `Result.Failure<T>(error)` needs
  the explicit type argument — an `Error` carries no type information to infer from.

## Caching

Two markers in `Abstractions/Caching.cs` declare intent; Infrastructure alone chooses the store.
**No caching package may be referenced from this layer** — that is the point of the markers.

- Opt a query in with `: IQuery<T>, ICacheable`. `CacheKey` is composed from the query's **own
  arguments only**, and `Duration` is a literal on the record — the query is what knows how stale
  its own result may be, and a record here has nothing to inject configuration through.
- **Never put a user id in `CacheKey`.** The caching behavior prepends the query type name and
  `ICurrentUser.Id`; adding one here duplicates it rather than securing anything. A cacheable
  query dispatched with no current user throws rather than sharing one entry across callers.
- `IInvalidatesCache.Tags` names query **types**, and uses `nameof(GetOrders)` rather than a string
  literal, so renaming a query is a compile error here instead of an eviction that quietly stops
  matching. The caller's id is prepended by the behavior, as with keys.
- Nothing on the auth path is cacheable. `GetUser` is the query to leave alone, and so is any
  future one reading account state: ADR 0008's lockout state served from a cache is a security bug,
  not a stale read. `SignIn`, `RegisterUser`, and `ChangePassword` are commands, so the question
  does not arise — the caching behavior wraps queries only.
- A handler reached through the cache may run with no ambient `HttpContext`. Depend on
  `ICurrentUser`, whose contract guarantees a stable id for the scope; never read a claim, an
  `HttpContext`, or anything else ambient — this layer cannot reach them anyway.

See ADR 0009.

## Resilience

**No resilience package may be referenced from this layer** — the same rule as caching, for the
same reason. A port like `IExchangeRateProvider` (`Rates/IExchangeRateProvider.cs`) declares what
the use case needs and returns `Result<T>`; Infrastructure's adapter owns the `HttpClient`, the
retry, the timeouts, and translates the transport's failure into that `Result` before this layer
ever sees it. A handler that receives a failed `Result` from a port never retries, translates, or
catches anything itself — the port's own failure, whatever kind it is, passes straight through.

See ADR 0014.

## Never appears here

- `Microsoft.EntityFrameworkCore` — this layer depends on the port, not on EF
- `Microsoft.AspNetCore`
- `Microsoft.Extensions.Http.Resilience`, `Polly`, or `System.Net.Http.HttpClient`
- Any `AiFramework.Infrastructure` or `.Api` namespace

Blocked by the dependency-rule hook.

## Domain event handlers

`IDomainEventHandler<TEvent>` (`src/Application/Abstractions/DomainEventHandling.cs`) lives
here, alongside `ICommand<T>`/`IQuery<T>`. Handlers go under `<Feature>/`, next to the command
or query for that feature.

**Handlers must be idempotent.** Delivery off the outbox is at-least-once, and retry granularity
is the message rather than the handler: if one handler in a fan-out throws, the whole message is
retried, re-running handlers that already succeeded on the first attempt. `DomainEventContext`
(same file) carries `MessageId` and `Attempt`; `MessageId` is stable across every redelivery of
the same event, so it is the dedupe key a handler checks before doing anything with a side
effect — `OrderPlacedAuditHandler` is the existing example, keyed on `MessageId` via a database
uniqueness constraint rather than an in-memory check.

## Error handling

Expected failures — not found, conflict, validation — return `Result<T>` rather
than throwing. Exceptions are for genuinely exceptional conditions. A handler that
returns `Result` gives the Api layer something to map to a status code without a
`try`/`catch`.

## Tests

`tests/Application.Tests`. Substitute the ports with NSubstitute. Assert on the
`Result` and on the calls made to the ports. Never touch a database.
