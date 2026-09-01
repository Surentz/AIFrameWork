# Domain Events and the Transactional Outbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An aggregate raises a domain event, the event is written to an outbox table inside the same transaction as the aggregate, and a channel-fed worker pool delivers it to its handlers at least once — surviving a process crash.

**Architecture:** Aggregates collect events on an `Entity` base. A `SaveChanges` interceptor turns pending events into `outbox` rows **added to the same `DbContext` before the save**, so EF writes them in the aggregate's transaction. A `BackgroundService` claims batches with `FOR UPDATE SKIP LOCKED` and writes them to a bounded `Channel`; a pool of workers drains it, resolves handlers through a registration-captured delegate (no reflection, matching the command side), and marks each row `Processed`, rescheduled with backoff, or `Dead`.

**Tech Stack:** .NET 10, `System.Threading.Channels`, EF Core + Npgsql, xUnit, FluentAssertions, NSubstitute, Testcontainers.PostgreSql.

**Spec:** `docs/superpowers/specs/2026-08-28-in-process-messaging-design.md` — this plan implements **§7 (Domain), §8 (Application), §9 (Infrastructure), §10 (the CA1031 exemption)** and the event half of §11–§12. The request path (§6) is already built and merged.

**Builds on:** `main` at `feac87e` — 8 projects, 48 tests, the full request path with a reflection-free dispatcher, validation and unit-of-work behaviors, and EF persistence over PostgreSQL.

## Global Constraints

