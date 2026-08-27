# Tests

One project per layer, mirroring `src/`.

| Project | Tests | Style |
|---|---|---|
| `Domain.Tests` | Entities, value objects, invariants | Pure. No mocks, no I/O. |
| `Application.Tests` | Use-case handlers | Ports substituted with NSubstitute |
| `Infrastructure.Tests` | Repositories, EF mapping | Real database via Testcontainers |
| `Api.IntegrationTests` | HTTP contract | `WebApplicationFactory<Program>` |

## Stack

xUnit + FluentAssertions + NSubstitute.

## Rules

- Name tests `MethodName_Scenario_ExpectedOutcome`.
- One behaviour per test. If the name needs "and", split it.
- Assert on behaviour, not on implementation detail.
- Never mock a type you do not own — wrap it in a port and substitute that.
- No `Thread.Sleep`. Inject an `IClock`.
- A test that needs `[Fact(Skip = ...)]` is either deleted or fixed.
