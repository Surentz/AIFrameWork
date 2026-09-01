# Request Path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A controller can accept an HTTP request, dispatch it to an Application handler through a reflection-free dispatcher, persist the result in one transaction, and map the outcome to a status code.

**Architecture:** Commands and queries are declared in `Application` as `ICommand<T>` / `IQuery<T>` and registered in the composition root with `AddCommand<TCommand, TResponse, THandler>()`, which captures the closed generic in a delegate at registration time — so dispatch is a dictionary lookup, never reflection. Two behaviors wrap every command: validation short-circuits to a failed `Result`, and unit-of-work calls `SaveChangesAsync` exactly once on success. Persistence is EF Core over PostgreSQL, tested against a real engine via Testcontainers.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit, FluentAssertions, NSubstitute, Testcontainers.PostgreSql.

**Spec:** `docs/superpowers/specs/2026-08-28-in-process-messaging-design.md` — this plan implements **§6 (The request path)** in full, plus the persistence foundation §6.3's unit-of-work behavior requires. It does **not** implement §7–§10 (domain events, outbox, channel dispatch); those are the next plan.

**Builds on:** `docs/superpowers/plans/2026-08-28-solution-scaffold.md` (complete — 8 projects, `net10.0` pinned, guardrails proven).

## Global Constraints

- **.NET SDK 10.0.400, `net10.0`, `LangVersion` 14.0** — all inherited from the root `Directory.Build.props`. **Never re-declare them in a `.csproj`.**
- **Package versions are exact literals, never floating ranges.** Existing pins to match: `FluentAssertions` 7.2.2 (7.x is Apache-2.0; **8+ is commercially licensed — never take 8**), `NSubstitute` 6.2.0, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.4, `Microsoft.NET.Test.Sdk` 17.14.1, `coverlet.collector` 6.0.4, `Microsoft.AspNetCore.Mvc.Testing` 10.0.11. New packages: resolve, then pin the exact resolved version and report it.
- **The dependency rule is enforced by a hook that blocks edits.** `Domain` → nothing; `Application` → `Domain`; `Infrastructure` → `Application`; `Api` → `Application` + `Infrastructure` (DI registration only). `Domain` additionally may not reference `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `Microsoft.Extensions.DependencyInjection`, `System.Data`, or `System.ComponentModel.DataAnnotations` — and `tests/Domain.Tests/ArchitectureTests.cs` now fails the build if any of those appear.
- **Warnings are errors** from compiler, analyzers and build. Any warning fails the build.
- **Nullable is enabled.** A missing null check does not compile.
- **`required` in `Domain`, never `[Required]`.** DataAnnotations belong on `Api` DTOs only.
- **Never `catch (Exception)`.** CA1031 is a global error. The global `IExceptionHandler` receives the exception as a parameter and needs no catch of its own.
- **`throw;`, never `throw ex;`.**
- **Never hand-edit an applied EF migration.** Add a new one. A hook blocks edits to applied migrations.
- **No secrets in `appsettings*.json`.** Use `dotnet user-secrets` or environment variables.
- **Never suppress a diagnostic without a justification comment.**
- **Minimal using directives.** `ImplicitUsings` is on repo-wide, and a nested test namespace resolves its production namespace by walk-up. A redundant using trips SonarAnalyzer S1128, an error here.
- **`tests/CLAUDE.md` rules:** name tests `MethodName_Scenario_ExpectedOutcome` (architecture/convention tests name the rule instead); one behaviour per test; assert behaviour not implementation; never mock a type you do not own; no `Thread.Sleep` — inject `IClock`; no `[Fact(Skip = ...)]`; FluentAssertions 7.2.2's `OnlyContain` throws on empty collections, so assert on the disallowed subset with `BeEmpty()`.

## File structure

| File | Responsibility |
|---|---|
| `src/Domain/Orders/Order.cs` | The one aggregate this plan needs. Enforces its own invariants. |
| `src/Domain/DomainException.cs` | Base for invariant violations thrown from `Domain` |
| `src/Application/Abstractions/Result.cs` | `Result`, `Result<T>`, `Error`, `ErrorKind` — the outcome type every handler returns |
| `src/Application/Abstractions/Messaging.cs` | `ICommand<T>`, `IQuery<T>`, handler and dispatcher interfaces |
| `src/Application/Abstractions/Ports.cs` | `IClock`, `IUnitOfWork` |
| `src/Application/Orders/IOrderRepository.cs` | The port `Infrastructure` implements |
| `src/Application/Orders/PlaceOrder.cs` | Command, handler, validator — one feature, one file group |
| `src/Application/Orders/GetOrder.cs` | Query, handler, and the response model |
| `src/Infrastructure/Messaging/Descriptors.cs` | `CommandDescriptor`, `QueryDescriptor` |
| `src/Infrastructure/Messaging/Dispatchers.cs` | `CommandDispatcher`, `QueryDispatcher` |
| `src/Infrastructure/Messaging/Behaviors.cs` | Validation and unit-of-work behaviors |
| `src/Infrastructure/Messaging/MessagingRegistration.cs` | `AddCommand<,,>()`, `AddQuery<,,>()`, `AddMessaging()` |
| `src/Infrastructure/Persistence/AiFrameworkDbContext.cs` | The `DbContext` |
| `src/Infrastructure/Persistence/Configurations/OrderConfiguration.cs` | EF mapping for `Order` — no annotations on the Domain type |
| `src/Infrastructure/Persistence/OrderRepository.cs` | `IOrderRepository` implementation |
| `src/Infrastructure/Persistence/UnitOfWork.cs` | `IUnitOfWork` over the `DbContext` |
| `src/Infrastructure/SystemClock.cs` | `IClock` implementation |
| `src/Infrastructure/InfrastructureRegistration.cs` | The single DI extension `Api` calls |
| `src/Api/Orders/OrdersController.cs` | Bind → dispatch → map |
| `src/Api/Orders/OrderDtos.cs` | Request/response DTOs with `required` + `init` |
| `src/Api/ResultExtensions.cs` | `Result` → `IActionResult` |
| `src/Api/GlobalExceptionHandler.cs` | `IExceptionHandler` → RFC 9457 `ProblemDetails` |

**One feature per file group.** `PlaceOrder.cs` holds the command, its handler and its validator together — they change together, and splitting them across three files triples the reading cost for no isolation benefit.

---

### Task 1: Prerequisites and packages

**Files:**
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj`
- Modify: `src/Application/AiFramework.Application.csproj`
- Modify: `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj`

**Interfaces:**
- Consumes: the eight projects from the scaffold plan
- Produces: EF Core, Npgsql, FluentValidation and Testcontainers available, with exact versions pinned and recorded

- [ ] **Step 1: Verify Docker is running before anything depends on it**

```bash
docker info --format '{{.ServerVersion}}'
```

Expected: a version string. If this fails, **stop and report** — every persistence test in Tasks 11 and 14 needs it, and there is no point proceeding.

- [ ] **Step 2: Add FluentValidation to Application**

`Application` owns validators (see `src/Application/CLAUDE.md`).

```bash
dotnet add src/Application/AiFramework.Application.csproj package FluentValidation
dotnet list src/Application/AiFramework.Application.csproj package
```

Read the resolved version, then edit the `.csproj` to pin it as an exact literal (e.g. `Version="12.0.0"`, not `12.*`). Report the version you pinned.

- [ ] **Step 3: Add EF Core and Npgsql to Infrastructure**

