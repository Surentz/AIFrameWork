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

## Registration completeness

`Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs` is what replaces
compile-time safety for `AddCommand`/`AddQuery`/validator registration: `AddCommand` and
`AddQuery` are reflection-free by design (see `src/Infrastructure/CLAUDE.md`), so a missing
registration compiles fine and only fails at dispatch time. These tests reflect over the
`Application` assembly instead, at test time, and fail the build if any `ICommand<>`,
`IQuery<>`, or `AbstractValidator<T>` lacks its registration. Deleting them removes the only
net catching that class of mistake.

## Testcontainers

`Infrastructure.Tests` shares one Postgres container across its whole test run via
`PostgresFixture` and `[Collection(nameof(PostgresCollection))]` — every test class that needs
the database joins that one collection, so xUnit starts exactly one container for all of them.
**One container per collection, never one per test class.** A new test class that needs
Postgres joins `PostgresCollection`; it does not declare its own fixture.

`Api.IntegrationTests` now follows this too: every class that needs an `ApiFactory` joins
`[Collection(nameof(ApiFactoryCollection))]` (the collection definition lives at the bottom of
`ApiFactory.cs`, mirroring `PostgresFixture.cs`'s pattern), so xUnit starts exactly one Postgres
container for the whole project. A new `Api.IntegrationTests` class that needs the database
joins that same collection — it does not declare its own `IClassFixture<ApiFactory>`.
`HealthTests` deliberately stays outside the collection: it uses its own
`IClassFixture<WebApplicationFactory<Program>>` because `/health` needs a host but never
touches the database, so it does not need the shared container.

## Rules

- Name tests `MethodName_Scenario_ExpectedOutcome`. Architecture and convention tests
  have no method under test, so name those for the rule they enforce instead
  (e.g. `Domain_references_no_other_layer`).
- One behaviour per test. If the name needs "and", split it.
- Assert on behaviour, not on implementation detail.
- Never mock a type you do not own — wrap it in a port and substitute that.
- No `Thread.Sleep`. Inject an `IClock`.
- A test that needs `[Fact(Skip = ...)]` is either deleted or fixed.
- FluentAssertions 7.2.2's `OnlyContain(predicate)` throws on an empty collection
  rather than treating it as vacuously true. Assert on the disallowed subset
  instead (`disallowed.Should().BeEmpty()`), so the check is correct whether or
  not the collection is empty.
