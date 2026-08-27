# Application

Use cases. Orchestrates Domain objects; owns no infrastructure.

## Belongs here

Use-case handlers, **ports** (the interfaces Infrastructure implements —
`IOrderRepository`, `IUnitOfWork`, `IClock`, `IEmailSender`), request and response
models, FluentValidation validators, and `Result<T>`.

## Never appears here

- `Microsoft.EntityFrameworkCore` — this layer depends on the port, not on EF
- `Microsoft.AspNetCore`
- Any `AiFramework.Infrastructure` or `.Api` namespace

Blocked by the dependency-rule hook.

## Error handling

Expected failures — not found, conflict, validation — return `Result<T>` rather
than throwing. Exceptions are for genuinely exceptional conditions. A handler that
returns `Result` gives the Api layer something to map to a status code without a
`try`/`catch`.

## Tests

`tests/Application.Tests`. Substitute the ports with NSubstitute. Assert on the
`Result` and on the calls made to the ports. Never touch a database.