```bash
dotnet add src/Infrastructure/AiFramework.Infrastructure.csproj package Microsoft.EntityFrameworkCore
dotnet add src/Infrastructure/AiFramework.Infrastructure.csproj package Microsoft.EntityFrameworkCore.Design
dotnet add src/Infrastructure/AiFramework.Infrastructure.csproj package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/Infrastructure/AiFramework.Infrastructure.csproj package Microsoft.Extensions.DependencyInjection.Abstractions
dotnet list src/Infrastructure/AiFramework.Infrastructure.csproj package
```

Pin every resolved version as an exact literal. `Microsoft.EntityFrameworkCore.Design` needs `PrivateAssets="all"` — it is a build-time tool, not a runtime dependency.

- [ ] **Step 4: Add Testcontainers to Infrastructure.Tests**

```bash
dotnet add tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj package Testcontainers.PostgreSql
dotnet add tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj package Microsoft.EntityFrameworkCore.Design
dotnet list tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj package
```

Pin exact versions.

- [ ] **Step 5: Confirm the dependency rule still holds**

Adding EF Core to `Infrastructure` is legal; adding it to `Domain` or `Application` is not.

```bash
dotnet build --nologo --verbosity quiet
dotnet test tests/Domain.Tests --nologo --verbosity quiet
```

Expected: build clean, and `Domain_references_no_banned_namespace` still passes — proving EF Core reached `Infrastructure` only.

- [ ] **Step 6: Commit**

```bash
git add src/Application/AiFramework.Application.csproj src/Infrastructure/AiFramework.Infrastructure.csproj tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj
git commit -m "chore: add EF Core, Npgsql, FluentValidation and Testcontainers"
```

---

### Task 2: `Result<T>` and `Error`

**Files:**
- Create: `src/Application/Abstractions/Result.cs`
- Test: `tests/Application.Tests/Abstractions/ResultTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `Result`, `Result<T>`, `Error`, `ErrorKind`. Every handler in this plan returns `Result<T>`; `Api` maps `Error.Kind` to a status code.

- [ ] **Step 1: Write the failing tests**

Create `tests/Application.Tests/Abstractions/ResultTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using FluentAssertions;

namespace AiFramework.Application.Tests.Abstractions;

