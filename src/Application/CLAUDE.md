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