- **.NET SDK 10.0.400, `net10.0`, `LangVersion` 14.0** — inherited from the root `Directory.Build.props`. **Never re-declare them in a `.csproj`.**
- **Package versions are exact literals, never floating ranges.** Existing pins: `FluentAssertions` 7.2.2 (7.x is Apache-2.0; **8+ is commercially licensed — never take 8**), `NSubstitute` 6.2.0, `xunit` 2.9.3, `Testcontainers.PostgreSql` 4.14.0, EF Core / Npgsql 10.0.11 / 10.0.3.
- **The dependency rule is hook-enforced.** `Domain` → nothing; `Application` → `Domain`; `Infrastructure` → `Application`; `Api` → `Application` + `Infrastructure` (DI registration only). `Domain` may not reference `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `Microsoft.Extensions.DependencyInjection`, `System.Data` or `System.ComponentModel.DataAnnotations` — and `tests/Domain.Tests/ArchitectureTests.cs` fails the build if any appear.
- **Warnings are errors** from compiler, analyzers (SonarAnalyzer, Meziantou, AsyncFixer) and build. `CA1062` is `error`. `CA1848` is enforced — logging uses the `[LoggerMessage]` source generator, never `logger.LogX(...)`. `dotnet_style_require_accessibility_modifiers = always:error`. Library awaits use `.ConfigureAwait(false)`.
- **Never suppress a diagnostic without a justification comment.** Four exemption sites exist today (`.editorconfig`: `CA2007`, `MA0004`, `MA0048`, `CA1716`; a path-scoped `Migrations/*.cs` section; two `S2326` pragmas). **This plan adds the fifth and it is deliberate** — see Task 9.
- **Never `catch (Exception)`.** `CA1031` is `error` globally. Task 9 is the one place that legitimately needs it, and it takes a documented, file-scoped exemption.
- **Never hand-edit an applied EF migration.** Add a new one; a hook blocks edits.
- **Nullable is enabled.** `required` in `Domain`, never `[Required]`.
- **Minimal using directives** — `ImplicitUsings` is on repo-wide; a redundant using trips SonarAnalyzer S1128, an error here.
- `tests/CLAUDE.md`: name tests `MethodName_Scenario_ExpectedOutcome` (architecture/convention tests name the rule instead); one behaviour per test; **no `Thread.Sleep` — inject `IClock`**; no `[Fact(Skip = ...)]`; `Infrastructure.Tests` shares one container via `PostgresFixture` and `[Collection(nameof(PostgresCollection))]` — **join that collection, never declare a new fixture**.
- **Handlers must be idempotent.** At-least-once delivery makes redelivery normal, and retry granularity is the message, not the handler — a partially-failed fan-out re-runs its successful handlers.

## File structure

| File | Responsibility |
|---|---|
| `src/Domain/Abstractions/DomainEvents.cs` | `IDomainEvent` marker and the `Entity` base that collects raised events |
| `src/Domain/Orders/OrderPlaced.cs` | The one event this plan delivers |
| `src/Domain/Orders/Order.cs` | *Modify* — derives from `Entity`, raises `OrderPlaced` in `Place` |
| `src/Application/Abstractions/DomainEventHandling.cs` | `DomainEventContext`, `IDomainEventHandler<TEvent>` |
| `src/Application/Orders/OrderPlacedAuditHandler.cs` | The worked example: an idempotent handler, and its port |
| `src/Infrastructure/Outbox/OutboxMessage.cs` | The outbox entity and `OutboxStatus` |
| `src/Infrastructure/Outbox/OutboxOptions.cs` | Poll interval, batch size, capacity, worker count, attempts, lease, backoff, retention |
| `src/Infrastructure/Outbox/DomainEventRegistration.cs` | `DomainEventDescriptor`, `AddDomainEvent<TEvent>(name)`, `DomainEventRegistry` |
| `src/Infrastructure/Outbox/DomainEventsInterceptor.cs` | Turns pending events into outbox rows in the same transaction |
| `src/Infrastructure/Outbox/OutboxPoller.cs` | `PollOnceAsync` — the claim query, and pruning. Testable without a host. |
| `src/Infrastructure/Outbox/OutboxWorkItemProcessor.cs` | `ProcessAsync` — dispatch one item, decide its outcome. Testable without a channel. |
| `src/Infrastructure/Outbox/OutboxHostedServices.cs` | The two `BackgroundService` pumps. Thin by design; **holds the CA1031 exemption**. |
| `src/Infrastructure/Persistence/Configurations/OutboxMessageConfiguration.cs` | EF mapping; `Status` as a **string** |
| `src/Infrastructure/Persistence/Configurations/OrderAuditConfiguration.cs` | Mapping for the audit table the example handler writes |

**The pump/processing split is deliberate and load-bearing.** `OutboxPoller.PollOnceAsync` and `OutboxWorkItemProcessor.ProcessAsync` hold all the logic and are directly testable; the two `BackgroundService` loops are thin enough to need no tests of their own. Without that split, every outbox test would depend on host timing and `tests/CLAUDE.md` bans the `Thread.Sleep` that would paper over it.

---

### Task 1: The `Entity` base and `IDomainEvent`

**Files:**
- Create: `src/Domain/Abstractions/DomainEvents.cs`
- Test: `tests/Domain.Tests/Abstractions/EntityTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `AiFramework.Domain.Abstractions.IDomainEvent` (marker), and `Entity` with `IReadOnlyCollection<IDomainEvent> DomainEvents`, `protected void Raise(IDomainEvent)`, `public void ClearDomainEvents()`

Namespace note: the spec says `AiFramework.Domain.Common`; use `AiFramework.Domain.Abstractions` instead, to match the existing `AiFramework.Application.Abstractions`. Record that deviation in your report.

- [ ] **Step 1: Write the failing tests**

Create `tests/Domain.Tests/Abstractions/EntityTests.cs`:

```csharp
using AiFramework.Domain.Abstractions;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Abstractions;

public sealed class EntityTests
{
    private sealed record Raised : IDomainEvent;

    private sealed class Thing : Entity
    {
        public void Do() => Raise(new Raised());
    }

    [Fact]
    public void Raise_AppendsToDomainEvents()
    {
        var thing = new Thing();

        thing.Do();

        thing.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<Raised>();
    }

    [Fact]
    public void ClearDomainEvents_EmptiesTheCollection()
    {
        var thing = new Thing();
        thing.Do();

        thing.ClearDomainEvents();

        thing.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void DomainEvents_OnANewEntity_IsEmpty()
    {
        var thing = new Thing();

        thing.DomainEvents.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Domain.Tests --nologo --verbosity quiet`
Expected: FAIL — `IDomainEvent` and `Entity` do not exist.

- [ ] **Step 3: Implement**

Create `src/Domain/Abstractions/DomainEvents.cs`:

```csharp
namespace AiFramework.Domain.Abstractions;

/// <summary>
/// Marker for something that happened in the domain. Deliberately carries no timestamp:
/// Domain has no clock, and the outbox row's OccurredAt is stamped by the interceptor,
/// which can inject IClock. Events stay pure data and trivially testable.
/// </summary>
public interface IDomainEvent;

/// <summary>An aggregate root that collects the events it raises until they are persisted.</summary>
public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>
    /// Public because the SaveChanges interceptor, in another assembly, must call it after
    /// copying the events to the outbox. The alternative — internal plus InternalsVisibleTo —
    /// would create a compile-time coupling between Domain and Infrastructure that the
    /// dependency rule exists to prevent.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Domain.Tests --nologo --verbosity quiet`
Expected: PASS, and `Domain_references_no_banned_namespace` still passes.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Abstractions/DomainEvents.cs tests/Domain.Tests/Abstractions/EntityTests.cs
git commit -m "feat(domain): add the Entity base and IDomainEvent marker"
```

---

### Task 2: `Order` raises `OrderPlaced`

**Files:**
- Create: `src/Domain/Orders/OrderPlaced.cs`
- Modify: `src/Domain/Orders/Order.cs`
- Modify: `src/Infrastructure/Persistence/Configurations/OrderConfiguration.cs`
- Test: `tests/Domain.Tests/Orders/OrderTests.cs` (add to the existing file)

**Interfaces:**
- Consumes: `Entity`, `IDomainEvent` (Task 1)
- Produces: `OrderPlaced(Guid OrderId, string Sku, int Quantity) : IDomainEvent`; `Order : Entity` raising it from `Place`

**The EF gotcha this task must handle.** `Order` gains a `DomainEvents` collection property. EF Core will try to map it and fail, because `IDomainEvent` is not an entity type. `OrderConfiguration` must `Ignore` it. Forgetting this produces a model-building error on the first `DbContext` use, not at compile time.

- [ ] **Step 1: Write the failing test**

Append to `tests/Domain.Tests/Orders/OrderTests.cs`:

```csharp
    [Fact]
    public void Place_RaisesOrderPlaced()
    {
        var id = Guid.NewGuid();

        var order = Order.Place(id, "SKU-1", 3, PlacedAt);

        order.DomainEvents.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new OrderPlaced(id, "SKU-1", 3));
    }

    [Fact]
    public void Place_WithAnInvalidQuantity_RaisesNothing()
    {
        var act = () => Order.Place(Guid.NewGuid(), "SKU-1", 0, PlacedAt);

        act.Should().Throw<DomainException>();
    }
```

The second test is thin on its own — the object never exists to inspect — but it pins that the guard runs *before* the event is raised. Keep it.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Domain.Tests --nologo --verbosity quiet`
Expected: FAIL — `OrderPlaced` does not exist and `Order` has no `DomainEvents`.

- [ ] **Step 3: Implement the event**

Create `src/Domain/Orders/OrderPlaced.cs`:

```csharp
using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Orders;

public sealed record OrderPlaced(Guid OrderId, string Sku, int Quantity) : IDomainEvent;
```

- [ ] **Step 4: Make `Order` an `Entity` that raises it**

In `src/Domain/Orders/Order.cs`, add `using AiFramework.Domain.Abstractions;`, change the declaration to `public sealed class Order : Entity`, and raise the event as the last statement of `Place`, after both guards:

```csharp
        var order = new Order(id, sku, quantity, placedAt);
        order.Raise(new OrderPlaced(id, sku, quantity));
        return order;
```

`Raise` is `protected`, so it is callable from inside `Order` on an `Order` instance.

- [ ] **Step 5: Tell EF to ignore the collection**

In `src/Infrastructure/Persistence/Configurations/OrderConfiguration.cs`, inside `Configure`:

```csharp
        // DomainEvents is transient state the interceptor drains before save; it is not
        // persisted. Without this, EF tries to map IDomainEvent as an entity type and the
        // model fails to build at first use.
        builder.Ignore(o => o.DomainEvents);
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test --nologo --verbosity quiet`
Expected: PASS across the whole solution — including the existing `Infrastructure.Tests` persistence tests, which prove the model still builds.

- [ ] **Step 7: Commit**

```bash
git add src/Domain/Orders/ src/Infrastructure/Persistence/Configurations/OrderConfiguration.cs tests/Domain.Tests/Orders/OrderTests.cs
git commit -m "feat(domain): raise OrderPlaced when an order is placed"
```

---

### Task 3: The handler contract

**Files:**
- Create: `src/Application/Abstractions/DomainEventHandling.cs`

**Interfaces:**
- Consumes: `IDomainEvent` (Task 1)
- Produces: `DomainEventContext(Guid MessageId, int Attempt)` and `IDomainEventHandler<in TEvent>` with `Task HandleAsync(TEvent, DomainEventContext, CancellationToken)`

No test in this task: these are contract types with no behaviour, and `tests/CLAUDE.md` forbids a test that asserts nothing. Task 5 is the first thing that can exercise them.

- [ ] **Step 1: Create the contracts**

Create `src/Application/Abstractions/DomainEventHandling.cs`:

```csharp
using AiFramework.Domain.Abstractions;

namespace AiFramework.Application.Abstractions;

/// <summary>
/// What a handler needs to be idempotent. MessageId is stable across every redelivery of the
/// same event, so it is the dedupe key — "have I already processed message X?" is answerable
/// without inventing a business key. Attempt lets a handler degrade on a retry.
/// </summary>
public readonly record struct DomainEventContext(Guid MessageId, int Attempt);

/// <summary>
/// Handles one domain event. MUST be idempotent: delivery is at-least-once, and retry
/// granularity is the message rather than the handler, so a partially-failed fan-out
/// re-runs the handlers that already succeeded.
/// </summary>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    public Task HandleAsync(TEvent domainEvent, DomainEventContext context, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build --nologo --verbosity quiet`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Application/Abstractions/DomainEventHandling.cs
git commit -m "feat(application): add the domain event handler contract"
```

---

### Task 4: The outbox table

**Files:**
- Create: `src/Infrastructure/Outbox/OutboxMessage.cs`
- Create: `src/Infrastructure/Persistence/Configurations/OutboxMessageConfiguration.cs`
- Modify: `src/Infrastructure/Persistence/AiFrameworkDbContext.cs`
- Create: migration under `src/Infrastructure/Persistence/Migrations/`

**Interfaces:**
- Consumes: nothing
- Produces: `OutboxMessage` (internal to Infrastructure) and `OutboxStatus`; a `DbSet<OutboxMessage> Outbox` on the context

**`Status` must persist as a string.** The claim query in Task 7 is raw SQL comparing against `'Pending'` and `'InFlight'`. Stored as an ordinal, that comparison silently matches nothing — the poller would run forever against a full table. A string column also survives someone reordering the enum.

- [ ] **Step 1: Create the entity**

Create `src/Infrastructure/Outbox/OutboxMessage.cs`:

```csharp
namespace AiFramework.Infrastructure.Outbox;

public enum OutboxStatus
{
    Pending,
    InFlight,
    Processed,
    Dead,
}

/// <summary>One domain event, persisted in the same transaction as the aggregate that raised it.</summary>
public sealed class OutboxMessage
{
    public required Guid Id { get; init; }

    /// <summary>The stable registered name, e.g. "order.placed" — not the CLR type name.</summary>
    public required string EventName { get; init; }

    public required string Payload { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public OutboxStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public DateTimeOffset? LeasedUntil { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? LastError { get; set; }
}
```

- [ ] **Step 2: Map it**

Create `src/Infrastructure/Persistence/Configurations/OutboxMessageConfiguration.cs`:

```csharp
using AiFramework.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.EventName).IsRequired().HasMaxLength(128);
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.OccurredAt).IsRequired();

        // Persisted as a string, not an ordinal: the claim query in OutboxPoller is raw SQL
        // comparing against 'Pending' and 'InFlight'. An ordinal column would match nothing
        // and the poller would spin against a full table. A string also survives a reorder
        // of the enum members.
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property(m => m.LastError).HasMaxLength(2048);

        builder.HasIndex(m => new { m.Status, m.NextAttemptAt })
            .HasDatabaseName("ix_outbox_pending");
    }
}
```

- [ ] **Step 3: Expose it on the context**

In `src/Infrastructure/Persistence/AiFrameworkDbContext.cs`, add `using AiFramework.Infrastructure.Outbox;` and:

```csharp
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
```

- [ ] **Step 4: Add the migration**

```bash
dotnet ef migrations add AddOutbox \
  --project src/Infrastructure/AiFramework.Infrastructure.csproj \
  --output-dir Persistence/Migrations
```

The existing `DesignTimeDbContextFactory` means no `--startup-project` is needed. Read the generated migration: it must create an `outbox` table whose `Status` column is `text`/`varchar`, **not** `integer`. If it is an integer, Step 2 did not take effect — fix the configuration and regenerate rather than editing the migration.

- [ ] **Step 5: Verify**

Run: `dotnet test --nologo --verbosity quiet`
Expected: PASS. The existing Testcontainers tests apply migrations, so this proves the new migration applies cleanly.

- [ ] **Step 6: Commit**

```bash
git add src/Infrastructure/Outbox/ src/Infrastructure/Persistence/
git commit -m "feat(infrastructure): add the outbox table"
```

---

### Task 5: Event registration and the dispatch delegate

**Files:**
- Create: `src/Infrastructure/Outbox/DomainEventRegistration.cs`
- Test: `tests/Infrastructure.Tests/Outbox/DomainEventRegistryTests.cs`

**Interfaces:**
- Consumes: `IDomainEventHandler<TEvent>` (Task 3)
- Produces: `DomainEventDescriptor(string Name, Type EventType, Func<IServiceProvider, string, DomainEventContext, CancellationToken, Task> Dispatch)`; `IServiceCollection.AddDomainEvent<TEvent>(string name)`; `DomainEventRegistry` with `TryGet(string, out DomainEventDescriptor)` and `GetName(Type)`

This mirrors `AddCommand` exactly: the closed generic is captured in a `static` local function at registration time, so dispatch is a dictionary lookup plus a delegate call. **Do not reach for `MakeGenericType`** — the no-reflection property is the reason this codebase rejected MediatR.

- [ ] **Step 1: Write the failing tests**

Create `tests/Infrastructure.Tests/Outbox/DomainEventRegistryTests.cs`:

```csharp
using System.Text.Json;
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Abstractions;
using AiFramework.Infrastructure.Outbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Tests.Outbox;

public sealed record Pinged(string Text) : IDomainEvent;

/// <summary>
/// Per-provider sink. NOT a static list: xUnit runs test classes in parallel, and two classes
/// share this handler — a static collection would race and produce flaky, order-dependent
/// failures that look like real bugs.
/// </summary>
public sealed class HandlerSink
{
    public List<string> Seen { get; } = [];
}

public sealed class RecordingHandler(HandlerSink sink) : IDomainEventHandler<Pinged>
{
    public Task HandleAsync(Pinged domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        sink.Seen.Add($"{domainEvent.Text}:{context.Attempt}");
        return Task.CompletedTask;
    }
}

public sealed class DomainEventRegistryTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<HandlerSink>();
        services.AddDomainEvent<Pinged>("test.pinged");
        services.AddScoped<IDomainEventHandler<Pinged>, RecordingHandler>();
        services.AddSingleton<DomainEventRegistry>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void GetName_ForARegisteredEvent_ReturnsTheStableName()
    {
        using var provider = Build();

        provider.GetRequiredService<DomainEventRegistry>()
            .GetName(typeof(Pinged)).Should().Be("test.pinged");
    }

    [Fact]
    public void GetName_ForAnUnregisteredEvent_ThrowsNamingTheType()
    {
        using var provider = Build();

        var act = () => provider.GetRequiredService<DomainEventRegistry>().GetName(typeof(Order2));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Order2*");
    }

    [Fact]
    public async Task Dispatch_DeserialisesAndInvokesEveryHandler()
    {
        using var provider = Build();
        var sink = provider.GetRequiredService<HandlerSink>();
        var registry = provider.GetRequiredService<DomainEventRegistry>();
        registry.TryGet("test.pinged", out var descriptor).Should().BeTrue();

        var payload = JsonSerializer.Serialize(new Pinged("hello"));
        await descriptor!.Dispatch(
            provider, payload, new DomainEventContext(Guid.NewGuid(), 2), CancellationToken.None);

        sink.Seen.Should().ContainSingle().Which.Should().Be("hello:2");
    }

    [Fact]
    public void TryGet_ForAnUnknownName_ReturnsFalse()
    {
        using var provider = Build();

        provider.GetRequiredService<DomainEventRegistry>()
            .TryGet("nope", out _).Should().BeFalse();
    }

    private sealed record Order2 : IDomainEvent;
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: FAIL — none of these types exist.

- [ ] **Step 3: Implement**

Create `src/Infrastructure/Outbox/DomainEventRegistration.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>The JSON settings the outbox reads and writes with. One place, so they cannot drift.</summary>
public static class OutboxJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// An event's dispatch closure, captured at registration so dispatch needs no reflection —
/// the same technique AddCommand uses on the request path.
/// </summary>
public sealed record DomainEventDescriptor(
    string Name,
    Type EventType,
    Func<IServiceProvider, string, DomainEventContext, CancellationToken, Task> Dispatch);

public static class DomainEventRegistration
{
    /// <summary>
    /// Registers an event under a STABLE STRING NAME. The name, not the CLR type name, is what
    /// the outbox stores — so renaming the record cannot orphan unprocessed rows.
    /// </summary>
    public static IServiceCollection AddDomainEvent<TEvent>(this IServiceCollection services, string name)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        static async Task DispatchAsync(
            IServiceProvider sp, string json, DomainEventContext context, CancellationToken ct)
        {
            var domainEvent = JsonSerializer.Deserialize<TEvent>(json, OutboxJson.Options)
                ?? throw new InvalidOperationException(
                    $"Payload for '{typeof(TEvent).Name}' deserialised to null.");

            foreach (var handler in sp.GetServices<IDomainEventHandler<TEvent>>())
            {
                await handler.HandleAsync(domainEvent, context, ct).ConfigureAwait(false);
            }
        }

        return services.AddSingleton(new DomainEventDescriptor(name, typeof(TEvent), DispatchAsync));
    }
}

/// <summary>Name-to-descriptor lookup, built once. Singleton, like CommandRegistry.</summary>
public sealed class DomainEventRegistry
{
    private readonly Dictionary<string, DomainEventDescriptor> _byName;
    private readonly Dictionary<Type, DomainEventDescriptor> _byType;

    public DomainEventRegistry(IEnumerable<DomainEventDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var all = descriptors.ToArray();

        _byName = all.GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single()
                : throw new InvalidOperationException(
                    $"Domain event name '{g.Key}' is registered {g.Count()} times."),
                StringComparer.Ordinal);

        _byType = all.GroupBy(d => d.EventType)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single()
                : throw new InvalidOperationException(
                    $"Domain event '{g.Key.Name}' is registered {g.Count()} times."));
    }

    public bool TryGet(string name, [MaybeNullWhen(false)] out DomainEventDescriptor descriptor) =>
        _byName.TryGetValue(name, out descriptor);

    /// <summary>Used by the interceptor. Throws loudly rather than dropping an event silently.</summary>
    public string GetName(Type eventType) =>
        _byType.TryGetValue(eventType, out var descriptor)
            ? descriptor.Name
            : throw new InvalidOperationException(
                $"Domain event '{eventType?.Name}' has no AddDomainEvent<T>(name) registration. " +
                "Add one in the composition root, or the event cannot be persisted.");
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Infrastructure/Outbox/DomainEventRegistration.cs tests/Infrastructure.Tests/Outbox/
git commit -m "feat(infrastructure): add reflection-free domain event registration"
```

---

### Task 6: The interceptor — atomicity

**Files:**
- Create: `src/Infrastructure/Outbox/DomainEventsInterceptor.cs`
- Modify: `src/Infrastructure/InfrastructureRegistration.cs`
- Test: `tests/Infrastructure.Tests/Outbox/OutboxAtomicityTests.cs`

**Interfaces:**
- Consumes: `Entity` (Task 1), `OutboxMessage` (Task 4), `DomainEventRegistry` (Task 5), `IClock`
- Produces: `DomainEventsInterceptor : SaveChangesInterceptor`, registered on the `DbContext`

**This is the task the whole design rests on.** The interceptor must add outbox rows to the same `DbContext` **before** the save, inside `SavingChangesAsync` — not `SavedChangesAsync`. That is the only reason the rows land in the aggregate's transaction. The atomicity test is what proves it, and it must fail if someone moves the logic to the wrong hook.

- [ ] **Step 1: Write the failing tests**

Create `tests/Infrastructure.Tests/Outbox/OutboxAtomicityTests.cs`:

```csharp
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Outbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Outbox;

[Collection(nameof(PostgresCollection))]
public sealed class OutboxAtomicityTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SaveChanges_WhenTheAggregateIsSaved_WritesAnOutboxRow()
    {
        var id = Guid.NewGuid();
        await using (var context = fixture.CreateContextWithOutbox())
        {
            context.Orders.Add(Order.Place(id, "SKU-A", 2, PlacedAt));
            await context.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContextWithOutbox();
        var row = await verify.Outbox.SingleOrDefaultAsync(m => m.Payload.Contains(id.ToString()));

        row.Should().NotBeNull();
        row!.EventName.Should().Be("order.placed");
        row.Status.Should().Be(OutboxStatus.Pending);
        row.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task SaveChanges_WhenTheSaveFails_WritesNoOutboxRow()
    {
        var id = Guid.NewGuid();

        await using (var seed = fixture.CreateContextWithOutbox())
        {
            seed.Orders.Add(Order.Place(id, "SKU-B", 1, PlacedAt));
            await seed.SaveChangesAsync();
        }

        // Re-inserting the same primary key makes SaveChangesAsync fail at the database.
        // If the outbox row were written outside the aggregate's transaction, this would
        // leave a second row behind — that is exactly the bug this test exists to catch.
        await using (var clash = fixture.CreateContextWithOutbox())
        {
            clash.Orders.Add(Order.Place(id, "SKU-B", 1, PlacedAt));
            var act = async () => await clash.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verify = fixture.CreateContextWithOutbox();
        var rows = await verify.Outbox.Where(m => m.Payload.Contains(id.ToString())).ToListAsync();

        rows.Should().HaveCount(1, "the failed save must leave no additional outbox row");
    }

    [Fact]
    public async Task SaveChanges_ClearsTheAggregatesPendingEvents()
    {
        await using var context = fixture.CreateContextWithOutbox();
        var order = Order.Place(Guid.NewGuid(), "SKU-C", 1, PlacedAt);
        context.Orders.Add(order);

        await context.SaveChangesAsync();

        order.DomainEvents.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Extend the fixture**

`PostgresFixture.CreateContext()` builds a context with no interceptor. Add a second factory that wires one, so these tests exercise the real path. In `tests/Infrastructure.Tests/Persistence/PostgresFixture.cs`:

```csharp
    /// <summary>A context with the domain-events interceptor attached, as production has it.</summary>
    public AiFrameworkDbContext CreateContextWithOutbox()
    {
        var services = new ServiceCollection();
        services.AddDomainEvent<OrderPlaced>("order.placed");
        services.AddSingleton<DomainEventRegistry>();
        services.AddSingleton<IClock, SystemClock>();
        using var provider = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<AiFrameworkDbContext>()
            .UseNpgsql(ConnectionString)
            .AddInterceptors(new DomainEventsInterceptor(
                provider.GetRequiredService<DomainEventRegistry>(),
                provider.GetRequiredService<IClock>()))
            .Options;

        return new AiFrameworkDbContext(options);
    }
```

Add the usings the file needs. Leave the existing `CreateContext()` untouched — the repository tests use it and must keep passing.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: FAIL — `DomainEventsInterceptor` does not exist.

- [ ] **Step 4: Implement**

Create `src/Infrastructure/Outbox/DomainEventsInterceptor.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>
/// Copies pending domain events onto the outbox as part of the SAME SaveChanges call, so EF
/// writes them in the aggregate's transaction. This must run in SavingChangesAsync, before the
/// save: in SavedChangesAsync the transaction has already committed and atomicity is gone.
/// </summary>
public sealed class DomainEventsInterceptor(DomainEventRegistry registry, IClock clock)
    : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        var context = eventData.Context;
        if (context is null)
        {
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        var entities = context.ChangeTracker.Entries<Entity>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToArray();

        var occurredAt = clock.UtcNow;

        foreach (var entity in entities)
        {
            foreach (var domainEvent in entity.DomainEvents)
            {
                context.Add(new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    EventName = registry.GetName(domainEvent.GetType()),
                    Payload = JsonSerializer.Serialize(
                        domainEvent, domainEvent.GetType(), OutboxJson.Options),
                    OccurredAt = occurredAt,
                    Status = OutboxStatus.Pending,
                });
            }

            entity.ClearDomainEvents();
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
```

Note `JsonSerializer.Serialize(value, type, options)` rather than the generic overload: the static type here is `IDomainEvent`, and the generic overload would serialise only the interface's (empty) surface.

- [ ] **Step 5: Wire it in production**

In `src/Infrastructure/InfrastructureRegistration.cs`, inside `AddInfrastructure`, replace the `AddDbContext` call:

```csharp
        services.AddSingleton<DomainEventRegistry>();
        services.AddSingleton<DomainEventsInterceptor>();
        services.AddDbContext<AiFrameworkDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .AddInterceptors(sp.GetRequiredService<DomainEventsInterceptor>()));
```

and register the event inside `AddMessaging`, beside the command and query registrations:

```csharp
        services.AddDomainEvent<OrderPlaced>("order.placed");
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test --nologo --verbosity quiet`
Expected: PASS across the solution.

- [ ] **Step 7: Prove the atomicity test can fail**

This is the single most important test in the plan; a green result that cannot go red is worthless. Temporarily change `SavingChangesAsync` to `SavedChangesAsync` (matching that method's signature), run `dotnet test tests/Infrastructure.Tests`, and confirm `SaveChanges_WhenTheSaveFails_WritesNoOutboxRow` FAILS. Restore, confirm PASS. Report both outputs. Do not commit the temporary change.

- [ ] **Step 8: Commit**

```bash
git add src/Infrastructure/Outbox/DomainEventsInterceptor.cs src/Infrastructure/InfrastructureRegistration.cs tests/Infrastructure.Tests/
git commit -m "feat(infrastructure): write domain events to the outbox atomically"
```

---

### Task 7: The poller — claiming, leasing and pruning

**Files:**
- Create: `src/Infrastructure/Outbox/OutboxOptions.cs`
- Create: `src/Infrastructure/Outbox/OutboxPoller.cs`
- Test: `tests/Infrastructure.Tests/Outbox/OutboxPollerTests.cs`

**Interfaces:**
- Consumes: `OutboxMessage` (Task 4)
- Produces: `OutboxOptions`; `OutboxWorkItem(Guid Id, string EventName, string Payload, int Attempt)`; `OutboxPoller` with `Task<IReadOnlyList<OutboxWorkItem>> ClaimAsync(CancellationToken)` and `Task<int> PruneAsync(CancellationToken)`

**`FOR UPDATE SKIP LOCKED` is what makes this safe across instances**, and the lease-expiry clause is what recovers rows from a worker that died mid-handler. `Attempts` increments **at claim time**, not on failure — otherwise a message that hard-crashes the process loops forever without exhausting its budget.

- [ ] **Step 1: Create the options**

Create `src/Infrastructure/Outbox/OutboxOptions.cs`:

```csharp
namespace AiFramework.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int BatchSize { get; init; } = 20;

    public int ChannelCapacity { get; init; } = 100;

    public int WorkerCount { get; init; } = 2;

    public int MaxAttempts { get; init; } = 5;

    /// <summary>Must comfortably exceed the slowest handler, or a healthy message is reclaimed and run twice.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(7);
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Infrastructure.Tests/Outbox/OutboxPollerTests.cs`:

```csharp
using AiFramework.Infrastructure.Outbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Outbox;

[Collection(nameof(PostgresCollection))]
public sealed class OutboxPollerTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private OutboxPoller CreatePoller(TestClock clock, OutboxOptions? options = null) =>
        new(fixture.CreateContext(), Options.Create(options ?? new OutboxOptions()), clock);

    private async Task<Guid> SeedAsync(OutboxStatus status, DateTimeOffset? nextAttemptAt = null,
        DateTimeOffset? leasedUntil = null, DateTimeOffset? processedAt = null)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        context.Outbox.Add(new OutboxMessage
        {
            Id = id,
            EventName = "test.event",
            Payload = "{}",
            OccurredAt = Now,
            Status = status,
            NextAttemptAt = nextAttemptAt,
            LeasedUntil = leasedUntil,
            ProcessedAt = processedAt,
        });
        await context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ClaimAsync_ClaimsAPendingRowAndIncrementsAttempts()
    {
        var id = await SeedAsync(OutboxStatus.Pending);
        var poller = CreatePoller(new TestClock(Now));

        var claimed = await poller.ClaimAsync(CancellationToken.None);

        claimed.Should().ContainSingle(i => i.Id == id).Which.Attempt.Should().Be(1);

        await using var verify = fixture.CreateContext();
        var row = await verify.Outbox.SingleAsync(m => m.Id == id);
        row.Status.Should().Be(OutboxStatus.InFlight);
        row.LeasedUntil.Should().NotBeNull();
    }

    [Fact]
    public async Task ClaimAsync_SkipsARowWhoseBackoffHasNotElapsed()
    {
        await SeedAsync(OutboxStatus.Pending, nextAttemptAt: Now.AddMinutes(5));
        var poller = CreatePoller(new TestClock(Now));

        var claimed = await poller.ClaimAsync(CancellationToken.None);

        claimed.Should().BeEmpty();
    }

    [Fact]
    public async Task ClaimAsync_ReclaimsAnInFlightRowWhoseLeaseExpired()
    {
        var id = await SeedAsync(OutboxStatus.InFlight, leasedUntil: Now.AddMinutes(-1));
        var poller = CreatePoller(new TestClock(Now));

        var claimed = await poller.ClaimAsync(CancellationToken.None);

        claimed.Should().ContainSingle(i => i.Id == id);
    }

    [Fact]
    public async Task ClaimAsync_LeavesAProcessedRowAlone()
    {
        await SeedAsync(OutboxStatus.Processed, processedAt: Now);
        var poller = CreatePoller(new TestClock(Now));

        var claimed = await poller.ClaimAsync(CancellationToken.None);

        claimed.Should().BeEmpty();
    }

    [Fact]
    public async Task ClaimAsync_WithTwoConcurrentPollers_ReturnsDisjointSets()
    {
        for (var i = 0; i < 10; i++)
        {
            await SeedAsync(OutboxStatus.Pending);
        }

        var first = CreatePoller(new TestClock(Now), new OutboxOptions { BatchSize = 10 });
        var second = CreatePoller(new TestClock(Now), new OutboxOptions { BatchSize = 10 });

        var results = await Task.WhenAll(
            first.ClaimAsync(CancellationToken.None),
            second.ClaimAsync(CancellationToken.None));

        results[0].Select(i => i.Id).Intersect(results[1].Select(i => i.Id))
            .Should().BeEmpty("FOR UPDATE SKIP LOCKED must prevent double-claiming");
    }

    [Fact]
    public async Task PruneAsync_DeletesProcessedRowsPastRetentionButKeepsDeadOnes()
    {
        var old = await SeedAsync(OutboxStatus.Processed, processedAt: Now.AddDays(-30));
        var dead = await SeedAsync(OutboxStatus.Dead, processedAt: Now.AddDays(-30));
        var poller = CreatePoller(new TestClock(Now));

        await poller.PruneAsync(CancellationToken.None);

        await using var verify = fixture.CreateContext();
        (await verify.Outbox.AnyAsync(m => m.Id == old)).Should().BeFalse();
        (await verify.Outbox.AnyAsync(m => m.Id == dead)).Should().BeTrue();
    }
}
```

- [ ] **Step 3: Add the test clock**

`tests/CLAUDE.md` bans `Thread.Sleep` and requires an injected `IClock`. Create `tests/Infrastructure.Tests/TestClock.cs`:

```csharp
using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Tests;

/// <summary>A clock the test drives, so no test ever waits on wall time.</summary>
public sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
```

- [ ] **Step 4: Run to verify it fails**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: FAIL — `OutboxPoller` does not exist.

- [ ] **Step 5: Implement**

Create `src/Infrastructure/Outbox/OutboxPoller.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>A claimed row on its way to a worker. A record struct, never a tracked entity.</summary>
public readonly record struct OutboxWorkItem(Guid Id, string EventName, string Payload, int Attempt);

/// <summary>
/// Claims batches of due outbox rows. Exposed as plain async methods rather than a loop so it
/// can be tested without a host — the BackgroundService that calls it stays thin.
/// </summary>
public sealed class OutboxPoller(
    AiFrameworkDbContext context, IOptions<OutboxOptions> options, IClock clock)
{
    private readonly OutboxOptions _options = options.Value;

    /// <summary>
    /// Atomically claims up to BatchSize due rows. FOR UPDATE SKIP LOCKED is what makes this
    /// safe across application instances; the leased_until clause is what recovers rows from a
    /// worker that died mid-handler. Attempts increments HERE, at claim time — if it only
    /// incremented on failure, a message that hard-crashes the process would loop forever.
    /// </summary>
    public async Task<IReadOnlyList<OutboxWorkItem>> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var until = now.Add(_options.LeaseDuration);

        const string Sql = """
            UPDATE outbox SET "Status" = 'InFlight', "LeasedUntil" = @until, "Attempts" = "Attempts" + 1
            WHERE "Id" IN (
                SELECT "Id" FROM outbox
                WHERE ("Status" = 'Pending'  AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= @now))
                   OR ("Status" = 'InFlight' AND "LeasedUntil" < @now)
                ORDER BY "OccurredAt"
                LIMIT @batch
                FOR UPDATE SKIP LOCKED
            )
            RETURNING "Id", "EventName", "Payload", "Attempts";
            """;

        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("until", until);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch", _options.BatchSize);

        var claimed = new List<OutboxWorkItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(new OutboxWorkItem(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return claimed;
    }

    /// <summary>Deletes Processed rows past retention. Dead rows are never pruned — they are the signal.</summary>
    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.Subtract(_options.RetentionPeriod);

        return context.Outbox
            .Where(m => m.Status == OutboxStatus.Processed
                && m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
```

**Every identifier is double-quoted, and that is not cosmetic.** EF names columns after the properties — `"Status"`, `"Attempts"`, `"NextAttemptAt"` — in PascalCase, but PostgreSQL folds *unquoted* identifiers to lower case. A bare `status = 'Pending'` therefore looks for a column called `status`, does not find `"Status"`, and errors at runtime rather than at compile time. **Open the migration generated in Task 4 and confirm the exact column names before running these tests**, adjusting the SQL if EF named anything differently. Report what you found.

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: PASS, including the concurrent-claim test.

- [ ] **Step 7: Commit**

```bash
git add src/Infrastructure/Outbox/OutboxOptions.cs src/Infrastructure/Outbox/OutboxPoller.cs tests/Infrastructure.Tests/
git commit -m "feat(infrastructure): claim outbox rows with SKIP LOCKED and prune processed ones"
```

---

### Task 8: The processor — dispatch, retry, dead-letter

**Files:**
- Create: `src/Infrastructure/Outbox/OutboxWorkItemProcessor.cs`
- Test: `tests/Infrastructure.Tests/Outbox/OutboxWorkItemProcessorTests.cs`

**Interfaces:**
- Consumes: `OutboxWorkItem` (Task 7), `DomainEventRegistry` (Task 5), `OutboxOptions`
- Produces: `OutboxWorkItemProcessor` with `Task ProcessAsync(OutboxWorkItem, CancellationToken)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Infrastructure.Tests/Outbox/OutboxWorkItemProcessorTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Abstractions;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Outbox;

public sealed record Boom : IDomainEvent;

public sealed class BoomHandler : IDomainEventHandler<Boom>
{
    public Task HandleAsync(Boom domainEvent, DomainEventContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("handler exploded");
}

[Collection(nameof(PostgresCollection))]
public sealed class OutboxWorkItemProcessorTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private ServiceProvider BuildProvider(OutboxOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<HandlerSink>();
        services.AddDomainEvent<Pinged>("test.pinged");
        services.AddDomainEvent<Boom>("test.boom");
        services.AddScoped<IDomainEventHandler<Pinged>, RecordingHandler>();
        services.AddScoped<IDomainEventHandler<Boom>, BoomHandler>();
        services.AddSingleton<DomainEventRegistry>();
        services.AddSingleton<IClock>(new TestClock(Now));
        services.AddSingleton(Options.Create(options ?? new OutboxOptions()));
        services.AddDbContext<AiFrameworkDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddScoped<OutboxWorkItemProcessor>();
        return services.BuildServiceProvider();
    }

    private async Task<Guid> SeedAsync(string eventName, string payload, int attempts)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        context.Outbox.Add(new OutboxMessage
        {
            Id = id, EventName = eventName, Payload = payload, OccurredAt = Now,
            Status = OutboxStatus.InFlight, Attempts = attempts,
        });
        await context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ProcessAsync_WhenTheHandlerSucceeds_MarksTheRowProcessed()
    {
        var id = await SeedAsync("test.pinged", """{"text":"hi"}""", 1);
        await using var provider = BuildProvider();

        await provider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.pinged", """{"text":"hi"}""", 1), CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var row = await verify.Outbox.SingleAsync(m => m.Id == id);
        row.Status.Should().Be(OutboxStatus.Processed);
        row.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_WhenTheHandlerThrows_ReschedulesWithBackoff()
    {
        var id = await SeedAsync("test.boom", "{}", 1);
        await using var provider = BuildProvider();

        await provider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.boom", "{}", 1), CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var row = await verify.Outbox.SingleAsync(m => m.Id == id);
        row.Status.Should().Be(OutboxStatus.Pending);
        row.NextAttemptAt.Should().BeAfter(Now);
        row.LastError.Should().Contain("handler exploded");
    }

    [Fact]
    public async Task ProcessAsync_OnTheFinalAttempt_MarksTheRowDead()
    {
        var id = await SeedAsync("test.boom", "{}", 5);
        await using var provider = BuildProvider(new OutboxOptions { MaxAttempts = 5 });

        await provider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.boom", "{}", 5), CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var row = await verify.Outbox.SingleAsync(m => m.Id == id);
        row.Status.Should().Be(OutboxStatus.Dead);
        row.LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_WithAnUnknownEventName_MarksTheRowDeadImmediately()
    {
        var id = await SeedAsync("test.unregistered", "{}", 1);
        await using var provider = BuildProvider();

        await provider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.unregistered", "{}", 1), CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var row = await verify.Outbox.SingleAsync(m => m.Id == id);
        row.Status.Should().Be(OutboxStatus.Dead, "no number of retries invents a registration");
    }

    [Fact]
    public async Task ProcessAsync_PassesTheMessageIdAsTheDedupeKey()
    {
        var id = await SeedAsync("test.pinged", """{"text":"x"}""", 3);
        await using var provider = BuildProvider();

        await provider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.pinged", """{"text":"x"}""", 3), CancellationToken.None);

        provider.GetRequiredService<HandlerSink>().Seen
            .Should().ContainSingle().Which.Should().Be("x:3");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: FAIL — `OutboxWorkItemProcessor` does not exist.

- [ ] **Step 3: Implement**

Create `src/Infrastructure/Outbox/OutboxWorkItemProcessor.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>
/// Processes exactly one claimed item and records the outcome. All the decision logic lives
/// here rather than in the worker loop, so it is testable without a host or a channel.
/// </summary>
public sealed class OutboxWorkItemProcessor(
    AiFrameworkDbContext context,
    DomainEventRegistry registry,
    IServiceProvider services,
    IOptions<OutboxOptions> options,
    IClock clock)
{
    private readonly OutboxOptions _options = options.Value;

    public async Task ProcessAsync(OutboxWorkItem item, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(item.EventName, out var descriptor))
        {
            await CompleteAsync(item, OutboxStatus.Dead,
                $"No registration for event name '{item.EventName}'.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await descriptor.Dispatch(
                services, item.Payload, new DomainEventContext(item.Id, item.Attempt), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailAsync(item, exception, cancellationToken).ConfigureAwait(false);
            return;
        }

        await CompleteAsync(item, OutboxStatus.Processed, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailAsync(OutboxWorkItem item, Exception exception, CancellationToken cancellationToken)
    {
        if (item.Attempt >= _options.MaxAttempts)
        {
            await CompleteAsync(item, OutboxStatus.Dead, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Exponential backoff with jitter, capped. Jitter stops a batch that failed together
        // from retrying in lockstep and re-colliding on whatever caused the failure.
        var seconds = Math.Min(Math.Pow(2, item.Attempt), _options.MaxBackoff.TotalSeconds);
        var jitter = Random.Shared.NextDouble() * seconds * 0.2;
        var nextAttemptAt = clock.UtcNow.AddSeconds(seconds + jitter);

        await context.Outbox.Where(m => m.Id == item.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                .SetProperty(m => m.LeasedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LastError, Truncate(exception.Message)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task CompleteAsync(
        OutboxWorkItem item, OutboxStatus status, string? error, CancellationToken cancellationToken)
    {
        var processedAt = clock.UtcNow;

        return context.Outbox.Where(m => m.Id == item.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, status)
                .SetProperty(m => m.ProcessedAt, processedAt)
                .SetProperty(m => m.LeasedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LastError, error == null ? null : Truncate(error)),
                cancellationToken);
    }

    private static string Truncate(string value) =>
        value.Length <= 2048 ? value : value[..2048];
}
```

The `catch (Exception ...) when (exception is not OperationCanceledException)` here is a **filtered** catch, not a bare `catch (Exception)`, and the filter is what keeps shutdown cancellation from being recorded as a handler failure. If CA1031 fires on it anyway, do not suppress — report it, because Task 9 owns the exemption and it may need to cover this file too.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: PASS, all five.

- [ ] **Step 5: Commit**

```bash
git add src/Infrastructure/Outbox/OutboxWorkItemProcessor.cs tests/Infrastructure.Tests/Outbox/OutboxWorkItemProcessorTests.cs
git commit -m "feat(infrastructure): dispatch outbox items with retry and dead-lettering"
```

---

### Task 9: The pumps, the channel, and the CA1031 exemption

**Files:**
- Create: `src/Infrastructure/Outbox/OutboxHostedServices.cs`
- Modify: `.editorconfig`
- Modify: `CLAUDE.md` (root)
- Modify: `src/Infrastructure/InfrastructureRegistration.cs`

**Interfaces:**
- Consumes: `OutboxPoller` (Task 7), `OutboxWorkItemProcessor` (Task 8), `OutboxOptions`
- Produces: `OutboxPollerService` and `OutboxWorkerService`, both `BackgroundService`; a singleton `Channel<OutboxWorkItem>`; `AddOutbox(this IServiceCollection)`

**This task takes the fifth analyzer exemption in the repo, and it is the first for CA1031.** A worker loop must catch every exception, or one throwing handler kills that worker and the pool dies one worker at a time until nothing drains — with no record against the row that caused it. Root `CLAUDE.md` currently says CA1031 has "no exemption"; that sentence stops being true and must be amended in the same commit.

- [ ] **Step 1: Implement the two pumps**

Create `src/Infrastructure/Outbox/OutboxHostedServices.cs`:

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>
/// Claims due rows and hands them to the workers. FullMode.Wait is the point of using a
/// channel: when the workers saturate, WriteAsync blocks, the poller stops claiming, and the
/// DATABASE stays the buffer instead of memory.
/// </summary>
public sealed partial class OutboxPollerService(
    IServiceScopeFactory scopeFactory,
    ChannelWriter<OutboxWorkItem> writer,
    IOptions<OutboxOptions> options,
    ILogger<OutboxPollerService> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var poller = scope.ServiceProvider.GetRequiredService<OutboxPoller>();
                var batch = await poller.ClaimAsync(stoppingToken).ConfigureAwait(false);
                claimed = batch.Count;

                foreach (var item in batch)
                {
                    await writer.WriteAsync(item, stoppingToken).ConfigureAwait(false);
                }

                if (claimed == 0)
                {
                    await poller.PruneAsync(stoppingToken).ConfigureAwait(false);
                }
            }

            if (claimed == 0)
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        writer.Complete();
        LogStopped(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox poller stopped.")]
    private static partial void LogStopped(ILogger logger);
}

/// <summary>Drains the channel. WorkerCount loops share one reader; the channel distributes.</summary>
public sealed partial class OutboxWorkerService(
    IServiceScopeFactory scopeFactory,
    ChannelReader<OutboxWorkItem> reader,
    IOptions<OutboxOptions> options,
    ILogger<OutboxWorkerService> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, _options.WorkerCount)
            .Select(_ => RunAsync(stoppingToken)));

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>();

            // A worker loop must survive anything a handler or the database throws. Without
            // this, one bad message kills this worker, and the pool dies one worker at a time
            // until nothing drains — with no record against the row that caused it. The
            // processor already records handler failures; this catches what escapes it, such
            // as a database error while recording the outcome. See .editorconfig for the
            // file-scoped CA1031 exemption this requires.
            try
            {
                await processor.ProcessAsync(item, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogWorkItemFailed(logger, exception, item.Id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox item {MessageId} failed outside the processor.")]
    private static partial void LogWorkItemFailed(ILogger logger, Exception exception, Guid messageId);
}
```

- [ ] **Step 2: Take the exemption**

Append to `.editorconfig`:

```
# --- Deliberately off, one file only: a channel worker loop must catch every exception or
#     one throwing handler kills that worker and the pool dies one at a time until nothing
#     drains, with no record against the offending row. IExceptionHandler escapes CA1031 by
#     receiving the exception as a parameter; a worker loop has no such out. See
#     docs/superpowers/plans/2026-09-01-domain-events-outbox.md Task 9. ---
[src/Infrastructure/Outbox/OutboxHostedServices.cs]
dotnet_diagnostic.CA1031.severity = none
```

- [ ] **Step 3: Amend the root `CLAUDE.md`**

Its non-negotiable reads: *"Never `catch (Exception)`. The global `IExceptionHandler` receives it as a parameter. CA1031 is a global error with no exemption."* That last clause is now false. Amend it to state that one file-scoped exemption exists, name it, and say why — keeping the file's terse voice and leaving the rest of the bullet intact.

- [ ] **Step 4: Register everything**

In `src/Infrastructure/InfrastructureRegistration.cs`, add:

```csharp
    /// <summary>The outbox pipeline. Call from AddInfrastructure; the hosted services start with the app.</summary>
    public static IServiceCollection AddOutbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OutboxOptions>();

        // The channel is built from the CONFIGURED options, not from a fresh OutboxOptions() —
        // constructing one here would silently ignore any capacity the host configured. It is
        // registered as a singleton Channel<T>, with the reader and writer projected from it,
        // so both pumps provably share one instance.
        services.AddSingleton(sp =>
        {
            var configured = sp.GetRequiredService<IOptions<OutboxOptions>>().Value;
            return Channel.CreateBounded<OutboxWorkItem>(
                new BoundedChannelOptions(configured.ChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = true,
                    SingleReader = false,
                });
        });

        services.AddSingleton(sp => sp.GetRequiredService<Channel<OutboxWorkItem>>().Writer);
        services.AddSingleton(sp => sp.GetRequiredService<Channel<OutboxWorkItem>>().Reader);
        services.AddScoped<OutboxPoller>();
        services.AddScoped<OutboxWorkItemProcessor>();
        services.AddHostedService<OutboxPollerService>();
        services.AddHostedService<OutboxWorkerService>();

        return services;
    }
```

and call `services.AddOutbox();` from `AddInfrastructure` before the `return`.

- [ ] **Step 5: Verify**

Run: `dotnet build --nologo --verbosity quiet` then `dotnet test --nologo --verbosity quiet`
Expected: 0 warnings, 0 errors — in particular **no CA1031**, because the exemption is scoped to exactly that file. All existing tests still pass.

- [ ] **Step 6: Prove the exemption is scoped, not global**

Temporarily add a bare `try { } catch (Exception) { }` to `src/Infrastructure/Outbox/OutboxPoller.cs`, build, and confirm it **FAILS** with CA1031 — proving the exemption did not leak beyond the one file. Remove it and confirm the build is clean. Report both outputs.

- [ ] **Step 7: Commit**

```bash
git add src/Infrastructure/Outbox/OutboxHostedServices.cs src/Infrastructure/InfrastructureRegistration.cs .editorconfig CLAUDE.md
git commit -m "feat(infrastructure): add the outbox pumps and scope the CA1031 exemption"
```

---

### Task 10: The worked example — an idempotent handler

**Files:**
- Create: `src/Application/Orders/OrderPlacedAuditHandler.cs`
- Create: `src/Infrastructure/Persistence/OrderAuditWriter.cs`
- Create: `src/Infrastructure/Persistence/Configurations/OrderAuditConfiguration.cs`
- Modify: `src/Infrastructure/Persistence/AiFrameworkDbContext.cs`, `src/Infrastructure/InfrastructureRegistration.cs`
- Create: migration
- Test: `tests/Application.Tests/Orders/OrderPlacedAuditHandlerTests.cs`

**Interfaces:**
- Consumes: `IDomainEventHandler<OrderPlaced>` (Task 3), `OrderPlaced` (Task 2)
- Produces: `IOrderAuditWriter.RecordAsync(Guid messageId, Guid orderId, CancellationToken)`; `OrderPlacedAuditHandler`

**Idempotency is the point of this task, not the audit trail.** The handler must be safe to run twice with the same `MessageId`, because at-least-once delivery makes that normal. The `MessageId` is the primary key of the audit row — that is what makes the second run a no-op rather than a duplicate.

- [ ] **Step 1: Write the failing test**

Create `tests/Application.Tests/Orders/OrderPlacedAuditHandlerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class OrderPlacedAuditHandlerTests
{
    private readonly IOrderAuditWriter _writer = Substitute.For<IOrderAuditWriter>();

    [Fact]
    public async Task HandleAsync_RecordsTheAuditKeyedOnTheMessageId()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var handler = new OrderPlacedAuditHandler(_writer);

        await handler.HandleAsync(
            new OrderPlaced(orderId, "SKU-1", 2),
            new DomainEventContext(messageId, 1),
            CancellationToken.None);

        await _writer.Received(1).RecordAsync(messageId, orderId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CalledTwiceWithTheSameMessageId_RecordsWithTheSameKey()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var handler = new OrderPlacedAuditHandler(_writer);
        var context = new DomainEventContext(messageId, 1);
        var raised = new OrderPlaced(orderId, "SKU-1", 2);

        await handler.HandleAsync(raised, context, CancellationToken.None);
        await handler.HandleAsync(raised, new DomainEventContext(messageId, 2), CancellationToken.None);

        // Both calls use the same key, so the writer's insert-if-absent makes the second a
        // no-op. The handler carries no dedupe state of its own; the key is the whole mechanism.
        await _writer.Received(2).RecordAsync(messageId, orderId, Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Application.Tests --nologo --verbosity quiet`
Expected: FAIL — neither type exists.

- [ ] **Step 3: Implement the port and handler**

Create `src/Application/Orders/OrderPlacedAuditHandler.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

/// <summary>Writes an audit row if one does not already exist for this message.</summary>
public interface IOrderAuditWriter
{
    public Task RecordAsync(Guid messageId, Guid orderId, CancellationToken cancellationToken);
}

/// <summary>
/// Idempotent by construction: the outbox MessageId is the audit row's primary key, so a
/// redelivery inserts nothing rather than duplicating. Delivery is at-least-once and retry
/// granularity is the message, so this handler WILL run twice at some point.
/// </summary>
public sealed class OrderPlacedAuditHandler(IOrderAuditWriter writer)
    : IDomainEventHandler<OrderPlaced>
{
    public Task HandleAsync(
        OrderPlaced domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return writer.RecordAsync(context.MessageId, domainEvent.OrderId, cancellationToken);
    }
}
```

- [ ] **Step 4: Implement the writer and its table**

Create `src/Infrastructure/Persistence/OrderAuditWriter.cs`:

```csharp
using AiFramework.Application.Orders;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class OrderAudit
{
    public required Guid MessageId { get; init; }

    public required Guid OrderId { get; init; }
}

public sealed class OrderAuditWriter(AiFrameworkDbContext context) : IOrderAuditWriter
{
    public async Task RecordAsync(Guid messageId, Guid orderId, CancellationToken cancellationToken)
    {
        // Insert-if-absent, keyed on the outbox MessageId. This is what makes redelivery a
        // no-op rather than a duplicate row.
        var exists = await context.OrderAudits
            .AsNoTracking()
            .AnyAsync(a => a.MessageId == messageId, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            return;
        }

        context.OrderAudits.Add(new OrderAudit { MessageId = messageId, OrderId = orderId });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

Create `src/Infrastructure/Persistence/Configurations/OrderAuditConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class OrderAuditConfiguration : IEntityTypeConfiguration<OrderAudit>
{
    public void Configure(EntityTypeBuilder<OrderAudit> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("order_audit");
        builder.HasKey(a => a.MessageId);
        builder.Property(a => a.OrderId).IsRequired();
    }
}
```

Add `public DbSet<OrderAudit> OrderAudits => Set<OrderAudit>();` to the context, then:

```bash
dotnet ef migrations add AddOrderAudit \
  --project src/Infrastructure/AiFramework.Infrastructure.csproj \
  --output-dir Persistence/Migrations
```

- [ ] **Step 5: Register the handler and writer**

In `AddMessaging`, beside the existing registrations:

```csharp
        services.AddScoped<IDomainEventHandler<OrderPlaced>, OrderPlacedAuditHandler>();
```

and in `AddInfrastructure`: `services.AddScoped<IOrderAuditWriter, OrderAuditWriter>();`

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test --nologo --verbosity quiet`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Application/Orders/OrderPlacedAuditHandler.cs src/Infrastructure/Persistence/ src/Infrastructure/InfrastructureRegistration.cs tests/Application.Tests/
git commit -m "feat: add an idempotent OrderPlaced audit handler"
```

---

### Task 11: End-to-end delivery

**Files:**
- Test: `tests/Api.IntegrationTests/Orders/OutboxDeliveryTests.cs`
- Modify: `tests/Api.IntegrationTests/ApiFactory.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–10
- Produces: proof that `POST /api/orders` results in a delivered domain event

**Determinism, not sleeping.** `tests/CLAUDE.md` bans `Thread.Sleep`, and waiting on the `BackgroundService` would be flaky. Instead, resolve `OutboxPoller` and `OutboxWorkItemProcessor` from the factory's services and drive **one cycle** by hand. That proves the same wiring the hosted services use, without depending on their timing.

- [ ] **Step 1: Write the failing test**

Create `tests/Api.IntegrationTests/Orders/OutboxDeliveryTests.cs`:

```csharp
using System.Net.Http.Json;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests.Orders;

public sealed class OutboxDeliveryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task PostOrders_WritesAPendingOutboxRow()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-1", Quantity = 2 });
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var row = await context.Outbox.AsNoTracking()
            .SingleAsync(m => m.Payload.Contains(orderId.ToString()));

        row.EventName.Should().Be("order.placed");
        row.Status.Should().Be(OutboxStatus.Pending);
    }

    [Fact]
    public async Task PostOrders_ThenDrainingTheOutbox_DeliversToTheHandler()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-2", Quantity = 1 });
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        await factory.DrainOutboxOnceAsync();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        var audit = await context.OrderAudits.AsNoTracking()
            .SingleOrDefaultAsync(a => a.OrderId == orderId);
        audit.Should().NotBeNull("the handler must have run");

        var row = await context.Outbox.AsNoTracking()
            .SingleAsync(m => m.Payload.Contains(orderId.ToString()));
        row.Status.Should().Be(OutboxStatus.Processed);
    }

    [Fact]
    public async Task DrainingTwice_LeavesOneAuditRow()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-E2E-3", Quantity = 1 });
        var orderId = await response.Content.ReadFromJsonAsync<Guid>();

        await factory.DrainOutboxOnceAsync();
        await factory.RedeliverAsync(orderId);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        (await context.OrderAudits.AsNoTracking().CountAsync(a => a.OrderId == orderId))
            .Should().Be(1, "the handler is idempotent, so redelivery adds nothing");
    }
}
```

- [ ] **Step 2: Stop the hosted services from racing the test**

`AddInfrastructure` registers `OutboxPollerService` and `OutboxWorkerService`, and `WebApplicationFactory` starts hosted services with the host. So in a test the background poller is running too — and it will happily claim the row **before** `DrainOutboxOnceAsync` gets to it. The row would be `InFlight` or already `Processed`, the manual drain would claim nothing, and the assertions would fail intermittently depending on timing. That is exactly the flakiness `tests/CLAUDE.md`'s no-`Thread.Sleep` rule exists to prevent, and no amount of waiting fixes it.

Remove them in `ConfigureWebHost`, so the test is the only thing draining:

```csharp
            // The outbox pumps are removed here deliberately. They would compete with
            // DrainOutboxOnceAsync for the same rows and make these tests timing-dependent.
            // The drain helper below invokes the same OutboxPoller and OutboxWorkItemProcessor
            // the pumps use, so the wiring under test is still the real one.
            services.RemoveAll<IHostedService>();
```

`RemoveAll<T>` needs `using Microsoft.Extensions.DependencyInjection.Extensions;`. Note this removes *all* hosted services; nothing else in this app registers one today, and if that changes the removal should be narrowed to the two outbox types.

- [ ] **Step 3: Add the two test helpers**

In `tests/Api.IntegrationTests/ApiFactory.cs`:

```csharp
    /// <summary>
    /// Runs one claim-and-process cycle synchronously. Drives the same OutboxPoller and
    /// OutboxWorkItemProcessor the hosted services use, so the wiring under test is real —
    /// but deterministically, without waiting on BackgroundService timing.
    /// </summary>
    public async Task DrainOutboxOnceAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var poller = scope.ServiceProvider.GetRequiredService<OutboxPoller>();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>();

        foreach (var item in await poller.ClaimAsync(CancellationToken.None))
        {
            await processor.ProcessAsync(item, CancellationToken.None);
        }
    }

    /// <summary>Forces a redelivery of an already-processed message, to exercise idempotency.</summary>
    public async Task RedeliverAsync(Guid orderId)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var row = await context.Outbox.SingleAsync(m => m.Payload.Contains(orderId.ToString()));

        var processor = scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>();
        await processor.ProcessAsync(
            new OutboxWorkItem(row.Id, row.EventName, row.Payload, row.Attempts + 1),
            CancellationToken.None);
    }
```

Add the usings the file needs.

- [ ] **Step 4: Run to verify it fails, then passes**

Run: `dotnet test tests/Api.IntegrationTests --nologo --verbosity quiet`
Expected: FAIL first (helpers missing), then PASS once they exist.

- [ ] **Step 5: Commit**

```bash
git add tests/Api.IntegrationTests/
git commit -m "test(api): prove domain events are persisted and delivered end to end"
```

---

### Task 12: Event registration completeness

**Files:**
- Modify: `tests/Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs`

**Interfaces:**
- Consumes: `AddDomainEvent` (Task 5), `AddMessaging` (existing)
- Produces: a test that fails the build when an `IDomainEvent` has no registration

The existing file already guards `ICommand<>`, `IQuery<>` and `AbstractValidator<T>`. Domain events have the same weakness and a worse failure mode: an unregistered event throws at **save time**, taking the user's whole request down.

- [ ] **Step 1: Add the test**

```csharp
    [Fact]
    public void AddMessaging_RegistersEveryDomainEventInTheDomainAssembly()
    {
        var services = new ServiceCollection();
        services.AddMessaging();
        var registered = services
            .Select(d => d.ImplementationInstance)
            .OfType<DomainEventDescriptor>()
            .Select(d => d.EventType)
            .ToHashSet();

        var unregistered = DomainMarker.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                && typeof(IDomainEvent).IsAssignableFrom(t)
                && !registered.Contains(t));

        unregistered.Should().BeEmpty(
            "an unregistered domain event throws at SaveChanges, taking the request down with it");
    }
```

`DomainMarker` is an alias for `AiFramework.Domain.AssemblyMarker` — add `using DomainMarker = AiFramework.Domain.AssemblyMarker;` at the top. **The alias is required**: this file's namespace walks up through `AiFramework.Infrastructure`, which has its own `AssemblyMarker`, so an unqualified reference binds to the wrong assembly and the test scans nothing.

- [ ] **Step 2: Prove it can fail**

Delete the `AddDomainEvent<OrderPlaced>("order.placed")` line from `AddMessaging`, run the test, confirm it FAILS naming `OrderPlaced`, then restore and confirm PASS. Capture both outputs. Do not use a comment to disable the line — SonarAnalyzer S125 makes commented-out code a build error; delete and restore instead.

- [ ] **Step 3: Commit**

```bash
git add tests/Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs
git commit -m "test(infrastructure): fail the build on an unregistered domain event"
```

---

### Task 13: Documentation

**Files:**
- Modify: `src/Domain/CLAUDE.md`, `src/Application/CLAUDE.md`, `src/Infrastructure/CLAUDE.md`, `tests/CLAUDE.md`
- Modify: `docs/adr/0003-in-process-messaging-without-mediatr.md`

- [ ] **Step 1: `src/Domain/CLAUDE.md`**

Add: domain events are pure data records implementing `IDomainEvent`; they carry no timestamp because `Domain` has no clock — the outbox row's `OccurredAt` is stamped by the interceptor. Aggregates derive from `Entity` and raise events through `Raise()`; `ClearDomainEvents()` is public because the interceptor must call it from another assembly.

- [ ] **Step 2: `src/Application/CLAUDE.md`**

Add: `IDomainEventHandler<TEvent>` lives here; handlers go under `<Feature>/`. **Handlers must be idempotent** — delivery is at-least-once and retry granularity is the message, so a partially-failed fan-out re-runs handlers that already succeeded. `DomainEventContext.MessageId` is the dedupe key.

- [ ] **Step 3: `src/Infrastructure/CLAUDE.md`**

Add an `Outbox/` section: the interceptor writes events in the aggregate's transaction and must stay in `SavingChangesAsync`; the poller claims with `FOR UPDATE SKIP LOCKED` and increments `Attempts` at claim time; the processor owns retry and dead-lettering; the two `BackgroundService` pumps are deliberately thin so the logic is testable without a host. Note that `OutboxHostedServices.cs` holds the repo's only CA1031 exemption and why.

- [ ] **Step 4: `tests/CLAUDE.md`**

Add: outbox tests join `PostgresCollection` like every other database test; timing is driven by an injected `TestClock`, never by waiting; the end-to-end delivery test drives one poll-and-process cycle by hand rather than waiting on the hosted services.

- [ ] **Step 5: Amend ADR 0003**

Its Consequences say the event half is specified but not built. That is now false. Amend that section — Decision and Status stay as they are; ADRs are not rewritten once accepted, and this corrects a statement later work made stale.

- [ ] **Step 6: Verify and commit**

```bash
dotnet build --nologo --verbosity quiet
dotnet test --nologo --verbosity quiet
git add src/*/CLAUDE.md tests/CLAUDE.md docs/adr/0003-in-process-messaging-without-mediatr.md
git commit -m "docs: record the event path and update ADR 0003"
```

---

## Definition of done

- `dotnet build` clean, 0 warnings across 8 projects.
- `dotnet test` passes, including Testcontainers-backed outbox tests and end-to-end delivery.
- An order placed over HTTP writes an outbox row **in the same transaction**, and a failed save leaves none — proven by a test that was seen failing when the interceptor was moved to `SavedChangesAsync`.
- Two concurrent pollers claim disjoint sets.
- A failing handler reschedules with backoff, and dies after `MaxAttempts` with the row kept.
- An unknown event name dead-letters immediately.
- Redelivering an already-processed message adds no second audit row.
- A missing `AddDomainEvent` registration fails the build.
- Exactly one CA1031 exemption exists, scoped to one file, proven not to leak; root `CLAUDE.md` describes it accurately.

## Deferred

- **Alerting on `Dead` rows** — a health check or metric on the dead-row count. Dead rows are discoverable only by querying the table.
- **A replay mechanism** for dead messages, once the failure modes are understood.
- **Per-aggregate ordering** — partitioning the channel by aggregate id. Spec D6 accepts no ordering guarantee.
- **`Directory.Packages.props`** to end duplicated package versions across test projects.