public sealed class ResultTests
{
    [Fact]
    public void Success_WithValue_ExposesTheValue()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Failure_WithError_ExposesTheError()
    {
        var error = new Error(ErrorKind.NotFound, "order.not_found", "No such order.");

        var result = Result<int>.Failure(error);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Value_OnFailure_Throws()
    {
        var result = Result<int>.Failure(new Error(ErrorKind.Conflict, "c", "m"));

        var act = () => _ = result.Value;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Error_OnSuccess_Throws()
    {
        var result = Result<int>.Success(1);

        var act = () => _ = result.Error;

        act.Should().Throw<InvalidOperationException>();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Application.Tests --nologo --verbosity quiet`
Expected: FAIL — `Result<>`, `Error` and `ErrorKind` do not exist.

- [ ] **Step 3: Implement**

Create `src/Application/Abstractions/Result.cs`:

```csharp
namespace AiFramework.Application.Abstractions;

/// <summary>How a failure should be surfaced at the HTTP boundary.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
}

/// <summary>An expected failure. Exceptions are for genuinely exceptional conditions.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message);

/// <summary>The outcome of a use case that produces a value.</summary>
public sealed class Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T value)
    {
        _value = value;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        _error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read Value of a failed Result.");

    public Error Error => IsSuccess
        ? throw new InvalidOperationException("Cannot read Error of a successful Result.")
        : _error!;

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Application.Tests --nologo --verbosity quiet`
Expected: PASS, and the whole solution builds with 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Application/Abstractions/Result.cs tests/Application.Tests/Abstractions/ResultTests.cs
git commit -m "feat(application): add Result<T> and Error"
```

---

### Task 3: Dispatch contracts

**Files:**
- Create: `src/Application/Abstractions/Messaging.cs`

**Interfaces:**
- Consumes: `Result<T>` from Task 2
- Produces: `ICommand<TResponse>`, `IQuery<TResponse>`, `ICommandHandler<TCommand, TResponse>`, `IQueryHandler<TQuery, TResponse>`, `ICommandDispatcher`, `IQueryDispatcher`. Tasks 5–9 and 12 all bind to these exact signatures.

There is no test in this task: these are marker and contract interfaces with no behaviour. Task 7 is the first thing that can meaningfully exercise them, and a test asserting "an interface exists" would violate `tests/CLAUDE.md`.

- [ ] **Step 1: Create the contracts**

Create `src/Application/Abstractions/Messaging.cs`:

```csharp
namespace AiFramework.Application.Abstractions;

/// <summary>
/// A request that changes state. A type may implement this EXACTLY ONCE — the dispatcher
/// infers TResponse from the argument, and two implementations make that ambiguous.
/// </summary>
public interface ICommand<TResponse>;

/// <summary>A request that reads state. Same single-implementation rule as ICommand.</summary>
public interface IQuery<TResponse>;

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

public interface ICommandDispatcher
{
    Task<Result<TResponse>> SendAsync<TResponse>(
        ICommand<TResponse> command, CancellationToken cancellationToken);
}

public interface IQueryDispatcher
{
    Task<Result<TResponse>> SendAsync<TResponse>(
        IQuery<TResponse> query, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build --nologo --verbosity quiet`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Application/Abstractions/Messaging.cs
git commit -m "feat(application): add command and query dispatch contracts"
```

---

### Task 4: The `Order` aggregate

**Files:**
- Create: `src/Domain/DomainException.cs`
- Create: `src/Domain/Orders/Order.cs`
- Test: `tests/Domain.Tests/Orders/OrderTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `Order.Place(Guid id, string sku, int quantity, DateTimeOffset placedAt) → Order`, with `Id`, `Sku`, `Quantity`, `PlacedAt` properties, and `DomainException`.

`Order` deliberately has **no** base class. The next plan introduces an `Entity` base carrying domain events and retrofits it here; inventing that base now, with no events to hold, would be building for a requirement this plan does not have.

- [ ] **Step 1: Write the failing tests**

Create `tests/Domain.Tests/Orders/OrderTests.cs`:

```csharp
using AiFramework.Domain.Orders;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Orders;

public sealed class OrderTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Place_WithValidDetails_SetsTheProperties()
    {
        var id = Guid.NewGuid();

        var order = Order.Place(id, "SKU-1", 3, PlacedAt);

        order.Id.Should().Be(id);
        order.Sku.Should().Be("SKU-1");
        order.Quantity.Should().Be(3);
        order.PlacedAt.Should().Be(PlacedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Place_WithNonPositiveQuantity_Throws(int quantity)
    {
        var act = () => Order.Place(Guid.NewGuid(), "SKU-1", quantity, PlacedAt);

        act.Should().Throw<DomainException>().WithMessage("*quantity*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Place_WithBlankSku_Throws(string sku)
    {
        var act = () => Order.Place(Guid.NewGuid(), sku, 1, PlacedAt);

        act.Should().Throw<DomainException>().WithMessage("*sku*");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Domain.Tests --nologo --verbosity quiet`
Expected: FAIL — `Order` and `DomainException` do not exist.

- [ ] **Step 3: Implement**

Create `src/Domain/DomainException.cs`:

```csharp
namespace AiFramework.Domain;

/// <summary>A broken domain invariant. Never used for expected failures — those return Result.</summary>
public class DomainException : Exception
{
    public DomainException()
    {
    }

    public DomainException(string message) : base(message)
    {
    }

    public DomainException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
```

Create `src/Domain/Orders/Order.cs`:

```csharp
namespace AiFramework.Domain.Orders;

public sealed class Order
{
    private Order(Guid id, string sku, int quantity, DateTimeOffset placedAt)
    {
        Id = id;
        Sku = sku;
        Quantity = quantity;
        PlacedAt = placedAt;
    }

    public Guid Id { get; private set; }

    public string Sku { get; private set; }

    public int Quantity { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }

    public static Order Place(Guid id, string sku, int quantity, DateTimeOffset placedAt)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new DomainException("An order needs a sku.");
        }

        if (quantity <= 0)
        {
            throw new DomainException("An order needs a positive quantity.");
        }

        return new Order(id, sku, quantity, placedAt);
    }
}
```

Note the private setters rather than `init`: EF Core materialises entities by setting properties, and `private set` lets it do so without a public mutation surface. `src/Domain/CLAUDE.md` requires exactly this shape.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Domain.Tests --nologo --verbosity quiet`
Expected: PASS. The architecture tests must still pass — `Order` uses no banned namespace.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/DomainException.cs src/Domain/Orders/Order.cs tests/Domain.Tests/Orders/OrderTests.cs
git commit -m "feat(domain): add the Order aggregate and DomainException"
```

---

### Task 5: Ports and the `PlaceOrder` command

**Files:**
- Create: `src/Application/Abstractions/Ports.cs`
- Create: `src/Application/Orders/IOrderRepository.cs`
- Create: `src/Application/Orders/PlaceOrder.cs`
- Test: `tests/Application.Tests/Orders/PlaceOrderHandlerTests.cs`

**Interfaces:**
- Consumes: `Result<T>` (Task 2), `ICommand`/`ICommandHandler` (Task 3), `Order.Place` (Task 4)
- Produces: `IClock.UtcNow`, `IUnitOfWork.SaveChangesAsync(CancellationToken)`, `IOrderRepository.AddAsync(Order, CancellationToken)` / `GetAsync(Guid, CancellationToken)`, `PlaceOrder(string Sku, int Quantity) : ICommand<Guid>`, `PlaceOrderHandler`, `PlaceOrderValidator`

- [ ] **Step 1: Write the failing tests**

Create `tests/Application.Tests/Orders/PlaceOrderHandlerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class PlaceOrderHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public PlaceOrderHandlerTests() => _clock.UtcNow.Returns(Now);

    [Fact]
    public async Task HandleAsync_WithValidCommand_AddsTheOrder()
    {
        var handler = new PlaceOrderHandler(_repository, _clock);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<Order>(o => o.Sku == "SKU-1" && o.Quantity == 2 && o.PlacedAt == Now),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsTheNewOrderId()
    {
        var handler = new PlaceOrderHandler(_repository, _clock);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.Value.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_DoesNotSaveChanges()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var handler = new PlaceOrderHandler(_repository, _clock);

        await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
```

That third test looks odd — it substitutes a `IUnitOfWork` the handler never receives. That is the point: it pins the contract that **handlers never commit**, which the unit-of-work behavior in Task 9 depends on. If someone later injects `IUnitOfWork` into this handler and calls it, one command becomes two transactions and the next plan's outbox guarantee silently breaks.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Application.Tests --nologo --verbosity quiet`
Expected: FAIL — the ports, command and handler do not exist.

- [ ] **Step 3: Implement the ports**

Create `src/Application/Abstractions/Ports.cs`:

```csharp
namespace AiFramework.Application.Abstractions;

/// <summary>Time, as a dependency. Never call DateTimeOffset.UtcNow directly.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Commits the current transaction. Handlers never call this — the unit-of-work behavior
/// does, exactly once, after a successful command.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
```

Create `src/Application/Orders/IOrderRepository.cs`:

```csharp
using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

public interface IOrderRepository
{
    Task AddAsync(Order order, CancellationToken cancellationToken);

    Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement the command, handler and validator**

Create `src/Application/Orders/PlaceOrder.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Orders;
using FluentValidation;

namespace AiFramework.Application.Orders;

public sealed record PlaceOrder(string Sku, int Quantity) : ICommand<Guid>;

public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrder>
{
    public PlaceOrderValidator()
    {
        RuleFor(c => c.Sku).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Quantity).GreaterThan(0);
    }
}

public sealed class PlaceOrderHandler(IOrderRepository orders, IClock clock)
    : ICommandHandler<PlaceOrder, Guid>
{
    public async Task<Result<Guid>> HandleAsync(PlaceOrder command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var order = Order.Place(Guid.NewGuid(), command.Sku, command.Quantity, clock.UtcNow);

        await orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

        return Result<Guid>.Success(order.Id);
    }
}
```

`ArgumentNullException.ThrowIfNull` satisfies CA1062, which is `error` in `.editorconfig`.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Application.Tests --nologo --verbosity quiet`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Application/Abstractions/Ports.cs src/Application/Orders/ tests/Application.Tests/Orders/
git commit -m "feat(application): add ports and the PlaceOrder command"
```

---

### Task 6: The `GetOrder` query

**Files:**
- Create: `src/Application/Orders/GetOrder.cs`
- Test: `tests/Application.Tests/Orders/GetOrderHandlerTests.cs`

**Interfaces:**
- Consumes: `IOrderRepository` and `Result<T>` from Task 5 and Task 2
- Produces: `GetOrder(Guid Id) : IQuery<OrderView>`, `OrderView(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt)`, `GetOrderHandler`

- [ ] **Step 1: Write the failing tests**

Create `tests/Application.Tests/Orders/GetOrderHandlerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class GetOrderHandlerTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();

    [Fact]
    public async Task HandleAsync_WhenTheOrderExists_ReturnsTheView()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Order.Place(id, "SKU-1", 4, PlacedAt));
        var handler = new GetOrderHandler(_repository);

        var result = await handler.HandleAsync(new GetOrder(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new OrderView(id, "SKU-1", 4, PlacedAt));
    }

    [Fact]
    public async Task HandleAsync_WhenTheOrderIsMissing_ReturnsNotFound()
    {
        _repository.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);
        var handler = new GetOrderHandler(_repository);

