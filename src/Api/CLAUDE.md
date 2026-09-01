# Api

HTTP boundary and composition root.

## Belongs here

Controllers, request/response DTOs, FluentValidation validators for those DTOs,
the global `IExceptionHandler`, middleware, and DI wiring.

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
  root. A controller reaching into a repository is a violation. This one is not
  hook-enforceable, so it is on you and on `dotnet-reviewer`.

## Exception handling

One `IExceptionHandler` maps the domain hierarchy to RFC 9457 `ProblemDetails`:

| Exception | Status |
|---|---|
| `ValidationException` | 400 |
| `NotFoundException` | 404 |
| `ConflictException` | 409 |
| anything else | 500, logged, message not leaked |

`IExceptionHandler.TryHandleAsync` receives the exception as a parameter, so this handler needs
no `catch (Exception)` of its own — and could not have one without a suppression. CA1031 is
`error` globally in `.editorconfig`, with no exemption for this file.

## Tests

`tests/Api.IntegrationTests`, via `WebApplicationFactory<Program>`. Assert status
codes, `ProblemDetails` shape, and validation responses.
