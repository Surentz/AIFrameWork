---
name: dotnet-testing
description: Use when writing or modifying .NET tests in this repo - xUnit, FluentAssertions, NSubstitute, and what belongs at each layer.
---

# .NET Testing

Stack: xUnit + FluentAssertions + NSubstitute.

## What to test where

| Project | Tests | Never |
|---|---|---|
| `Domain.Tests` | invariants, value objects, pure logic | mocks, I/O, fixtures |
| `Application.Tests` | handlers with ports substituted | real database |
| `Infrastructure.Tests` | repositories, EF mapping, query translation, the outbox | business rules |
| `Api.IntegrationTests` | HTTP contract, status codes, `ProblemDetails`, authorization policies | unit-level logic |
| `Worker.IntegrationTests` | jobs reaching their handlers, lanes, scheduling, inbound broker messages | anything the API host does |

If a `Domain` test needs a mock, the logic is in the wrong layer. That is the signal, not an
inconvenience to work around.

## Shape

```csharp
[Fact]
public void Cancel_OnAShippedOrder_ThrowsAStateException()
{
    var order = Placed();
    order.Ship(ShippedAt);

    var act = () => order.Cancel("Changed my mind.", CancelledAt);

    act.Should().Throw<OrderStateException>();
}
```

- Name: `MethodName_Scenario_ExpectedOutcome`.
- Arrange, act, assert — separated by blank lines, no comment headers.
- One behaviour per test. If the name needs "and", split it.
- `[Theory]` with `[InlineData]` for the same behaviour over several inputs; not for several
  behaviours.

## Rules

- Assert on behaviour, not implementation. A test that breaks on a rename but not on a bug is
  a liability.
- Never mock a type you do not own. Wrap it in a port and substitute that.
- No `Thread.Sleep`. Inject an `IClock`.
- No shared mutable state between tests. Build fresh data per test.
- A skipped test is deleted or fixed. `[Fact(Skip = ...)]` is not a resting state.

## Integration tests

`WebApplicationFactory<Program>` for the Api (`ApiFactory`) and the worker (`WorkerFactory`).
Testcontainers for a real database where query translation matters — the in-memory provider does
not reproduce it faithfully, and a test that passes against it can still fail in production.

Both factories also start a RabbitMQ container, because a durable host cannot boot without a
broker, so `dotnet test` needs a running Docker daemon. Join the project's one collection rather
than declaring a fixture, and simulate a broker outage with `StopBrokerAppAsync()`, never
`PauseAsync` — `tests/CLAUDE.md` has both rules and why.