        var result = await handler.HandleAsync(new GetOrder(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Application.Tests --nologo --verbosity quiet`
Expected: FAIL — `GetOrder`, `OrderView` and `GetOrderHandler` do not exist.

- [ ] **Step 3: Implement**

Create `src/Application/Orders/GetOrder.cs`:

```csharp
using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Orders;

public sealed record OrderView(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt);

public sealed record GetOrder(Guid Id) : IQuery<OrderView>;

public sealed class GetOrderHandler(IOrderRepository orders) : IQueryHandler<GetOrder, OrderView>
{
    public async Task<Result<OrderView>> HandleAsync(GetOrder query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var order = await orders.GetAsync(query.Id, cancellationToken).ConfigureAwait(false);

        return order is null
            ? Result<OrderView>.Failure(new Error(
                ErrorKind.NotFound, "order.not_found", $"No order with id '{query.Id}'."))
            : Result<OrderView>.Success(
                new OrderView(order.Id, order.Sku, order.Quantity, order.PlacedAt));
    }
}
```

Note this returns a failed `Result`, not an exception. `src/Application/CLAUDE.md`: expected failures return `Result`; exceptions are for genuinely exceptional conditions.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Application.Tests --nologo --verbosity quiet`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Application/Orders/GetOrder.cs tests/Application.Tests/Orders/GetOrderHandlerTests.cs
git commit -m "feat(application): add the GetOrder query"
```

---

### Task 7: Descriptors, registration and the command dispatcher

**Files:**
- Create: `src/Infrastructure/Messaging/Descriptors.cs`
- Create: `src/Infrastructure/Messaging/MessagingRegistration.cs`
- Create: `src/Infrastructure/Messaging/Dispatchers.cs`
- Test: `tests/Infrastructure.Tests/Messaging/CommandDispatcherTests.cs`

**Interfaces:**
- Consumes: all of `src/Application/Abstractions/Messaging.cs` (Task 3), `PlaceOrder` (Task 5)
- Produces: `CommandDescriptor`, `IServiceCollection.AddCommand<TCommand, TResponse, THandler>()`, `CommandDispatcher : ICommandDispatcher`

This is the heart of spec §6.2 — the reflection-free dispatch.

- [ ] **Step 1: Write the failing tests**

Create `tests/Infrastructure.Tests/Messaging/CommandDispatcherTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Tests.Messaging;

public sealed record Ping(string Text) : ICommand<string>;

public sealed class PingHandler : ICommandHandler<Ping, string>
{
    public Task<Result<string>> HandleAsync(Ping command, CancellationToken cancellationToken) =>
        Task.FromResult(Result<string>.Success(command!.Text.ToUpperInvariant()));
}

public sealed class CommandDispatcherTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddCommand<Ping, string, PingHandler>();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SendAsync_WithARegisteredCommand_InvokesItsHandler()
    {
        await using var provider = BuildProvider();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var result = await dispatcher.SendAsync(new Ping("hello"), CancellationToken.None);

        result.Value.Should().Be("HELLO");
    }

    [Fact]
    public async Task SendAsync_WithAnUnregisteredCommand_Throws()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var act = async () => await dispatcher.SendAsync(new Ping("hello"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ping*");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: FAIL — `AddCommand` and `CommandDispatcher` do not exist.

- [ ] **Step 3: Implement the descriptor**

Create `src/Infrastructure/Messaging/Descriptors.cs`:

```csharp
namespace AiFramework.Infrastructure.Messaging;

/// <summary>
/// A command's dispatch closure, captured at registration so dispatch needs no reflection.
/// The delegate returns object? because the closed generic is known only inside AddCommand.
/// </summary>
public sealed record CommandDescriptor(
    Type CommandType,
    Func<IServiceProvider, object, CancellationToken, Task<object?>> Invoke);

/// <summary>The query-side equivalent. See CommandDescriptor.</summary>
public sealed record QueryDescriptor(
    Type QueryType,
    Func<IServiceProvider, object, CancellationToken, Task<object?>> Invoke);
```

- [ ] **Step 4: Implement registration**

Create `src/Infrastructure/Messaging/MessagingRegistration.cs`:

```csharp
using AiFramework.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Messaging;

public static class MessagingRegistration
{
    /// <summary>
    /// Registers a command, its handler, and a dispatch delegate that closes over TCommand and
    /// TResponse at compile time. The static local function captures nothing, so there is no
    /// closure allocation — but TEvent-style generics still come from the enclosing method.
    /// </summary>
    public static IServiceCollection AddCommand<TCommand, TResponse, THandler>(
        this IServiceCollection services)
        where TCommand : ICommand<TResponse>
        where THandler : class, ICommandHandler<TCommand, TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);

        static async Task<object?> InvokeAsync(
            IServiceProvider sp, object command, CancellationToken ct)
        {
            var handler = sp.GetRequiredService<ICommandHandler<TCommand, TResponse>>();
            return await handler.HandleAsync((TCommand)command, ct).ConfigureAwait(false);
        }

        services.AddScoped<ICommandHandler<TCommand, TResponse>, THandler>();
        return services.AddSingleton(new CommandDescriptor(typeof(TCommand), InvokeAsync));
    }

    public static IServiceCollection AddQuery<TQuery, TResponse, THandler>(
        this IServiceCollection services)
        where TQuery : IQuery<TResponse>
        where THandler : class, IQueryHandler<TQuery, TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);

        static async Task<object?> InvokeAsync(
            IServiceProvider sp, object query, CancellationToken ct)
        {
            var handler = sp.GetRequiredService<IQueryHandler<TQuery, TResponse>>();
            return await handler.HandleAsync((TQuery)query, ct).ConfigureAwait(false);
        }

        services.AddScoped<IQueryHandler<TQuery, TResponse>, THandler>();
        return services.AddSingleton(new QueryDescriptor(typeof(TQuery), InvokeAsync));
    }
}
```

- [ ] **Step 5: Implement the dispatchers**

Create `src/Infrastructure/Messaging/Dispatchers.cs`:

```csharp
using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Messaging;

public sealed class CommandDispatcher(
    IServiceProvider serviceProvider,
    IEnumerable<CommandDescriptor> descriptors) : ICommandDispatcher
{
    private readonly Dictionary<Type, CommandDescriptor> _descriptors =
        descriptors.ToDictionary(d => d.CommandType);

    public async Task<Result<TResponse>> SendAsync<TResponse>(
        ICommand<TResponse> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_descriptors.TryGetValue(command.GetType(), out var descriptor))
        {
            throw new InvalidOperationException(
                $"No handler registered for command '{command.GetType().Name}'. " +
                "Add services.AddCommand<...>() in the composition root.");
        }

        var result = await descriptor.Invoke(serviceProvider, command, cancellationToken)
            .ConfigureAwait(false);

        return (Result<TResponse>)result!;
    }
}

public sealed class QueryDispatcher(
    IServiceProvider serviceProvider,
    IEnumerable<QueryDescriptor> descriptors) : IQueryDispatcher
{
    private readonly Dictionary<Type, QueryDescriptor> _descriptors =
        descriptors.ToDictionary(d => d.QueryType);

    public async Task<Result<TResponse>> SendAsync<TResponse>(
        IQuery<TResponse> query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_descriptors.TryGetValue(query.GetType(), out var descriptor))
        {
            throw new InvalidOperationException(
                $"No handler registered for query '{query.GetType().Name}'. " +
                "Add services.AddQuery<...>() in the composition root.");
        }

        var result = await descriptor.Invoke(serviceProvider, query, cancellationToken)
            .ConfigureAwait(false);

        return (Result<TResponse>)result!;
    }
}
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Infrastructure/Messaging/ tests/Infrastructure.Tests/Messaging/
git commit -m "feat(infrastructure): add reflection-free command and query dispatch"
```

---

### Task 8: The query dispatcher's own test

**Files:**
- Test: `tests/Infrastructure.Tests/Messaging/QueryDispatcherTests.cs`

**Interfaces:**
- Consumes: `AddQuery` and `QueryDispatcher` from Task 7
- Produces: nothing new

Task 7 built both dispatchers because they share `Descriptors.cs` and `MessagingRegistration.cs`, but only the command side was tested. This task closes that gap.

- [ ] **Step 1: Write the failing test**

Create `tests/Infrastructure.Tests/Messaging/QueryDispatcherTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Tests.Messaging;

public sealed record Echo(int Value) : IQuery<int>;

public sealed class EchoHandler : IQueryHandler<Echo, int>
{
    public Task<Result<int>> HandleAsync(Echo query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<int>.Success(query!.Value * 2));
}

public sealed class QueryDispatcherTests
{
    [Fact]
    public async Task SendAsync_WithARegisteredQuery_InvokesItsHandler()
    {
        var services = new ServiceCollection();
        services.AddQuery<Echo, int, EchoHandler>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var result = await dispatcher.SendAsync(new Echo(21), CancellationToken.None);

        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task SendAsync_WithAnUnregisteredQuery_Throws()
    {
        var services = new ServiceCollection();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var act = async () => await dispatcher.SendAsync(new Echo(1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Echo*");
    }
}
```

- [ ] **Step 2: Run**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: PASS — Task 7 already implemented the query side.

- [ ] **Step 3: Prove the test can fail**

A test that passes on first write has never been seen failing. Temporarily change `EchoHandler` to return `query.Value * 3`, run, confirm FAILURE, then restore and confirm PASS. Record both outputs. Do not commit the temporary change.

- [ ] **Step 4: Commit**

```bash
git add tests/Infrastructure.Tests/Messaging/QueryDispatcherTests.cs
git commit -m "test(infrastructure): cover the query dispatcher"
```

---

### Task 9: Validation and unit-of-work behaviors

**Files:**
- Create: `src/Infrastructure/Messaging/Behaviors.cs`
- Modify: `src/Infrastructure/Messaging/MessagingRegistration.cs` — wrap the command delegate
- Test: `tests/Infrastructure.Tests/Messaging/BehaviorTests.cs`

**Interfaces:**
- Consumes: `AddCommand` and `CommandDescriptor` (Task 7), `IUnitOfWork` (Task 5)
- Produces: command dispatch that validates then commits. Queries are unchanged — they get neither behavior.

Per spec §6.3: validation short-circuits to a failed `Result` before the handler runs; unit-of-work calls `SaveChangesAsync` exactly once on success. **This is what makes one command one transaction**, which the next plan's outbox depends on.

- [ ] **Step 1: Write the failing tests**

Create `tests/Infrastructure.Tests/Messaging/BehaviorTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.Messaging;

public sealed record Save(string Name) : ICommand<string>;

public sealed class SaveValidator : AbstractValidator<Save>
{
    public SaveValidator() => RuleFor(c => c.Name).NotEmpty();
}

public sealed class SaveHandler : ICommandHandler<Save, string>
{
    public bool WasCalled { get; private set; }

    public Task<Result<string>> HandleAsync(Save command, CancellationToken cancellationToken)
    {
        WasCalled = true;
        return Task.FromResult(Result<string>.Success(command!.Name));
    }
}

public sealed class FailingHandler : ICommandHandler<Save, string>
{
    public Task<Result<string>> HandleAsync(Save command, CancellationToken cancellationToken) =>
        Task.FromResult(Result<string>.Failure(
            new Error(ErrorKind.Conflict, "conflict", "nope")));
}

public sealed class BehaviorTests
{
    private static ServiceProvider Build<THandler>(IUnitOfWork unitOfWork, bool withValidator)
        where THandler : class, ICommandHandler<Save, string>
    {
        var services = new ServiceCollection();
        services.AddCommand<Save, string, THandler>();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddSingleton(unitOfWork);
        if (withValidator)
        {
            services.AddScoped<IValidator<Save>, SaveValidator>();
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SendAsync_WhenValidationFails_ReturnsValidationError()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<SaveHandler>(unitOfWork, withValidator: true);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var result = await dispatcher.SendAsync(new Save(""), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task SendAsync_WhenValidationFails_DoesNotSaveChanges()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<SaveHandler>(unitOfWork, withValidator: true);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await dispatcher.SendAsync(new Save(""), CancellationToken.None);

        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_OnSuccess_SavesChangesExactlyOnce()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<SaveHandler>(unitOfWork, withValidator: true);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await dispatcher.SendAsync(new Save("ok"), CancellationToken.None);

        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhenTheHandlerFails_DoesNotSaveChanges()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<FailingHandler>(unitOfWork, withValidator: true);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await dispatcher.SendAsync(new Save("ok"), CancellationToken.None);

        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithNoValidatorRegistered_StillRunsTheHandler()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        await using var provider = Build<SaveHandler>(unitOfWork, withValidator: false);
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var result = await dispatcher.SendAsync(new Save(""), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
```

That last test pins a deliberate decision: **absence of a validator is not an error.** Not every command needs one.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: FAIL — no behaviors run, so nothing validates and nothing commits.

- [ ] **Step 3: Implement the behaviors**

Create `src/Infrastructure/Messaging/Behaviors.cs`:

```csharp
using AiFramework.Application.Abstractions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Messaging;

internal static class Behaviors
{
    /// <summary>
    /// Runs the command's validator if one is registered, short-circuiting to a failed Result
    /// before the handler runs. No validator registered means no validation — absence is not
    /// an error, because not every command needs one.
    /// </summary>
    internal static async Task<Result<TResponse>?> ValidateAsync<TCommand, TResponse>(
        IServiceProvider sp, TCommand command, CancellationToken ct)
    {
        var validator = sp.GetService<IValidator<TCommand>>();
        if (validator is null)
        {
            return null;
        }

        var validation = await validator.ValidateAsync(command, ct).ConfigureAwait(false);
        if (validation.IsValid)
        {
            return null;
        }

        var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
        return Result<TResponse>.Failure(
            new Error(ErrorKind.Validation, "validation.failed", message));
    }

    /// <summary>
    /// Commits exactly once, and only when the command succeeded. Handlers never call
    /// SaveChangesAsync themselves — that is what makes one command one transaction.
    /// </summary>
    internal static async Task CommitAsync<TResponse>(
        IServiceProvider sp, Result<TResponse> result, CancellationToken ct)
    {
        if (!result.IsSuccess)
        {
            return;
        }

        var unitOfWork = sp.GetService<IUnitOfWork>();
        if (unitOfWork is not null)
        {
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 4: Wrap the command delegate**

In `src/Infrastructure/Messaging/MessagingRegistration.cs`, replace the body of `AddCommand`'s `InvokeAsync` with:

```csharp
        static async Task<object?> InvokeAsync(
            IServiceProvider sp, object command, CancellationToken ct)
        {
            var typed = (TCommand)command;

            var failed = await Behaviors.ValidateAsync<TCommand, TResponse>(sp, typed, ct)
                .ConfigureAwait(false);
            if (failed is not null)
            {
                return failed;
            }

            var handler = sp.GetRequiredService<ICommandHandler<TCommand, TResponse>>();
            var result = await handler.HandleAsync(typed, ct).ConfigureAwait(false);

            await Behaviors.CommitAsync(sp, result, ct).ConfigureAwait(false);

            return result;
        }
```

Leave `AddQuery` untouched — queries get neither behavior. A query that needs a transaction is a command.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: PASS, all five behavior tests.

- [ ] **Step 6: Commit**

```bash
git add src/Infrastructure/Messaging/ tests/Infrastructure.Tests/Messaging/BehaviorTests.cs
git commit -m "feat(infrastructure): add validation and unit-of-work behaviors"
```

---

### Task 10: The registration-completeness test

**Files:**
- Test: `tests/Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs`
- Create: `src/Infrastructure/InfrastructureRegistration.cs`

**Interfaces:**
- Consumes: `AddCommand`/`AddQuery` (Task 7), `PlaceOrder` (Task 5), `GetOrder` (Task 6)
- Produces: `IServiceCollection.AddMessaging()` registering every command and query in this plan

Spec §6.2 gives up compile-time safety for a missing registration; §11 buys it back with this test. **It is the only thing standing between a forgotten `AddCommand` and a 500 in production.** Reflection is fine here — it runs in a test, not on the request path.

- [ ] **Step 1: Write the failing test**

Create `tests/Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs`:

```csharp
using AiFramework.Application;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Tests.Messaging;

public sealed class RegistrationCompletenessTests
{
    private static IReadOnlyCollection<Type> Implementing(Type openGeneric) =>
        AssemblyMarker.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                && t.GetInterfaces().Any(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric))
            .ToArray();

    [Fact]
    public void AddMessaging_RegistersEveryCommandInTheApplicationAssembly()
    {
        var services = new ServiceCollection();
        services.AddMessaging();
        var registered = services
            .Where(d => d.ServiceType == typeof(CommandDescriptor))
            .Select(d => ((CommandDescriptor)d.ImplementationInstance!).CommandType)
            .ToHashSet();

        var unregistered = Implementing(typeof(ICommand<>)).Where(t => !registered.Contains(t));

        unregistered.Should().BeEmpty(
            "every ICommand<> needs an AddCommand<> call or it fails at runtime, not at compile time");
    }

    [Fact]
    public void AddMessaging_RegistersEveryQueryInTheApplicationAssembly()
    {
        var services = new ServiceCollection();
        services.AddMessaging();
        var registered = services
            .Where(d => d.ServiceType == typeof(QueryDescriptor))
            .Select(d => ((QueryDescriptor)d.ImplementationInstance!).QueryType)
            .ToHashSet();

        var unregistered = Implementing(typeof(IQuery<>)).Where(t => !registered.Contains(t));

        unregistered.Should().BeEmpty(
            "every IQuery<> needs an AddQuery<> call or it fails at runtime, not at compile time");
    }

    [Fact]
    public void EveryCommand_ImplementsICommandExactlyOnce()
    {
        var multiple = Implementing(typeof(ICommand<>))
            .Where(t => t.GetInterfaces().Count(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)) > 1);

        multiple.Should().BeEmpty(
            "TResponse is inferred from the argument; two ICommand<> interfaces make it ambiguous");
    }
}
```

Note the open-generic syntax `typeof(ICommand<>)`: the angle brackets stay and the type argument is omitted. `typeof(ICommand<)` does not compile.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: FAIL — `AddMessaging` does not exist.

- [ ] **Step 3: Implement the registration extension**

Create `src/Infrastructure/InfrastructureRegistration.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure;

public static class InfrastructureRegistration
{
    /// <summary>
    /// Every command and query in the Application assembly must appear here. The
    /// registration-completeness test in Infrastructure.Tests fails the build if one is missed.
    /// </summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();

        services.AddCommand<PlaceOrder, Guid, PlaceOrderHandler>();
        services.AddQuery<GetOrder, OrderView, GetOrderHandler>();

        return services;
    }
}
```

- [ ] **Step 4: Register the validators**

FluentValidation validators live in `Application`. Add to `AddMessaging`, before the `return`:

```csharp
        services.AddScoped<IValidator<PlaceOrder>, PlaceOrderValidator>();
```

with `using FluentValidation;` at the top. Registering each validator explicitly — rather than scanning — keeps the same no-reflection, greppable posture as `AddCommand`.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: PASS.

- [ ] **Step 6: Prove the completeness test can fail**

Temporarily comment out the `AddQuery<GetOrder, ...>` line, run the tests, confirm `AddMessaging_RegistersEveryQueryInTheApplicationAssembly` FAILS naming `GetOrder`, then restore and confirm PASS. Record both outputs. This test is the safety net for the whole registration approach — if it cannot fail, the approach has no net.

- [ ] **Step 7: Commit**

```bash
git add src/Infrastructure/InfrastructureRegistration.cs tests/Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs
git commit -m "feat(infrastructure): add AddMessaging with a registration-completeness test"
```

---

### Task 11: Persistence — DbContext, mapping, repository, migration

**Files:**
- Create: `src/Infrastructure/Persistence/AiFrameworkDbContext.cs`
- Create: `src/Infrastructure/Persistence/Configurations/OrderConfiguration.cs`
- Create: `src/Infrastructure/Persistence/OrderRepository.cs`
- Create: `src/Infrastructure/Persistence/UnitOfWork.cs`
- Create: `src/Infrastructure/SystemClock.cs`
- Create: `src/Infrastructure/Persistence/Migrations/` (generated)
- Test: `tests/Infrastructure.Tests/Persistence/PostgresFixture.cs`
- Test: `tests/Infrastructure.Tests/Persistence/OrderRepositoryTests.cs`

**Interfaces:**
- Consumes: `Order` (Task 4), `IOrderRepository`, `IUnitOfWork`, `IClock` (Task 5)
- Produces: `AiFrameworkDbContext`, `OrderRepository : IOrderRepository`, `UnitOfWork : IUnitOfWork`, `SystemClock : IClock`

- [ ] **Step 1: Write the Testcontainers fixture**

Create `tests/Infrastructure.Tests/Persistence/PostgresFixture.cs`:

```csharp
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace AiFramework.Infrastructure.Tests.Persistence;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public AiFrameworkDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AiFrameworkDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new AiFrameworkDbContext(options);
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
```

One container is started per collection, not per test — starting Postgres per test would make the suite unusably slow.

- [ ] **Step 2: Write the failing repository tests**

Create `tests/Infrastructure.Tests/Persistence/OrderRepositoryTests.cs`:

```csharp
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Persistence;

[Collection(nameof(PostgresCollection))]
public sealed class OrderRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddAsync_ThenSaveChanges_PersistsTheOrder()
    {
        var id = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            var repository = new OrderRepository(context);
            await repository.AddAsync(Order.Place(id, "SKU-1", 5, PlacedAt), CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify).GetAsync(id, CancellationToken.None);

        found.Should().NotBeNull();
        found!.Sku.Should().Be("SKU-1");
        found.Quantity.Should().Be(5);
        found.PlacedAt.Should().Be(PlacedAt);
    }

    [Fact]
    public async Task AddAsync_WithoutSaveChanges_PersistsNothing()
    {
        var id = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            var repository = new OrderRepository(context);
            await repository.AddAsync(Order.Place(id, "SKU-2", 1, PlacedAt), CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify).GetAsync(id, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenTheOrderIsMissing_ReturnsNull()
    {
        await using var context = fixture.CreateContext();

        var found = await new OrderRepository(context).GetAsync(Guid.NewGuid(), CancellationToken.None);

        found.Should().BeNull();
    }
}
```

The second test is the important one: it proves the repository does **not** commit on its own, which is the contract the unit-of-work behavior depends on.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: FAIL — none of the persistence types exist.

- [ ] **Step 4: Implement the DbContext and mapping**

Create `src/Infrastructure/Persistence/AiFrameworkDbContext.cs`:

```csharp
using AiFramework.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class AiFrameworkDbContext(DbContextOptions<AiFrameworkDbContext> options)
    : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AiFrameworkDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
```

Create `src/Infrastructure/Persistence/Configurations/OrderConfiguration.cs`:

```csharp
using AiFramework.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("orders");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Sku).IsRequired().HasMaxLength(64);
        builder.Property(o => o.Quantity).IsRequired();
        builder.Property(o => o.PlacedAt).IsRequired();
    }
}
```

All mapping lives here. `src/Infrastructure/CLAUDE.md`: never annotate a Domain type.

- [ ] **Step 5: Implement the repository, unit of work and clock**

Create `src/Infrastructure/Persistence/OrderRepository.cs`:

```csharp
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class OrderRepository(AiFrameworkDbContext context) : IOrderRepository
{
    public async Task AddAsync(Order order, CancellationToken cancellationToken) =>
        await context.Orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

    public Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
}
```

`AsNoTracking()` on the read, per `src/Infrastructure/CLAUDE.md`. The repository never calls `SaveChangesAsync`.

Create `src/Infrastructure/Persistence/UnitOfWork.cs`:

```csharp
using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Persistence;

