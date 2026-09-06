# Order Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scope every order to the user who placed it, so a signed-in caller can only ever read and write their own orders.

**Architecture:** `Order` gains a `UserId` set once at `Place`. Handlers learn who is calling through a new `ICurrentUser` port implemented in `Api` over `IHttpContextAccessor`. Enforcement lives in `IOrderRepository`, whose read methods take an owner id — there is no overload that can return another user's order, so forgetting the filter is a compile error rather than a data leak.

**Tech Stack:** .NET 10 (net10.0), ASP.NET Core cookie auth, EF Core + Npgsql, xUnit + FluentAssertions + NSubstitute, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-06-order-ownership-design.md`

## Global Constraints

- **Warnings are errors** — compiler, analyzers and build (`Directory.Build.props`). Never suppress without a justification comment above a narrow `#pragma warning disable`/`restore` pair.
- **Nullable is enabled.** A missing null check does not compile.
- **Never `catch (Exception)`.** CA1031 is an error.
- **`required` keyword in Domain, never `[Required]`.** DataAnnotations belong on Api DTOs.
- **Never hand-edit an *applied* EF migration.** Editing a migration you just generated and have not yet applied is fine and this plan does exactly that.
- **Handlers never call `SaveChangesAsync`** — the unit-of-work behavior commits once after a successful command.
- **Application must not reference** `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, or any `AiFramework.Infrastructure`/`.Api` namespace. The dependency hook blocks the edit.
- **One Testcontainer per collection, never per class.** New database test classes join `PostgresCollection` (Infrastructure.Tests) or `ApiFactoryCollection` (Api.IntegrationTests).
- **Error message for a missing session** is exactly `"That session is no longer valid."` with code `auth.failed` and `ErrorKind.Unauthorized` — matching `ChangePasswordHandler`.
- Dev database connection string for `dotnet ef` (it cannot see user-secrets):
  `Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres`
- Branch: `feat/order-ownership`, already created, spec already committed.

---

## File Structure

**Created:**
- `src/Api/Auth/CurrentUser.cs` — reads the caller out of the cookie's claims. The only place `NameIdentifier` is parsed.
- `tests/Api.IntegrationTests/Auth/CurrentUserTests.cs` — unit tests for that parse, no host or database.
- `src/Infrastructure/Persistence/Migrations/<timestamp>_AddOrderOwner.cs` — generated, then edited.
- `docs/adr/0007-orders-belong-to-the-user-who-placed-them.md`

**Modified:**
- `src/Application/Abstractions/Ports.cs` — add `ICurrentUser`.
- `src/Application/Orders/IOrderRepository.cs` — owner id on both reads.
- `src/Application/Orders/PlaceOrder.cs`, `GetOrder.cs`, `GetOrders.cs` — inject `ICurrentUser`.
- `src/Domain/Orders/Order.cs` — `UserId` property, `Place` signature, guard.
- `src/Infrastructure/Persistence/OrderRepository.cs` — filter both reads.
- `src/Infrastructure/Persistence/Configurations/OrderConfiguration.cs` — required property, index swap.
- `src/Api/Program.cs` — register the port.
- `src/Api/Auth/AuthController.cs` — drop the private claim parser, inject the port.
- Test files across all four test projects — `Order.Place` call sites, plus the new ownership tests.

**Deliberately unchanged:** `src/Domain/Orders/OrderPlaced.cs`, every Api DTO, `openapi/AiFramework.Api.json`, `frontend/**`.

---

### Task 1: The `ICurrentUser` seam

Introduces the port and its implementation, and makes `AuthController` the first consumer. No behavior changes — the claim is parsed in one place instead of two.

**Files:**
- Modify: `src/Application/Abstractions/Ports.cs`
- Create: `src/Api/Auth/CurrentUser.cs`
- Modify: `src/Api/Program.cs`
- Modify: `src/Api/Auth/AuthController.cs`
- Test: `tests/Api.IntegrationTests/Auth/CurrentUserTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `AiFramework.Application.Abstractions.ICurrentUser` with `Guid? Id { get; }`; `AiFramework.Api.Auth.CurrentUser(IHttpContextAccessor accessor)` implementing it.

- [ ] **Step 1: Write the failing test**

Create `tests/Api.IntegrationTests/Auth/CurrentUserTests.cs`:

```csharp
using System.Security.Claims;
using AiFramework.Api.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace AiFramework.Api.IntegrationTests.Auth;

/// <summary>
/// A plain unit test, deliberately outside ApiFactoryCollection: CurrentUser needs an
/// IHttpContextAccessor, not a host and not a database.
/// </summary>
public sealed class CurrentUserTests
{
    [Fact]
    public void Id_WhenThereIsNoHttpContext_IsNull()
    {
        // Background work - the outbox pumps - resolves scopes with no request. This is the
        // case that makes the property nullable rather than throwing.
        var currentUser = new CurrentUser(new HttpContextAccessor());

        currentUser.Id.Should().BeNull();
    }

    [Fact]
    public void Id_WhenSignedOut_IsNull()
    {
        var currentUser = new CurrentUser(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        currentUser.Id.Should().BeNull();
    }

    [Fact]
    public void Id_WhenTheClaimIsPresent_IsTheUserId()
    {
        var userId = Guid.NewGuid();

        var currentUser = new CurrentUser(
            AccessorFor(new Claim(ClaimTypes.NameIdentifier, userId.ToString())));

        currentUser.Id.Should().Be(userId);
    }

    [Fact]
    public void Id_WhenTheClaimIsNotAGuid_IsNull()
    {
        // IssueCookieAsync can only mint a Guid today, but a cookie in an older format must
        // not throw its way out of a property read on every request.
        var currentUser = new CurrentUser(
            AccessorFor(new Claim(ClaimTypes.NameIdentifier, "not-a-guid")));

        currentUser.Id.Should().BeNull();
    }

    private static IHttpContextAccessor AccessorFor(Claim claim) =>
        new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([claim], "test")),
            },
        };
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~CurrentUserTests"`
Expected: FAIL to build — `CS0246: The type or namespace name 'CurrentUser' could not be found`.

- [ ] **Step 3: Add the port**

In `src/Application/Abstractions/Ports.cs`, after `IClock` and before `IUnitOfWork`:

```csharp
/// <summary>
/// The signed-in caller, or null when there is no session. Nullable rather than throwing:
/// the outbox pumps resolve scopes with no HTTP context at all, and a handler that returns
/// ErrorKind.Unauthorized produces a 401 where a throw would produce a 500.
/// </summary>
public interface ICurrentUser
{
    public Guid? Id { get; }
}
```

- [ ] **Step 4: Add the implementation**

Create `src/Api/Auth/CurrentUser.cs`:

```csharp
using System.Security.Claims;
using AiFramework.Application.Abstractions;

namespace AiFramework.Api.Auth;

/// <summary>
/// The caller, read from the cookie's claims. The only place NameIdentifier is parsed —
/// AuthController used to do it privately, and a second parser is a second thing to get wrong.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? Id =>
        Guid.TryParse(
            accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;
}
```

- [ ] **Step 5: Register it**

In `src/Api/Program.cs`, add `using AiFramework.Api.Auth;` and `using AiFramework.Application.Abstractions;` to the usings, then immediately after `builder.Services.AddAuthorization();`:

```csharp
// The caller, as an Application port. HttpContextAccessor is what makes the claims reachable
// from a handler; scoped because "who is calling" is per-request.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~CurrentUserTests"`
Expected: PASS, 4 tests.

- [ ] **Step 7: Make `AuthController` use the port**

In `src/Api/Auth/AuthController.cs`:

Change the constructor to:

```csharp
public sealed class AuthController(
    ICommandDispatcher commands, IQueryDispatcher queries, ICurrentUser currentUser) : ControllerBase
```

Delete the private helper at the bottom of the class:

```csharp
    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
```

Replace both call sites — in `Me` and in `ChangeOwnPassword` — changing `CurrentUserId()` to `currentUser.Id`:

```csharp
        if (currentUser.Id is not { } userId)
        {
            return Unauthorized();
        }
```

Keep `using System.Security.Claims;` — `IssueCookieAsync` still uses `Claim`, `ClaimTypes` and `ClaimsIdentity`.

- [ ] **Step 8: Run the auth suite**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~Auth"`
Expected: PASS — `AuthEndpointTests` exercises `/api/auth/me` and `/api/auth/change-password`, which are the two paths that just changed how they find the caller.

- [ ] **Step 9: Commit**

```bash
git add src/Application/Abstractions/Ports.cs src/Api/Auth/CurrentUser.cs src/Api/Program.cs src/Api/Auth/AuthController.cs tests/Api.IntegrationTests/Auth/CurrentUserTests.cs
git commit -m "feat(auth): an ICurrentUser port, and one place that parses the claim"
```

---

### Task 2: An order records who placed it

Domain, the write handler, and the schema together. They cannot be separated: adding `UserId` to the entity without the column breaks every test that saves an order, and `Order.Place`'s new parameter breaks every call site at compile time.

The index swap ships in this task's migration too, even though nothing filters on the owner until Task 3 — one migration is better than two, and an unused index is harmless.

**Files:**
- Modify: `src/Domain/Orders/Order.cs`
- Modify: `src/Application/Orders/PlaceOrder.cs`
- Modify: `src/Infrastructure/Persistence/Configurations/OrderConfiguration.cs`
- Create: `src/Infrastructure/Persistence/Migrations/<timestamp>_AddOrderOwner.cs` (generated)
- Test: `tests/Domain.Tests/Orders/OrderTests.cs`, `tests/Application.Tests/Orders/PlaceOrderHandlerTests.cs`
- Modify (call sites only): `tests/Application.Tests/Orders/GetOrderHandlerTests.cs`, `tests/Application.Tests/Orders/GetOrdersHandlerTests.cs`, `tests/Infrastructure.Tests/Persistence/OrderRepositoryTests.cs`, `tests/Infrastructure.Tests/Persistence/OrderRepositoryPagingTests.cs`, `tests/Infrastructure.Tests/Outbox/OutboxAtomicityTests.cs`, `tests/Api.IntegrationTests/EventPath/WolverineOutboxAtomicityTests.cs`

**Interfaces:**
- Consumes: `ICurrentUser` from Task 1.
- Produces: `Order.UserId` (`Guid`, private setter); `Order.Place(Guid id, Guid userId, string sku, int quantity, DateTimeOffset placedAt)`; `PlaceOrderHandler(IOrderRepository orders, IClock clock, ICurrentUser currentUser)`.

- [ ] **Step 1: Write the failing Domain tests**

In `tests/Domain.Tests/Orders/OrderTests.cs`, replace `Place_WithValidDetails_SetsTheProperties` and add the guard test:

```csharp
    [Fact]
    public void Place_WithValidDetails_SetsTheProperties()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var order = Order.Place(id, userId, "SKU-1", 3, PlacedAt);

        order.Id.Should().Be(id);
        order.UserId.Should().Be(userId);
        order.Sku.Should().Be("SKU-1");
        order.Quantity.Should().Be(3);
        order.PlacedAt.Should().Be(PlacedAt);
    }

    [Fact]
    public void Place_WithNoUser_Throws()
    {
        var act = () => Order.Place(Guid.NewGuid(), Guid.Empty, "SKU-1", 1, PlacedAt);

        act.Should().Throw<DomainException>().WithMessage("*user*");
    }
```

Then add `Guid.NewGuid(),` as the second argument to the three remaining `Order.Place(` calls in this file — in `Place_WithNonPositiveQuantity_Throws`, `Place_WithBlankSku_Throws`, and `Place_RaisesOrderPlaced`. `Place_RaisesOrderPlaced`'s assertion stays exactly as it is: `OrderPlaced` did not change.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Domain.Tests --filter "FullyQualifiedName~OrderTests"`
Expected: FAIL to build — `CS1501: No overload for method 'Place' takes 5 arguments`.

- [ ] **Step 3: Add the owner to the aggregate**

In `src/Domain/Orders/Order.cs`, take `userId` in the private constructor and assign it, add the property, and guard it in `Place`:

```csharp
    private Order(Guid id, Guid userId, string sku, int quantity, DateTimeOffset placedAt)
    {
        Id = id;
        UserId = userId;
        Sku = sku;
        Quantity = quantity;
        PlacedAt = placedAt;
    }

    public Guid Id { get; private set; }

    /// <summary>The user who placed it. Set once; an order is never reassigned.</summary>
    public Guid UserId { get; private set; }
```

```csharp
    public static Order Place(
        Guid id, Guid userId, string sku, int quantity, DateTimeOffset placedAt)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException("An order needs a user.");
        }

        // ... the existing sku and quantity guards, unchanged ...

        var order = new Order(id, userId, sku, quantity, placedAt);
        order.Raise(new OrderPlaced(id, sku, quantity));
        return order;
    }
```

`userId` goes second, beside `id`: both are identity rather than order content.

- [ ] **Step 4: Run the Domain tests**

Run: `dotnet test tests/Domain.Tests --filter "FullyQualifiedName~OrderTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing Application tests**

Replace `tests/Application.Tests/Orders/PlaceOrderHandlerTests.cs` in full:

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
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public PlaceOrderHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _currentUser.Id.Returns(UserId);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_AddsTheOrder()
    {
        var handler = new PlaceOrderHandler(_repository, _clock, _currentUser);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<Order>(o => o.Sku == "SKU-1" && o.Quantity == 2 && o.PlacedAt == Now),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_RecordsThePlacingUser()
    {
        var handler = new PlaceOrderHandler(_repository, _clock, _currentUser);

        await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        await _repository.Received(1).AddAsync(
            Arg.Is<Order>(o => o.UserId == UserId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsTheNewOrderId()
    {
        var handler = new PlaceOrderHandler(_repository, _clock, _currentUser);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.Value.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithNoCurrentUser_ReturnsUnauthorizedAndWritesNothing()
    {
        _currentUser.Id.Returns((Guid?)null);
        var handler = new PlaceOrderHandler(_repository, _clock, _currentUser);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        await _repository.DidNotReceive().AddAsync(
            Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 6: Run to verify it fails**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~PlaceOrderHandlerTests"`
Expected: FAIL to build — `CS1729: 'PlaceOrderHandler' does not contain a constructor that takes 3 arguments`.

- [ ] **Step 7: Take the user in the handler**

In `src/Application/Orders/PlaceOrder.cs`, leave the `PlaceOrder` record and its validator untouched — the command gains no `UserId` field, deliberately — and change the handler:

```csharp
public sealed class PlaceOrderHandler(
    IOrderRepository orders, IClock clock, ICurrentUser currentUser)
    : ICommandHandler<PlaceOrder, Guid>
{
    public async Task<Result<Guid>> HandleAsync(PlaceOrder command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<Guid>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        var order = Order.Place(
            Guid.NewGuid(), userId, command.Sku, command.Quantity, clock.UtcNow);

        await orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

        return Result.Success(order.Id);
    }
}
```

- [ ] **Step 8: Run to verify it passes**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~PlaceOrderHandlerTests"`
Expected: PASS, 4 tests.

- [ ] **Step 9: Fix the remaining `Order.Place` call sites**

The compiler will list them. Add a `Guid.NewGuid()` second argument to each, except where the test needs a known owner:

- `tests/Application.Tests/Orders/GetOrderHandlerTests.cs:20` — `Order.Place(id, Guid.NewGuid(), "SKU-1", 4, PlacedAt)`
- `tests/Application.Tests/Orders/GetOrdersHandlerTests.cs` lines 19, 32, 90, 94, 123, 127 — insert `Guid.NewGuid(),` after the first argument in each
- `tests/Infrastructure.Tests/Outbox/OutboxAtomicityTests.cs` lines 20, 40, 49, 64 — same
- `tests/Api.IntegrationTests/EventPath/WolverineOutboxAtomicityTests.cs:68` — same

For `tests/Infrastructure.Tests/Persistence/OrderRepositoryPagingTests.cs`, add a class-level owner and thread it through the seed helper, so every row this class writes belongs to one user:

```csharp
    /// <summary>
    /// One owner for the whole class. Rows written by other test classes now belong to other
    /// users and are filtered out by ListAsync, but the per-test TopOf(...) windows still do
    /// the work of keeping these tests from seeing each other's rows.
    /// </summary>
    private static readonly Guid Owner = Guid.NewGuid();

    private Task<Guid> SeedAsync(string sku, DateTimeOffset placedAt) =>
        SeedAsync(sku, placedAt, Guid.NewGuid());

    private Task<Guid> SeedAsync(string sku, DateTimeOffset placedAt, Guid id) =>
        SeedAsync(Owner, sku, placedAt, id);

    private async Task<Guid> SeedAsync(Guid owner, string sku, DateTimeOffset placedAt, Guid id)
    {
        await using var context = fixture.CreateContext();
        await new OrderRepository(context).AddAsync(
            Order.Place(id, owner, sku, 1, placedAt), CancellationToken.None);
        await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        return id;
    }
```

For `tests/Infrastructure.Tests/Persistence/OrderRepositoryTests.cs`, give each test its own owner inline — `Order.Place(id, Guid.NewGuid(), "SKU-1", 5, PlacedAt)` at line 19 and `Order.Place(id, Guid.NewGuid(), "SKU-2", 1, PlacedAt)` at line 39.

- [ ] **Step 10: Map the column and swap the index**

In `src/Infrastructure/Persistence/Configurations/OrderConfiguration.cs`, add the property after `HasKey` and replace the index:

```csharp
        builder.Property(o => o.UserId).IsRequired();
```

```csharp
        // The list endpoint filters by owner and orders by (PlacedAt DESC, Id DESC). The owner
        // is the leading column because it is an equality predicate; the previous
        // IX_Orders_PlacedAt_Id_Desc cannot serve this query and is dropped.
        builder.HasIndex(o => new { o.UserId, o.PlacedAt, o.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_Orders_UserId_PlacedAt_Id_Desc");
```

- [ ] **Step 11: Generate the migration**

Make sure the dev database is up (`docker compose up -d --wait`), then:

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef migrations add AddOrderOwner --project src/Infrastructure --startup-project src/Infrastructure
```

- [ ] **Step 12: Edit the generated migration**

Open the new `src/Infrastructure/Persistence/Migrations/<timestamp>_AddOrderOwner.cs` and make two changes to `Up`:

1. Add this as the **first** statement, before the `DropIndex`:

```csharp
            // The column is NOT NULL and there is no owner to infer for rows that predate it.
            // These are throwaway development rows - see the spec - and nothing is deployed.
            // Down cannot restore them; that is accepted.
            migrationBuilder.Sql("DELETE FROM orders;");
```

2. Delete the `defaultValue:` argument EF generated on the `AddColumn<Guid>` call. The table is empty by the time it runs, so Postgres accepts `NOT NULL` without one — and leaving it in would let a future insert silently default an order to `Guid.Empty`. The call should end up as:

```csharp
            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "orders",
                type: "uuid",
                nullable: false);
```

Leave `Down` as generated.

- [ ] **Step 13: Apply it and run the database suites**

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
dotnet test tests/Infrastructure.Tests
dotnet test tests/Domain.Tests tests/Application.Tests
```

Expected: PASS. `Api.IntegrationTests` is not expected to be green yet only if something else broke — run it too and fix anything that did:

```bash
dotnet test tests/Api.IntegrationTests
```

- [ ] **Step 14: Commit**

```bash
git add src/Domain/Orders/Order.cs src/Application/Orders/PlaceOrder.cs src/Infrastructure/Persistence tests/
git commit -m "feat(orders): an order records the user who placed it"
```

---

### Task 3: Reads are scoped to the owner

The security fix. After this task the repository cannot return an order its caller does not own.

**Files:**
- Modify: `src/Application/Orders/IOrderRepository.cs`
- Modify: `src/Application/Orders/GetOrder.cs`, `src/Application/Orders/GetOrders.cs`
- Modify: `src/Infrastructure/Persistence/OrderRepository.cs`
- Test: `tests/Application.Tests/Orders/GetOrderHandlerTests.cs`, `tests/Application.Tests/Orders/GetOrdersHandlerTests.cs`, `tests/Infrastructure.Tests/Persistence/OrderRepositoryTests.cs`, `tests/Infrastructure.Tests/Persistence/OrderRepositoryPagingTests.cs`

**Interfaces:**
- Consumes: `ICurrentUser` (Task 1), `Order.UserId` (Task 2).
- Produces:
  - `Task<Order?> IOrderRepository.GetAsync(Guid id, Guid ownerId, CancellationToken cancellationToken)`
  - `Task<IReadOnlyList<Order>> IOrderRepository.ListAsync(Guid ownerId, int limit, (DateTimeOffset PlacedAt, Guid Id)? after, CancellationToken cancellationToken)`
  - `GetOrderHandler(IOrderRepository orders, ICurrentUser currentUser)`, `GetOrdersHandler(IOrderRepository orders, ICurrentUser currentUser)`

`AddAsync` is unchanged — the owner arrives on the entity.

- [ ] **Step 1: Write the failing Infrastructure tests**

These are the ones whose absence let the bug ship. In `tests/Infrastructure.Tests/Persistence/OrderRepositoryTests.cs`, update the three existing `GetAsync` calls to pass an owner, and add the cross-user test. The first test becomes:

```csharp
    [Fact]
    public async Task AddAsync_ThenSaveChanges_PersistsTheOrder()
    {
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            var repository = new OrderRepository(context);
            await repository.AddAsync(
                Order.Place(id, owner, "SKU-1", 5, PlacedAt), CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify).GetAsync(id, owner, CancellationToken.None);

        found.Should().NotBeNull();
        found.Sku.Should().Be("SKU-1");
        found.Quantity.Should().Be(5);
        found.PlacedAt.Should().Be(PlacedAt);
    }
```

`AddAsync_WithoutSaveChanges_PersistsNothing` and `GetAsync_WhenTheOrderIsMissing_ReturnsNull` take the same treatment — a local `owner` threaded into both `Place` and `GetAsync`. Then add:

```csharp
    [Fact]
    public async Task GetAsync_WhenTheOrderBelongsToAnotherUser_ReturnsNull()
    {
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            await new OrderRepository(context).AddAsync(
                Order.Place(id, owner, "SKU-PRIVATE", 1, PlacedAt), CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify).GetAsync(id, stranger, CancellationToken.None);

        // Null, not the order: indistinguishable from an id that was never issued, which is
        // what makes the endpoint answer 404 rather than 403.
        found.Should().BeNull();
    }
```

In `tests/Infrastructure.Tests/Persistence/OrderRepositoryPagingTests.cs`, add `Owner` as the first argument to every `ListAsync` call in the existing five tests, then add the interleaving test:

```csharp
    /// <summary>
    /// Interleaved in time, not merely present: a stranger's rows sitting BETWEEN the owner's
    /// mean a missing filter would not just add rows, it would change which rows land on which
    /// page and where the cursor points. A test that seeds the stranger's rows outside the
    /// owner's window would pass against a broken filter.
    /// </summary>
    [Fact]
    public async Task ListAsync_WithAnotherUsersOrdersInterleaved_ReturnsOnlyTheOwners()
    {
        var stranger = Guid.NewGuid();
        var third = await SeedAsync("SKU-OWN-3", Base.AddMinutes(61));
        await SeedAsync(stranger, "SKU-STR-A", Base.AddMinutes(62), Guid.NewGuid());
        var second = await SeedAsync("SKU-OWN-2", Base.AddMinutes(63));
        await SeedAsync(stranger, "SKU-STR-B", Base.AddMinutes(64), Guid.NewGuid());
        var first = await SeedAsync("SKU-OWN-1", Base.AddMinutes(65));

        await using var context = fixture.CreateContext();
        var repository = new OrderRepository(context);

        var pageOne = await repository.ListAsync(Owner, 2, TopOf(70), CancellationToken.None);
        pageOne.Select(o => o.Id).Should().Equal(first, second);

        var cursorRow = pageOne[^1];
        var pageTwo = await repository.ListAsync(
            Owner, 2, (cursorRow.PlacedAt, cursorRow.Id), CancellationToken.None);

        pageTwo.Select(o => o.Id).Should().Equal(third);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~OrderRepository"`
Expected: FAIL to build — `CS1501: No overload for method 'GetAsync' takes 3 arguments`.

- [ ] **Step 3: Widen the port**

`src/Application/Orders/IOrderRepository.cs`:

```csharp
public interface IOrderRepository
{
    public Task AddAsync(Order order, CancellationToken cancellationToken);

    /// <summary>
    /// Scoped to the owner by signature: there is no overload that returns another user's
    /// order, so a caller cannot forget to filter. An order the caller does not own comes
    /// back null, exactly like one that does not exist.
    /// </summary>
    public Task<Order?> GetAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);

    public Task<IReadOnlyList<Order>> ListAsync(
        Guid ownerId,
        int limit,
        (DateTimeOffset PlacedAt, Guid Id)? after,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Filter in the repository**

In `src/Infrastructure/Persistence/OrderRepository.cs`:

```csharp
    public Task<Order?> GetAsync(Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        context.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && o.UserId == ownerId, cancellationToken);

    public async Task<IReadOnlyList<Order>> ListAsync(
        Guid ownerId,
        int limit,
        (DateTimeOffset PlacedAt, Guid Id)? after,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // The owner filter goes first, and stays in SQL: it is the leading column of
        // IX_Orders_UserId_PlacedAt_Id_Desc, and filtering a fetched page in memory would
        // break the keyset - short pages, wrong cursors, skipped rows.
        var query = context.Orders.AsNoTracking().Where(o => o.UserId == ownerId);

        // ... the existing keyset `if (after is { } cursor)` block and the
        // OrderByDescending/ThenByDescending/Take tail, unchanged ...
    }
```

- [ ] **Step 5: Run the Infrastructure tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~OrderRepository"`
Expected: PASS, including the two new tests.

- [ ] **Step 6: Write the failing Application tests**

In `tests/Application.Tests/Orders/GetOrderHandlerTests.cs`, add the substitute and rewrite the file's tests to pass the owner:

```csharp
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public GetOrderHandlerTests() => _currentUser.Id.Returns(UserId);

    [Fact]
    public async Task HandleAsync_WhenTheOrderExists_ReturnsTheView()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id, UserId, Arg.Any<CancellationToken>())
            .Returns(Order.Place(id, UserId, "SKU-1", 4, PlacedAt));
        var handler = new GetOrderHandler(_repository, _currentUser);

        var result = await handler.HandleAsync(new GetOrder(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new OrderView(id, "SKU-1", 4, PlacedAt));
    }

    [Fact]
    public async Task HandleAsync_WhenTheOrderIsMissing_ReturnsNotFound()
    {
        _repository.GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);
        var handler = new GetOrderHandler(_repository, _currentUser);

        var result = await handler.HandleAsync(new GetOrder(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task HandleAsync_WhenQueried_AsksOnlyForTheCallersOwnOrder()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);
        var handler = new GetOrderHandler(_repository, _currentUser);

        await handler.HandleAsync(new GetOrder(id), CancellationToken.None);

        await _repository.Received(1).GetAsync(id, UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithNoCurrentUser_ReturnsUnauthorized()
    {
        _currentUser.Id.Returns((Guid?)null);
        var handler = new GetOrderHandler(_repository, _currentUser);

        var result = await handler.HandleAsync(new GetOrder(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }
```

In `tests/Application.Tests/Orders/GetOrdersHandlerTests.cs`, add the same two fields and constructor, construct every handler as `new GetOrdersHandler(_repository, _currentUser)`, and add `Arg.Any<Guid>(),` as the first argument to every `_repository.ListAsync(...)` stub and `Received(1).ListAsync(...)` assertion — so `await _repository.Received(1).ListAsync(21, null, Arg.Any<CancellationToken>())` becomes:

```csharp
        await _repository.Received(1).ListAsync(UserId, 21, null, Arg.Any<CancellationToken>());
```

and the two cursor assertions become:

```csharp
        await _repository.Received(1).ListAsync(
            UserId,
            2,
            Arg.Is<(DateTimeOffset PlacedAt, Guid Id)?>(a => a!.Value.PlacedAt == head.PlacedAt && a.Value.Id == head.Id),
            Arg.Any<CancellationToken>());
```

Then add:

```csharp
    [Fact]
    public async Task HandleAsync_WithNoCurrentUser_ReturnsUnauthorized()
    {
        _currentUser.Id.Returns((Guid?)null);
        var handler = new GetOrdersHandler(_repository, _currentUser);

        var result = await handler.HandleAsync(new GetOrders(20, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }
```

- [ ] **Step 7: Run to verify it fails**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~Orders"`
Expected: FAIL to build — `CS1729: 'GetOrderHandler' does not contain a constructor that takes 2 arguments`.

- [ ] **Step 8: Take the user in the read handlers**

`src/Application/Orders/GetOrder.cs`:

```csharp
public sealed class GetOrderHandler(IOrderRepository orders, ICurrentUser currentUser)
    : IQueryHandler<GetOrder, OrderView>
{
    public async Task<Result<OrderView>> HandleAsync(GetOrder query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<OrderView>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        var order = await orders.GetAsync(query.Id, userId, cancellationToken).ConfigureAwait(false);

        // No ownership branch: the repository cannot return another user's order, so someone
        // else's id lands on the same not-found failure as an id that was never issued.
        return order is null
            ? Result.Failure<OrderView>(new Error(
                ErrorKind.NotFound, "order.not_found", $"No order with id '{query.Id}'."))
            : Result.Success(
                new OrderView(order.Id, order.Sku, order.Quantity, order.PlacedAt));
    }
}
```

`src/Application/Orders/GetOrders.cs` — add `ICurrentUser currentUser` to the primary constructor, and put the same guard immediately after `ArgumentNullException.ThrowIfNull(query);`, before the limit check:

```csharp
        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<OrderPage>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }
```

then pass it as the first argument to the existing call:

```csharp
        var rows = await orders
            .ListAsync(userId, query.Limit + 1, after, cancellationToken)
            .ConfigureAwait(false);
```

- [ ] **Step 9: Run to verify it passes**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~Orders"`
Expected: PASS.

- [ ] **Step 10: Run everything**

Run: `dotnet build && dotnet test`
Expected: PASS across all four test projects.

- [ ] **Step 11: Commit**

```bash
git add src/Application/Orders src/Infrastructure/Persistence/OrderRepository.cs tests/
git commit -m "feat(orders): a caller can only read their own orders"
```

---

### Task 4: Prove it over HTTP, and confirm the contract did not move

The end-to-end gate. Two real users, two real cookies, one real database.

**Files:**
- Test: `tests/Api.IntegrationTests/Orders/OrdersEndpointTests.cs`, `tests/Api.IntegrationTests/Orders/OrdersListEndpointTests.cs`
- Verify only: `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts`

**Interfaces:**
- Consumes: everything from Tasks 1–3. `ApiFactory.CreateAuthenticatedClientAsync()` already registers a distinct user per call — that is what makes two clients two users.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

In `tests/Api.IntegrationTests/Orders/OrdersEndpointTests.cs`, add — it can use the file's existing nested `OrderResponseDto`:

```csharp
    [Fact]
    public async Task GetOrder_WhenItBelongsToAnotherUser_Returns404()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync();
        using var stranger = await factory.CreateAuthenticatedClientAsync();
        var created = await owner.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-PRIVATE", Quantity = 1 });
        var id = await created.Content.ReadFromJsonAsync<Guid>();

        var response = await stranger.GetAsync($"/api/orders/{id}");

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "another user's order id must be indistinguishable from one that was never issued");
    }
```

In `tests/Api.IntegrationTests/Orders/OrdersListEndpointTests.cs`, add — using that file's nested `OrderPageDto`:

```csharp
    [Fact]
    public async Task GetOrders_DoesNotReturnAnotherUsersOrders()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync();
        using var stranger = await factory.CreateAuthenticatedClientAsync();
        var created = await owner.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-LIST-PRIVATE", Quantity = 1 });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await created.Content.ReadFromJsonAsync<Guid>();

        var page = await stranger.GetFromJsonAsync<OrderPageDto>("/api/orders?limit=100");

        page.Should().NotBeNull();
        page.Items.Should().NotContain(i => i.Id == id);
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~Orders"`
Expected: PASS. Tasks 1–3 already implement the behavior; these tests prove it end to end rather than driving new code. If either fails, the fix belongs in Task 3's layer, not here.

- [ ] **Step 3: Regenerate the API contract and confirm it is unchanged**

No DTO changed and no response exposes `UserId`, so both generated files must come back byte-identical. Confirm rather than assume:

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
git status --porcelain openapi/ frontend/src/api/schema.d.ts
```

Expected: `git status` prints nothing. If it prints a change, stop and read the diff — a moved contract means a DTO changed, which this plan did not intend.

- [ ] **Step 4: Full verification, both configurations**

Steps 4 and 5 together are what `/verify` runs, plus the Release pass it does not do. Run them
explicitly rather than through the command, so the Release results are visible on their own.

```bash
dotnet build
dotnet build -c Release
dotnet test
dotnet test -c Release
```

Expected: 0 warnings, 0 errors, all green. Release matters on its own: this repo has shipped a Release-only startup break that Debug could not see (ADR 0005). `WolverineCodegenTests` will fail if the pre-generated adapters need regenerating — no Wolverine handler changed here, so it should not, but if it does the fix is `dotnet run --project src/Api -- codegen write` and a commit of the result.

- [ ] **Step 5: Run the frontend suites and the e2e specs**

```bash
npm run lint --prefix frontend && npm run build --prefix frontend && npm test --prefix frontend
```

Then, with the dev API stopped (`npm run e2e` starts its own on port 5234):

```bash
npm run e2e --prefix frontend
```

Expected: PASS unchanged. `frontend/e2e/orders.spec.ts` registers its own user through `sign-up.ts`, so it already places and reads orders as one user. If it fails, that is a real regression, not a test that needs updating.

- [ ] **Step 6: Commit**

```bash
git add tests/Api.IntegrationTests/Orders
git commit -m "test(orders): one user cannot see another's orders over HTTP"
```

---

### Task 5: Record the decision

**Files:**
- Create: `docs/adr/0007-orders-belong-to-the-user-who-placed-them.md`

**Interfaces:**
- Consumes: the shipped implementation.
- Produces: nothing.

- [ ] **Step 1: Write the ADR**

Follow the structure of `docs/adr/0006-username-and-password-authentication.md` — Context, Decision, Consequences, Alternatives considered — and keep it to the decisions, not the code. It must cover:

- **Context:** ADR 0006 shipped authentication without authorisation. `[Authorize]` proved someone was signed in; nothing scoped data to them, so any account could read any order. No test caught it because every test used one user.
- **Decision:** ownership is the placing user, set once, never reassigned; a cross-user read is a 404 identical to a missing order, consistent with ADR 0006's refusal to answer "does this exist?"; enforcement lives in the `IOrderRepository` signature so forgetting it does not compile; `ICurrentUser` is an Application port implemented in Api over `IHttpContextAccessor`, with a nullable `Id` because the outbox pumps have no request; no foreign key to `users`, matching `order_audit`'s existing convention.
- **Consequences:** the migration deleted the three development orders; `Order.Place` grew a parameter, touched in every test project; an admin "see all" view now needs a new repository method, deliberately; `ChangePassword` still takes its user id as a command field and is now inconsistent with the pattern.
- **Alternatives considered:** filtering inside handlers (breaks keyset paging — short pages, wrong cursors, skipped rows); an EF global query filter (`HasQueryFilter`) rejected because the outbox pumps and `OrderPlacedAuditHandler` run in scopes with no HTTP context and would silently see nothing; putting `UserId` on the command record (re-implements the boundary per action and exposes a forgeable field).

Add the ADR to any index the `docs/adr/` directory keeps, if one exists.

- [ ] **Step 2: Commit**

```bash
git add docs/adr/0007-orders-belong-to-the-user-who-placed-them.md
git commit -m "docs: ADR 0007, orders belong to the user who placed them"
```

---

## Done when

- A signed-in user sees only their own orders through `GET /api/orders` and `GET /api/orders/{id}`, proven by integration tests with two real sessions.
- `IOrderRepository` has no read method that can return another user's order.
- `dotnet build` and `dotnet test` are green in both Debug and Release, with 0 warnings.
- `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` are unchanged, and no frontend file was touched.
- ADR 0007 is committed.
