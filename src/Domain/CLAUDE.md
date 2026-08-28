# Domain

The innermost layer. It knows about the business and nothing else.

## Belongs here

Entities, value objects, enums, domain events, domain exceptions
(`DomainException` and its subtypes), and pure business rules.

## Never appears here

**Blocked by the dependency-rule hook at edit time:**

- `Microsoft.EntityFrameworkCore` — persistence is Infrastructure's problem
- `Microsoft.AspNetCore` — HTTP is Api's problem
- `Microsoft.Extensions.DependencyInjection`, `System.Data`
- `System.ComponentModel.DataAnnotations` — use the C# `required` keyword instead.
  `[Required]` drags validation and persistence concerns into this layer.
- Any `AiFramework.Application`, `.Infrastructure`, or `.Api` namespace

The folder name is load-bearing: the hook recognises this layer from the path, matching
`src/Domain/` and a root-namespace-prefixed `src/AiFramework.Domain/`, with the layer names
read from `.claude/hooks/hooks.config.json`. Move or rename this directory to something the
config does not list and the rule stops applying to it — silently.

**Not hook-enforced — upheld by review and by the `dotnet-reviewer` agent:**

- `async` / `Task` — there is nothing to await in pure business logic. The hook
  matches `using` directives against a namespace list and has no keyword check,
  so nothing mechanical stops you writing `async Task` here.

## Shape

Properties are `required` and non-nullable, or genuinely optional and nullable —
never nullable-and-assumed-present. Setters are private; state changes go through
methods that enforce invariants. Collections are exposed as `IReadOnlyCollection<T>`
and initialised, never null.

## Tests

`tests/Domain.Tests`. Pure unit tests, no mocks, no fixtures, no I/O.
If a Domain test needs a mock, the logic is in the wrong layer.