public sealed class UnitOfWork(AiFrameworkDbContext context) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}
```

Create `src/Infrastructure/SystemClock.cs`:

```csharp
using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
```

- [ ] **Step 6: Add a design-time factory, then create the migration**

`dotnet ef` normally resolves the `DbContext` through the startup project's DI — but the Api composition root is not wired until Task 14, and making Task 11 depend on Task 14 would make the two tasks circular. A design-time factory removes the dependency entirely: `dotnet ef` uses it instead of the startup project.

Create `src/Infrastructure/Persistence/DesignTimeDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiFramework.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` at design time, so migrations can be generated without the Api
/// composition root. The connection string is never used to connect — EF needs a provider
/// registered to build the model, not a reachable database.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AiFrameworkDbContext>
{
    public AiFrameworkDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AiFrameworkDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only")
            .Options;

        return new AiFrameworkDbContext(options);
    }
}
```

That connection string is a design-time placeholder, not a secret and not a real target — `dotnet ef` needs a registered provider to build the model, not a reachable database.

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate \
  --project src/Infrastructure/AiFramework.Infrastructure.csproj \
  --output-dir Persistence/Migrations
```

Never hand-edit the generated migration — a hook blocks edits to applied ones. Review it: it should create an `orders` table with the four columns and a primary key.

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet`
Expected: PASS. The container starts, migrations apply, three repository tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Infrastructure/Persistence/ src/Infrastructure/SystemClock.cs tests/Infrastructure.Tests/Persistence/
git commit -m "feat(infrastructure): add EF Core persistence for Order"
```

---

### Task 12: Api DTOs, result mapping and the controller

**Files:**
- Create: `src/Api/Orders/OrderDtos.cs`
- Create: `src/Api/ResultExtensions.cs`
- Create: `src/Api/Orders/OrdersController.cs`

**Interfaces:**
- Consumes: `ICommandDispatcher`, `IQueryDispatcher` (Task 3), `PlaceOrder` (Task 5), `GetOrder`/`OrderView` (Task 6)
- Produces: `POST /api/orders` → 201 with the new id; `GET /api/orders/{id}` → 200 or 404

- [ ] **Step 1: Create the DTOs**

Create `src/Api/Orders/OrderDtos.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace AiFramework.Api.Orders;

/// <summary>DataAnnotations are legitimate here — this is the layer they belong to.</summary>
public sealed record PlaceOrderRequest
{
    [Required]
    [MaxLength(64)]
    public required string Sku { get; init; }

    [Range(1, int.MaxValue)]
    public required int Quantity { get; init; }
}

public sealed record OrderResponse
{
    public required Guid Id { get; init; }

    public required string Sku { get; init; }

    public required int Quantity { get; init; }

    public required DateTimeOffset PlacedAt { get; init; }
}
```

`required` + `init`, and separate types from the Domain entity — never return an entity directly.

- [ ] **Step 2: Create the result mapping**

Create `src/Api/ResultExtensions.cs`:

```csharp
using AiFramework.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api;

public static class ResultExtensions
{
    /// <summary>Maps a failed Result to RFC 9457 ProblemDetails with the right status code.</summary>
    public static ActionResult Problem<T>(this Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var status = result.Error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };

        return new ObjectResult(new ProblemDetails
        {
            Status = status,
            Title = result.Error.Code,
            Detail = result.Error.Message,
        })
        {
            StatusCode = status,
        };
    }
}
```

The `_ =>` arm is unreachable today but required: `ErrorKind` is an enum, so a future member would otherwise fall through silently.

- [ ] **Step 3: Create the controller**

Create `src/Api/Orders/OrdersController.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Orders;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(
    ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> Place(PlaceOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await commands.SendAsync(
            new PlaceOrder(request.Sku, request.Quantity), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? CreatedAtAction(nameof(Get), new { id = result.Value }, result.Value)
            : result.Problem();
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetOrder(id), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new OrderResponse
            {
                Id = result.Value.Id,
                Sku = result.Value.Sku,
                Quantity = result.Value.Quantity,
                PlacedAt = result.Value.PlacedAt,
            })
            : result.Problem();
    }
}
```

Bind, dispatch, map. No business logic, no EF, no `if` chains over domain state — and nothing from `Infrastructure`.

- [ ] **Step 4: Build**

Run: `dotnet build --nologo --verbosity quiet`
Expected: 0 warnings, 0 errors. The endpoints are not reachable yet — Task 14 wires DI.

- [ ] **Step 5: Commit**

```bash
git add src/Api/Orders/ src/Api/ResultExtensions.cs
git commit -m "feat(api): add the orders controller, DTOs and result mapping"
```

---

### Task 13: The global exception handler

**Files:**
- Create: `src/Api/GlobalExceptionHandler.cs`
- Test: covered end-to-end in Task 14

**Interfaces:**
- Consumes: `DomainException` (Task 4)
- Produces: `GlobalExceptionHandler : IExceptionHandler`

`src/Api/CLAUDE.md` mandates one `IExceptionHandler` mapping the domain hierarchy to RFC 9457 `ProblemDetails`. It receives the exception as a parameter, so it needs no `catch (Exception)` of its own — and could not have one, since CA1031 is a global error with no exemption for this file.

- [ ] **Step 1: Implement**

Create `src/Api/GlobalExceptionHandler.cs`:

```csharp
using AiFramework.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var (status, title, detail) = exception switch
        {
            DomainException domain => (
                StatusCodes.Status400BadRequest, "domain.invariant_violated", domain.Message),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An error occurred."),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception");
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = status, Title = title, Detail = detail },
            cancellationToken).ConfigureAwait(false);

        return true;
    }
}
```

The 500 branch logs the exception but returns a fixed message — the internal detail is never leaked to the caller.

- [ ] **Step 2: Build**

Run: `dotnet build --nologo --verbosity quiet`
Expected: 0 warnings, 0 errors. In particular, **no CA1031** — there is no `catch` anywhere in this file.

- [ ] **Step 3: Commit**

```bash
git add src/Api/GlobalExceptionHandler.cs
git commit -m "feat(api): add the global exception handler"
```

---

### Task 14: Composition root and end-to-end tests

**Files:**
- Modify: `src/Api/Program.cs`
- Modify: `src/Infrastructure/InfrastructureRegistration.cs` — add `AddInfrastructure`
- Modify: `src/Api/appsettings.json`
- Test: `tests/Api.IntegrationTests/Orders/OrdersEndpointTests.cs`
- Test: `tests/Api.IntegrationTests/ApiFactory.cs`
- Modify: `tests/Api.IntegrationTests/AiFramework.Api.IntegrationTests.csproj` — add Testcontainers

**Interfaces:**
- Consumes: everything from Tasks 5–13
- Produces: a running Api where `POST /api/orders` persists an order and `GET /api/orders/{id}` reads it back

- [ ] **Step 1: Add the Infrastructure DI extension**

Append to `src/Infrastructure/InfrastructureRegistration.cs`:

```csharp
    /// <summary>The single entry point Api calls. Api must not reach past this into Infrastructure.</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<AiFrameworkDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddSingleton<IClock, SystemClock>();

        return services.AddMessaging();
    }
```

with `using AiFramework.Infrastructure.Persistence;` and `using Microsoft.EntityFrameworkCore;` added.

- [ ] **Step 2: Wire the composition root**

Replace the service registrations in `src/Api/Program.cs`, keeping the existing `/health` endpoint and the `public partial class Program` declaration exactly as they are:

```csharp
using AiFramework.Api;
using AiFramework.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddInfrastructure(
    builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured."));

var app = builder.Build();

app.UseExceptionHandler();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();
```

The `?? throw` is deliberate: a missing connection string should fail at startup, not on the first request.

- [ ] **Step 3: Add an empty connection string placeholder**

In `src/Api/appsettings.json`, add:

```json
  "ConnectionStrings": {
    "Default": ""
  }
```

Empty, not a real value. **No secrets in `appsettings*.json`** — the real string comes from `dotnet user-secrets` or an environment variable. Run `dotnet user-secrets init --project src/Api/AiFramework.Api.csproj` so the developer has somewhere to put it, and report the generated `UserSecretsId`.

- [ ] **Step 4: Add Testcontainers to the integration test project**

```bash
dotnet add tests/Api.IntegrationTests/AiFramework.Api.IntegrationTests.csproj package Testcontainers.PostgreSql
```

Pin the same exact version used in Task 1.

- [ ] **Step 5: Write the test factory**

Create `tests/Api.IntegrationTests/ApiFactory.cs`:

```csharp
using AiFramework.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace AiFramework.Api.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    // The container must start BEFORE anything touches Services: the first access to
    // Services builds the host, which runs ConfigureWebHost, which reads the container's
    // connection string. Reversing these two lines fails with a connection error.
    async Task IAsyncLifetime.InitializeAsync()
    {
        await _container.StartAsync();
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>()
            .Database.MigrateAsync();
    }

    // Explicit interface implementation: WebApplicationFactory already exposes a
    // ValueTask DisposeAsync() from IAsyncDisposable, so declaring xUnit's
    // Task DisposeAsync() implicitly would hide it and leak the host.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(
                d => d.ServiceType == typeof(DbContextOptions<AiFrameworkDbContext>));
            services.Remove(descriptor);
            services.AddDbContext<AiFrameworkDbContext>(
                options => options.UseNpgsql(_container.GetConnectionString()));
        });
    }
}
```

- [ ] **Step 6: Write the failing end-to-end tests**

Create `tests/Api.IntegrationTests/Orders/OrdersEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Orders;

public sealed class OrdersEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task PostOrders_WithAValidRequest_Returns201()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-1", Quantity = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task PostOrders_ThenGet_ReturnsTheOrder()
    {
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-2", Quantity = 7 });
        var id = await created.Content.ReadFromJsonAsync<Guid>();

        var response = await client.GetFromJsonAsync<OrderResponseDto>($"/api/orders/{id}");

        response.Should().NotBeNull();
        response!.Sku.Should().Be("SKU-2");
        response.Quantity.Should().Be(7);
    }

    [Fact]
    public async Task PostOrders_WithZeroQuantity_Returns400()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-3", Quantity = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetOrders_WithAnUnknownId_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public sealed record OrderResponseDto(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt);
}
```

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet test --nologo --verbosity quiet`
Expected: the whole suite passes. Report the exact total.

The second test is the one that proves the plan's thesis: a command dispatched through the behavior chain actually committed, because a *separate* HTTP request in a *different* scope can read the row back.

- [ ] **Step 8: Commit**

```bash
git add src/Api/ src/Infrastructure/InfrastructureRegistration.cs tests/Api.IntegrationTests/
git commit -m "feat(api): wire the composition root and add end-to-end order tests"
```

---

### Task 15: Documentation and ADR 0003

**Files:**
- Modify: `src/Application/CLAUDE.md`
- Modify: `src/Api/CLAUDE.md`
- Modify: `src/Infrastructure/CLAUDE.md`
- Modify: `tests/CLAUDE.md`
- Create: `docs/adr/0003-in-process-messaging-without-mediatr.md`

**Interfaces:**
- Consumes: everything built in Tasks 2–14
- Produces: documentation matching the code

- [ ] **Step 1: Update `src/Application/CLAUDE.md`**

Add to the "Belongs here" section: `ICommand<T>` / `IQuery<T>` and their handlers, and add these rules:

- A command or query implements `ICommand<T>` / `IQuery<T>` **exactly once**. `TResponse` is inferred from the argument at the call site; a second interface makes that ambiguous and breaks every caller.
- **Handlers never call `SaveChangesAsync`.** The unit-of-work behavior commits exactly once after a successful command. A handler that commits turns one command into two transactions.
- Never inject a `DbContext`. Depend on the port.

- [ ] **Step 2: Update `src/Api/CLAUDE.md`**

Add: controllers bind → `SendAsync` → map the `Result`. `AddCommand<...>()`, `AddQuery<...>()` and `AddInfrastructure(...)` belong in the composition root only. Confirm the `IExceptionHandler` table still matches `GlobalExceptionHandler` — it now maps `DomainException` → 400 and everything else → 500.

- [ ] **Step 3: Update `src/Infrastructure/CLAUDE.md`**

Add: the dispatchers, descriptors and behaviors live in `Messaging/`; EF Core lives in `Persistence/`; `AddInfrastructure` is the single entry point `Api` calls, and `Api` must not reach past it.

- [ ] **Step 4: Update `tests/CLAUDE.md`**

Add: `Infrastructure.Tests` uses a shared `PostgresFixture` via `[Collection(nameof(PostgresCollection))]` — one container per collection, never per test. Note that the registration-completeness test is what replaces compile-time safety for `AddCommand`, so it must never be deleted.

- [ ] **Step 5: Write ADR 0003**

Create `docs/adr/0003-in-process-messaging-without-mediatr.md` recording the decision from the spec: two seams (direct dispatch for requests, channel dispatch for events), why not MediatR, the registration-based dispatcher, and the rejected alternatives (direct handler injection needing Scrutor; explicit type arguments; cached reflection). Follow the format of ADR 0001 and 0002: Context, Decision, Consequences, Alternatives considered. Status: Accepted.

Record honestly that this plan implements only the request half; the event half follows in the next plan.

- [ ] **Step 6: Verify and commit**

```bash
dotnet build --nologo --verbosity quiet
dotnet test --nologo --verbosity quiet
git add src/Application/CLAUDE.md src/Api/CLAUDE.md src/Infrastructure/CLAUDE.md tests/CLAUDE.md docs/adr/0003-in-process-messaging-without-mediatr.md
git commit -m "docs: record the request path and add ADR 0003"
```

---

## Definition of done

- `dotnet build` clean, 0 warnings across 8 projects.
- `dotnet test` passes, including Testcontainers-backed repository and end-to-end tests.
- `POST /api/orders` persists an order in one transaction; a later `GET` in a different scope reads it back.
- A missing `AddCommand`/`AddQuery` registration fails the registration-completeness test.
- Every suppression is argued at its site and recorded in `.editorconfig` (four repo-wide
  `severity = none` rules, each with a comment explaining why) or as a local `#pragma` with a
  justification comment — never bare. Zero `catch (Exception)`.
- `Domain` still references no banned namespace — the architecture test proves EF Core reached `Infrastructure` only.
- ADR 0003 records the decision; the four `CLAUDE.md` files match the code.

## Deferred to the next plan

- **Domain events, the outbox, the channel and the worker pool** — spec §7–§10.
- **`Entity` base class with `Raise()`** — `Order` gains it when there are events to hold.
- **`Directory.Packages.props`** to end duplicated package versions across test projects.
- **Idempotency** — meaningless until at-least-once delivery exists.
- **`.claude/commands/feature.md`** — spec §12 wants `/feature` to scaffold a command, handler, `AddCommand` line, event record and idempotency test. Half of that shape does not exist until the next plan, so updating it now would teach a pattern only half-built.
