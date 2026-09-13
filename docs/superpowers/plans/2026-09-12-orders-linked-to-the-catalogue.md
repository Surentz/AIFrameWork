# Orders Linked To The Catalogue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An order may only be placed for a product in the catalogue, and it records that product's name and unit price as they were at the moment it was placed.

**Architecture:** `PlaceOrderHandler` resolves the submitted sku against `IProductRepository` and refuses an unknown one; the resolved product becomes an `OrderedProduct` value object snapshotted onto the `Order`. Snapshot rather than join or foreign key, so a later reprice cannot rewrite history and no cross-aggregate cache invalidation is needed. The property is nullable only because rows written before the rule exist.

**Tech Stack:** .NET 10 / C# 14, EF Core (Npgsql), FluentValidation, xUnit + FluentAssertions + NSubstitute, React 19 + TypeScript 6, TanStack Query, Vitest + React Testing Library, Playwright.

**Spec:** `docs/superpowers/specs/2026-09-12-orders-linked-to-the-catalogue-design.md`

## Global Constraints

- **Warnings are errors** — compiler, analyzers and build (`Directory.Build.props`). Never suppress without a justification comment above a narrow `#pragma warning disable`/`restore` pair.
- **Nullable is enabled.** A missing null check does not compile.
- **`required` keyword in `Domain`, never `[Required]`.** DataAnnotations belong on `Api` DTOs only.
- **Never `catch (Exception)`.** `throw;`, never `throw ex;`.
- **Dependency rule** — `Domain` → nothing; `Application` → `Domain`; `Infrastructure` → `Domain`+`Application`; `Api` → all three (DI only). Enforced by `.claude/hooks/dependency-rule.ps1`, which blocks the edit.
- **`Domain` may not reference** `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `Microsoft.Extensions.DependencyInjection`, `System.Data`, `System.ComponentModel.DataAnnotations`.
- **Never hand-edit an applied EF migration.** A migration not yet committed is still yours to adjust; one in `HEAD` is not. Enforced by `.claude/hooks/protect-migrations.ps1`.
- **`frontend`: no `useEffect` data fetching**, no `any`, no `!`, explicit prop interfaces and return types, query keys in one object per feature, every query and mutation renders its error state. `npm run lint` runs with `--max-warnings 0`.
- **Do not hand-edit** `openapi/AiFramework.Api.json` or `frontend/src/api/schema.d.ts` — both are generated and checked by CI.
- **The dev API locks `src/Api/bin/Debug`.** If a build fails with MSB3021/MSB3026 "used by another process", stop the dev loop (`./scripts/stop-dev.ps1`) rather than debugging the build.

---

### Task 1: The `OrderedProduct` value object

**Files:**
- Create: `src/Domain/Orders/OrderedProduct.cs`
- Create: `tests/Domain.Tests/Orders/OrderedProductTests.cs`

**Interfaces:**
- Consumes: `AiFramework.Domain.Products.Product` (for `MaxNameLength`, `PriceScale`), `AiFramework.Domain.DomainException`.
- Produces: `public sealed record OrderedProduct(Guid ProductId, string Name, decimal UnitPrice)` in namespace `AiFramework.Domain.Orders`. Throws `DomainException` from its constructor on an empty name, a name over `Product.MaxNameLength`, a negative price, or a price with more than `Product.PriceScale` decimals.

- [ ] **Step 1: Write the failing tests**

Create `tests/Domain.Tests/Orders/OrderedProductTests.cs`:

```csharp
using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Orders;

public sealed class OrderedProductTests
{
    [Fact]
    public void Constructor_WithValidDetails_SetsTheProperties()
    {
        var productId = Guid.NewGuid();

        var snapshot = new OrderedProduct(productId, "Widget", 19.95m);

        snapshot.ProductId.Should().Be(productId);
        snapshot.Name.Should().Be("Widget");
        snapshot.UnitPrice.Should().Be(19.95m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithNoName_Throws(string name)
    {
        var act = () => new OrderedProduct(Guid.NewGuid(), name, 1m);

        act.Should().Throw<DomainException>().WithMessage("*name*");
    }

    [Fact]
    public void Constructor_WithANameOverTheMaximum_Throws()
    {
        var act = () => new OrderedProduct(
            Guid.NewGuid(), new string('x', Product.MaxNameLength + 1), 1m);

        act.Should().Throw<DomainException>().WithMessage("*name*");
    }

    [Fact]
    public void Constructor_WithANegativePrice_Throws()
    {
        var act = () => new OrderedProduct(Guid.NewGuid(), "Widget", -0.01m);

        act.Should().Throw<DomainException>().WithMessage("*price*");
    }

    [Fact]
    public void Constructor_WithMoreDecimalsThanTheScale_Throws()
    {
        // The column is numeric(18,2); a third decimal would be rounded away on write and the
        // snapshot would no longer equal what was charged.
        var act = () => new OrderedProduct(Guid.NewGuid(), "Widget", 1.005m);

        act.Should().Throw<DomainException>().WithMessage("*decimal places*");
    }

    [Fact]
    public void Constructor_WithNoProduct_Throws()
    {
        var act = () => new OrderedProduct(Guid.Empty, "Widget", 1m);

        act.Should().Throw<DomainException>().WithMessage("*product*");
    }

    [Fact]
    public void TwoSnapshotsWithTheSameValues_AreEqual()
    {
        // A record, because the snapshot has no identity of its own.
        var id = Guid.NewGuid();

        new OrderedProduct(id, "Widget", 1m).Should().Be(new OrderedProduct(id, "Widget", 1m));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Domain.Tests --filter FullyQualifiedName~OrderedProductTests
```

Expected: FAIL to compile — `The type or namespace name 'OrderedProduct' could not be found`.

- [ ] **Step 3: Write the value object**

Create `src/Domain/Orders/OrderedProduct.cs`:

```csharp
using AiFramework.Domain.Products;

namespace AiFramework.Domain.Orders;

/// <summary>
/// What a catalogue product was at the instant an order was placed, copied onto the order rather
/// than read back through <see cref="Product"/>. A later <c>UpdateProduct</c> therefore cannot
/// change what an existing order says it cost — and, because nothing on the order reads the
/// product, repricing cannot stale a cached order page either.
///
/// A record, not an entity: it has no identity of its own, and two snapshots carrying the same
/// three values are the same snapshot.
/// </summary>
public sealed record OrderedProduct
{
    public OrderedProduct(Guid productId, string name, decimal unitPrice)
    {
        if (productId == Guid.Empty)
        {
            throw new DomainException("A snapshot needs a product.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A snapshot needs a product name.");
        }

        if (name.Trim().Length > Product.MaxNameLength)
        {
            throw new DomainException(
                $"A product name cannot be longer than {Product.MaxNameLength} characters.");
        }

        if (unitPrice < 0m)
        {
            throw new DomainException("A unit price cannot be negative.");
        }

        // Mirrors Product.ValidatePrice: the column is numeric(18,2), so a third decimal would be
        // rounded away by Postgres and the snapshot would stop matching what was charged.
        if (decimal.Round(unitPrice, Product.PriceScale) != unitPrice)
        {
            throw new DomainException(
                $"A unit price cannot have more than {Product.PriceScale} decimal places.");
        }

        ProductId = productId;
        Name = name.Trim();
        UnitPrice = unitPrice;
    }

    public Guid ProductId { get; }

    public string Name { get; }

    public decimal UnitPrice { get; }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/Domain.Tests --filter FullyQualifiedName~OrderedProductTests
```

Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Orders/OrderedProduct.cs tests/Domain.Tests/Orders/OrderedProductTests.cs
git commit -m "feat(domain): add the OrderedProduct snapshot value object"
```

---

### Task 2: `Order` carries the snapshot, and every call site follows

`Order.Place` gains a required parameter, which breaks all 21 existing call sites at once. They are all fixed here, in one task, because any smaller split leaves the solution not compiling and therefore not testable.

**Files:**
- Modify: `src/Domain/Orders/Order.cs`
- Create: `tests/Domain.Tests/Orders/AnOrderedProduct.cs`
- Create: `tests/Application.Tests/Orders/AnOrderedProduct.cs`
- Create: `tests/Infrastructure.Tests/AnOrderedProduct.cs`
- Create: `tests/Api.IntegrationTests/AnOrderedProduct.cs`
- Modify: `tests/Domain.Tests/Orders/OrderTests.cs` (5 call sites)
- Modify: `tests/Application.Tests/Orders/GetOrderHandlerTests.cs` (1), `tests/Application.Tests/Orders/GetOrdersHandlerTests.cs` (6)
- Modify: `tests/Infrastructure.Tests/Outbox/OutboxAtomicityTests.cs` (4), `tests/Infrastructure.Tests/Persistence/OrderRepositoryTests.cs` (3), `tests/Infrastructure.Tests/Persistence/OrderRepositoryPagingTests.cs` (1)
- Modify: `tests/Api.IntegrationTests/EventPath/WolverineOutboxAtomicityTests.cs` (1)

**Interfaces:**
- Consumes: `OrderedProduct` from Task 1.
- Produces: `Order.Place(Guid id, Guid userId, int quantity, DateTimeOffset placedAt, OrderedProduct product)` — note the `sku` parameter is **gone**; `Order.Sku` is now derived. `Order.Product` is `OrderedProduct?`. Test helper `AnOrderedProduct.Any()` and `AnOrderedProduct.For(string sku)` in each test project's root namespace.

- [ ] **Step 1: Write the failing tests**

Replace the first two tests in `tests/Domain.Tests/Orders/OrderTests.cs` and add two new ones. The sku assertion changes meaning: it now comes from the snapshot's product, not from a caller-supplied string.

```csharp
    [Fact]
    public void Place_WithValidDetails_SetsTheProperties()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var product = new OrderedProduct(Guid.NewGuid(), "Widget", 19.95m);

        var order = Order.Place(id, userId, 3, PlacedAt, product, "SKU-1");

        order.Id.Should().Be(id);
        order.UserId.Should().Be(userId);
        order.Sku.Should().Be("SKU-1");
        order.Quantity.Should().Be(3);
        order.PlacedAt.Should().Be(PlacedAt);
        order.Product.Should().Be(product);
    }

    [Fact]
    public void Place_WithNoProduct_Throws()
    {
        var act = () => Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1, PlacedAt, null!, "SKU-1");

        act.Should().Throw<DomainException>().WithMessage("*product*");
    }
```

Add `using AiFramework.Domain.Products;` if the file does not already have it.

Create `tests/Domain.Tests/Orders/AnOrderedProduct.cs`:

```csharp
using AiFramework.Domain.Orders;

namespace AiFramework.Domain.Tests.Orders;

/// <summary>
/// A snapshot for the many tests that need an order to exist but assert nothing about which
/// product it was for. Named for how it reads at the call site: Order.Place(..., AnOrderedProduct.Any()).
/// </summary>
internal static class AnOrderedProduct
{
    public static OrderedProduct Any() => new(Guid.NewGuid(), "Widget", 9.99m);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build tests/Domain.Tests -c Debug
```

Expected: FAIL to compile — `No overload for method 'Place' takes 6 arguments`.

- [ ] **Step 3: Change `Order`**

In `src/Domain/Orders/Order.cs`, replace the constructor, add the property, and replace `Place`:

```csharp
    private Order(
        Guid id,
        Guid userId,
        string sku,
        int quantity,
        DateTimeOffset placedAt,
        OrderedProduct? product)
    {
        Id = id;
        UserId = userId;
        Sku = sku;
        Quantity = quantity;
        PlacedAt = placedAt;
        Product = product;
    }
```

Add, beside the existing properties:

```csharp
    /// <summary>
    /// The catalogue entry as it was when this order was placed.
    ///
    /// Nullable ONLY because the table holds rows written before the catalogue link existed, and
    /// EF must materialize those with something. It is not an invariant that bends: <see
    /// cref="Place"/> requires a snapshot, so no order created from here on can be without one.
    /// </summary>
    public OrderedProduct? Product { get; private set; }
```

Replace `Place`:

```csharp
    /// <summary>
    /// Takes a NON-nullable snapshot: from the catalogue link onwards there is no such thing as
    /// an order without a product. <paramref name="sku"/> is passed separately rather than read
    /// off the snapshot because the snapshot is the product's identity and price, not its sku —
    /// the caller supplies the catalogue's own normalized sku, which PlaceOrderHandler reads from
    /// the Product it just resolved.
    /// </summary>
    public static Order Place(
        Guid id,
        Guid userId,
        int quantity,
        DateTimeOffset placedAt,
        OrderedProduct product,
        string sku)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException("An order needs a user.");
        }

        if (product is null)
        {
            throw new DomainException("An order needs a catalogue product.");
        }

        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new DomainException("An order needs a sku.");
        }

        if (quantity <= 0)
        {
            throw new DomainException("An order needs a positive quantity.");
        }

        var order = new Order(id, userId, sku, quantity, placedAt, product);
        order.Raise(new OrderPlaced(id, sku, quantity));
        return order;
    }
```

- [ ] **Step 4: Update the remaining 16 call sites**

Copy `AnOrderedProduct.cs` into the other three test projects, changing only the namespace:

- `tests/Application.Tests/Orders/AnOrderedProduct.cs` → `namespace AiFramework.Application.Tests.Orders;`
- `tests/Infrastructure.Tests/AnOrderedProduct.cs` → `namespace AiFramework.Infrastructure.Tests;`
- `tests/Api.IntegrationTests/AnOrderedProduct.cs` → `namespace AiFramework.Api.IntegrationTests;`

Then rewrite each call. Every existing call has the shape `Order.Place(id, userId, sku, quantity, placedAt)`; the new shape moves `sku` last and adds the snapshot:

```csharp
// before
Order.Place(id, owner, sku, 1, placedAt)
// after
Order.Place(id, owner, 1, placedAt, AnOrderedProduct.Any(), sku)
```

Find every one:

```bash
grep -rn "Order.Place(" tests/ --include=*.cs
```

- [ ] **Step 5: Run the full backend suite**

```bash
dotnet test -c Debug
```

Expected: PASS. Domain 76+, Application 96, Infrastructure 122, Api.IntegrationTests 95. `PlaceOrderHandler` still compiles because it is updated in Task 4 — **it is not**; it calls the old signature. Fix it here minimally so the solution compiles, passing the snapshot it cannot yet resolve is not possible, so instead update `src/Application/Orders/PlaceOrder.cs`'s single call to:

```csharp
        var order = Order.Place(
            Guid.NewGuid(), userId, command.Quantity, clock.UtcNow,
            new OrderedProduct(Guid.NewGuid(), command.Sku, 0m), command.Sku);
```

with this comment directly above it:

```csharp
        // TEMPORARY, replaced in the next commit when the handler resolves the real product.
        // Kept for one commit only so that the signature change is reviewable on its own.
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(domain): Order.Place requires a catalogue snapshot"
```

---

### Task 3: Persist the snapshot, with the migration and backfill

**Files:**
- Modify: `src/Infrastructure/Persistence/Configurations/OrderConfiguration.cs`
- Create: `src/Infrastructure/Persistence/Migrations/<timestamp>_LinkOrdersToTheCatalogue.cs` (generated)
- Modify: `tests/Infrastructure.Tests/Persistence/OrderRepositoryTests.cs`

**Interfaces:**
- Consumes: `Order.Product` from Task 2.
- Produces: columns `ProductId uuid NULL`, `ProductName text NULL`, `UnitPrice numeric(18,2) NULL` on `orders`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Infrastructure.Tests/Persistence/OrderRepositoryTests.cs`:

```csharp
    [Fact]
    public async Task AddAsync_ThenSaveChanges_PersistsTheProductSnapshot()
    {
        var snapshot = new OrderedProduct(Guid.NewGuid(), "Widget", 19.95m);
        var order = Order.Place(
            Guid.NewGuid(), Guid.NewGuid(), 2, PlacedAt, snapshot, "SKU-SNAPSHOT");

        await using (var context = fixture.CreateContext())
        {
            await new OrderRepository(context).AddAsync(order, CancellationToken.None);
            await new UnitOfWork(context).SaveChangesAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify)
            .GetAsync(order.Id, order.UserId, CancellationToken.None);

        found.Should().NotBeNull();
        found.Product.Should().Be(snapshot);
    }

    [Fact]
    public async Task AnOrderRowWithNoSnapshot_MaterializesWithANullProduct()
    {
        // The rows that predate the catalogue link. Written as raw SQL because the domain can no
        // longer construct one - which is exactly the invariant Task 2 installed.
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using (var seed = fixture.CreateContext())
        {
            await seed.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO orders ("Id", "UserId", "Sku", "Quantity", "PlacedAt")
                 VALUES ({id}, {userId}, 'LEGACY', 1, {PlacedAt})
                 """);
        }

        await using var verify = fixture.CreateContext();
        var found = await new OrderRepository(verify)
            .GetAsync(id, userId, CancellationToken.None);

        found.Should().NotBeNull();
        found.Product.Should().BeNull();
        found.Sku.Should().Be("LEGACY");
    }
```

Add `using AiFramework.Domain.Orders;` and `using Microsoft.EntityFrameworkCore;` if absent. If the file has no `PlacedAt` constant, add `private static readonly DateTimeOffset PlacedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Infrastructure.Tests --filter FullyQualifiedName~OrderRepositoryTests
```

Expected: FAIL — the snapshot columns do not exist, so `found.Product` is null in the first test.

- [ ] **Step 3: Map the owned type**

In `OrderConfiguration.Configure`, after the `Sku`/`Quantity`/`PlacedAt` property lines:

```csharp
        // Owned rather than three loose nullable scalars, so "all three columns or none" is
        // structural instead of a convention this class has to police. EF decides the dependent
        // is absent by looking at its REQUIRED properties - OrderedProduct's three are all
        // non-nullable CLR types - which is why this does not trip
        // OptionalDependentWithAllNullPropertiesWarning, and why adding a nullable property to
        // OrderedProduct later would.
        builder.OwnsOne(o => o.Product, product =>
        {
            product.Property(p => p.ProductId).HasColumnName("ProductId");
            product.Property(p => p.Name)
                .HasColumnName("ProductName")
                .HasMaxLength(Product.MaxNameLength);
            product.Property(p => p.UnitPrice).HasColumnName("UnitPrice").HasPrecision(18, 2);
        });

        // Rows written before the catalogue link have all three columns null.
        builder.Navigation(o => o.Product).IsRequired(false);
```

Add `using AiFramework.Domain.Products;`.

- [ ] **Step 4: Confirm the model builds with no warning**

```bash
dotnet build src/Infrastructure -c Debug
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

**If this fails with `OptionalDependentWithAllNullPropertiesWarning`,** stop and fall back to the spec's documented alternative: three nullable scalar properties (`ProductId`, `ProductName`, `UnitPrice`) directly on `Order`, with `Order.Place` assigning all three from the `OrderedProduct` it is handed. Nothing else in this plan changes.

- [ ] **Step 5: Generate the migration**

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef migrations add LinkOrdersToTheCatalogue \
  --project src/Infrastructure --startup-project src/Infrastructure
```

- [ ] **Step 6: Add the backfill to the generated migration**

The migration is new and uncommitted, so it is still yours to edit. At the **end** of `Up`, after the three `AddColumn` calls:

```csharp
            // Orders predate the catalogue, so most rows match nothing and keep their nulls - the
            // UI renders those as the bare sku, which is honest: we do not know what they cost,
            // and copying today's price would assert something false. A row that DOES match has
            // its sku rewritten to the catalogue's normalized form too, so that sku and snapshot
            // agree for every row carrying one.
            //
            // Additive and null-tolerant on purpose: per ADR 0010 the previous generation's pods
            // keep inserting orders that set none of these columns for the length of the rollout.
            migrationBuilder.Sql(
                """
                UPDATE orders o
                SET "ProductId"   = p."Id",
                    "ProductName" = p."Name",
                    "UnitPrice"   = p."Price",
                    "Sku"         = p."Sku"
                FROM products p
                WHERE upper(trim(o."Sku")) = p."Sku";
                """);
```

Leave `Down` as generated — dropping the three columns is a correct inverse, and the sku rewrite is not reversible because the original casing is not recorded anywhere.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/Infrastructure.Tests --filter FullyQualifiedName~OrderRepositoryTests
```

Expected: PASS. The fixture runs `MigrateAsync`, so the new migration is applied to the test container automatically.

- [ ] **Step 8: Commit**

```bash
git add src/Infrastructure tests/Infrastructure.Tests
git commit -m "feat(infrastructure): persist the order product snapshot, with a backfill"
```

---

### Task 4: `PlaceOrder` validates against the catalogue

This is where every existing caller that posts an arbitrary sku starts failing, so the seven integration test files and the e2e fixture are fixed in the same task.

**Files:**
- Modify: `src/Application/Orders/PlaceOrder.cs`
- Modify: `tests/Application.Tests/Orders/PlaceOrderHandlerTests.cs`
- Create: `tests/Api.IntegrationTests/Orders/CatalogueSetup.cs`
- Modify: `tests/Api.IntegrationTests/Orders/OrdersEndpointTests.cs`, `OrdersListEndpointTests.cs`, `OrderCachingTests.cs`, `OutboxDeliveryTests.cs`, `ProblemDetailsContractTests.cs`, `tests/Api.IntegrationTests/DataProtectionTests.cs`, `tests/Api.IntegrationTests/OpenApiDocumentTests.cs`
- Modify: `frontend/e2e/fixtures/api.ts`, `frontend/e2e/specs/orders/place-order.spec.ts`, `frontend/e2e/specs/orders/validation.spec.ts`

**Interfaces:**
- Consumes: `IProductRepository.GetBySkuAsync(string sku, CancellationToken)` — **keyed on the normalized form**, so callers must pass `Product.NormalizeSku`'s output.
- Produces: failure `Error(ErrorKind.Validation, "orders.unknown_sku", ...)`; `CatalogueSetup.CreateProductAsync(HttpClient client, string? sku = null)` returning the created product's normalized sku.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Application.Tests/Orders/PlaceOrderHandlerTests.cs` (follow the file's existing NSubstitute setup for `IOrderRepository`, `IClock`, `ICurrentUser`; add a substituted `IProductRepository`):

```csharp
    [Fact]
    public async Task HandleAsync_WithASkuNotInTheCatalogue_FailsValidation()
    {
        _products.GetBySkuAsync("SKU-MISSING", Arg.Any<CancellationToken>())
            .Returns((Product?)null);

        var result = await _handler.HandleAsync(
            new PlaceOrder("SKU-MISSING", 2), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("orders.unknown_sku");
    }

    [Fact]
    public async Task HandleAsync_LooksTheSkuUpInItsNormalizedForm()
    {
        // Product.Sku is stored upper-case, so a lower-case submission must still match.
        var product = Product.Create(
            Guid.NewGuid(), "SKU-1", "Widget", null, 19.95m, _clock.UtcNow);
        _products.GetBySkuAsync("SKU-1", Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.HandleAsync(
            new PlaceOrder("sku-1", 2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _products.Received(1).GetBySkuAsync("SKU-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_SnapshotsTheProductOntoTheOrder()
    {
        var product = Product.Create(
            Guid.NewGuid(), "SKU-1", "Widget", null, 19.95m, _clock.UtcNow);
        _products.GetBySkuAsync("SKU-1", Arg.Any<CancellationToken>()).Returns(product);

        await _handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        await _orders.Received(1).AddAsync(
            Arg.Is<Order>(o =>
                o.Sku == "SKU-1"
                && o.Product?.ProductId == product.Id
                && o.Product.Name == "Widget"
                && o.Product.UnitPrice == 19.95m),
            Arg.Any<CancellationToken>());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Application.Tests --filter FullyQualifiedName~PlaceOrderHandlerTests
```

Expected: FAIL to compile — `PlaceOrderHandler` has no `IProductRepository` constructor parameter.

- [ ] **Step 3: Implement the handler**

Replace `PlaceOrderHandler` in `src/Application/Orders/PlaceOrder.cs`:

```csharp
public sealed class PlaceOrderHandler(
    IOrderRepository orders,
    IProductRepository products,
    IClock clock,
    ICurrentUser currentUser)
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

        var sku = Product.NormalizeSku(command.Sku);

        // Check-then-insert with no uniqueness guard behind it, unlike CreateProduct's, whose
        // unique index is the real defence. The asymmetry is deliberate: the only race here is a
        // product deleted between this read and the insert, and the catalogue has no delete. Add
        // one and this is the site to revisit.
        var product = await products.GetBySkuAsync(sku, cancellationToken).ConfigureAwait(false);

        if (product is null)
        {
            // Validation rather than NotFound: from the caller's position this is a bad value in
            // a submitted field, and it lands beside the input as a 400, the way an invalid
            // quantity already does.
            return Result.Failure<Guid>(new Error(
                ErrorKind.Validation, "orders.unknown_sku",
                $"No product with sku '{sku}' is in the catalogue."));
        }

        var order = Order.Place(
            Guid.NewGuid(),
            userId,
            command.Quantity,
            clock.UtcNow,
            new OrderedProduct(product.Id, product.Name, product.Price),
            product.Sku);

        await orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

        return Result.Success(order.Id);
    }
}
```

Add `using AiFramework.Application.Products;` and `using AiFramework.Domain.Products;`.

- [ ] **Step 4: Add the integration-test helper and fix the seven files**

Create `tests/Api.IntegrationTests/Orders/CatalogueSetup.cs`:

```csharp
using System.Net.Http.Json;

namespace AiFramework.Api.IntegrationTests.Orders;

/// <summary>
/// Every "place an order" test now needs a catalogue product first. One helper rather than the
/// same six lines in seven files.
/// </summary>
internal static class CatalogueSetup
{
    /// <summary>
    /// Creates a product and returns its normalized sku. Unique per call: the catalogue is global
    /// and its sku index is unique, so a fixed literal would collide across tests sharing the
    /// one Postgres container.
    /// </summary>
    public static async Task<string> CreateProductAsync(HttpClient client, decimal price = 19.95m)
    {
        var sku = $"SKU-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new { Sku = sku, Name = "Widget", Description = (string?)null, Price = price });

        response.EnsureSuccessStatusCode();

        return sku;
    }
}
```

Then in each of the seven files, replace every literal sku posted to `/api/orders` with one created first. The shape:

```csharp
// before
using var client = await factory.CreateAuthenticatedClientAsync();
var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-1", Quantity = 2 });

// after
using var client = await factory.CreateAuthenticatedClientAsync();
var sku = await CatalogueSetup.CreateProductAsync(client);
var response = await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 2 });
```

Leave the anonymous-request tests alone — they assert a 401 before validation runs, so they need no product.

Find them all:

```bash
grep -rn "api/orders" tests/Api.IntegrationTests --include=*.cs
```

- [ ] **Step 5: Fix the e2e fixture and the two affected specs**

In `frontend/e2e/fixtures/api.ts`, make `placeOrder` create the product it orders, so the fixture stays a one-call setup. In `frontend/e2e/specs/orders/place-order.spec.ts`, create a product via the api fixture and type **its** sku into the still-present text input. In `frontend/e2e/specs/orders/validation.spec.ts`, replace `SKU-E2E-INVALID` with a real product's sku:

```ts
test('shows the server validation message for an invalid quantity', async ({
  signedInPage,
  api,
  workerUser,
}) => {
  // A real catalogue sku, so this test still fails on the QUANTITY. With a sku the catalogue
  // does not hold, the request would be refused for the sku instead and this test would pass
  // while asserting nothing about quantity.
  const sku = await api.createProduct(workerUser, { price: 9.99 });

  await orders.placeOrder(signedInPage, { sku, quantity: 0 });
  ...
```

Expose `workerUser` on the test fixture if the spec does not already receive it (it is already declared as a worker fixture in `frontend/e2e/fixtures/index.ts`).

- [ ] **Step 6: Run everything**

```bash
dotnet test -c Debug
API_PORT=5299 npm run e2e --prefix frontend
```

Expected: backend PASS; e2e 15 passed.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(application): refuse an order for a sku the catalogue does not hold"
```

---

### Task 5: Carry the snapshot out through the API contract

**Files:**
- Modify: `src/Application/Orders/GetOrder.cs`, `src/Application/Orders/GetOrders.cs`
- Modify: `src/Api/Orders/OrderDtos.cs`, `src/Api/Orders/OrdersController.cs`
- Modify: `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts` (both **generated** — do not hand-edit)
- Modify: `tests/Api.IntegrationTests/Orders/OrdersEndpointTests.cs`

**Interfaces:**
- Produces: `OrderView(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt, Guid? ProductId, string? ProductName, decimal? UnitPrice)` and the same three trailing members on `OrderListItem`, `OrderResponse`, `OrderListItemResponse`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Api.IntegrationTests/Orders/OrdersEndpointTests.cs`:

```csharp
    [Fact]
    public async Task PostOrders_ThenGet_ReturnsTheProductSnapshot()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(client, price: 12.50m);

        var created = await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 2 });
        var id = await created.Content.ReadFromJsonAsync<Guid>();

        var order = await client.GetFromJsonAsync<JsonElement>($"/api/orders/{id}");

        order.GetProperty("productName").GetString().Should().Be("Widget");
        order.GetProperty("unitPrice").GetDecimal().Should().Be(12.50m);
        order.GetProperty("productId").GetGuid().Should().NotBeEmpty();
    }

    [Fact]
    public async Task PostOrders_WithAnUnknownSku_Returns400()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-NOT-IN-THE-CATALOGUE", Quantity = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Api.IntegrationTests --filter FullyQualifiedName~PostOrders_ThenGet_ReturnsTheProductSnapshot
```

Expected: FAIL — `The given key 'productName' was not present`.

- [ ] **Step 3: Widen the views and DTOs**

`src/Application/Orders/GetOrder.cs`:

```csharp
public sealed record OrderView(
    Guid Id,
    string Sku,
    int Quantity,
    DateTimeOffset PlacedAt,
    Guid? ProductId,
    string? ProductName,
    decimal? UnitPrice);
```

and in `GetOrderHandler`, the success projection:

```csharp
            : Result.Success(new OrderView(
                order.Id, order.Sku, order.Quantity, order.PlacedAt,
                order.Product?.ProductId, order.Product?.Name, order.Product?.UnitPrice));
```

`src/Application/Orders/GetOrders.cs`:

```csharp
public sealed record OrderListItem(
    Guid Id,
    string Sku,
    int Quantity,
    DateTimeOffset PlacedAt,
    Guid? ProductId,
    string? ProductName,
    decimal? UnitPrice);
```

and wherever `GetOrdersHandler` projects rows to `OrderListItem`, add the same three trailing arguments.

In `src/Api/Orders/OrderDtos.cs`, add to **both** `OrderResponse` and `OrderListItemResponse`:

```csharp
    /// <summary>
    /// The catalogue product as it was when the order was placed. Null on orders that predate the
    /// catalogue link — those render as the bare sku with no price.
    /// </summary>
    public Guid? ProductId { get; init; }

    public string? ProductName { get; init; }

    public decimal? UnitPrice { get; init; }
```

`init`-only and **not** `required`: a null snapshot is a legitimate value, and `required` would force every construction site to say so explicitly. Then update the two mapping sites in `OrdersController` to copy the three across.

- [ ] **Step 4: Regenerate the contract**

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
```

- [ ] **Step 5: Run the backend suite and the frontend build**

```bash
dotnet test -c Debug
npm run build --prefix frontend
npm test --prefix frontend
```

Expected: all PASS. The frontend still compiles because the three new members are optional.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(api): expose the order product snapshot"
```

---

### Task 6: Render the snapshot, falling back to the sku

**Files:**
- Modify: `frontend/src/features/orders/OrderDetail.tsx`, `frontend/src/features/orders/OrderList.tsx`
- Modify: `frontend/src/features/orders/OrderDetail.test.tsx`, `frontend/src/features/orders/OrderList.test.tsx`

**Interfaces:**
- Consumes: `Order` / `OrderListItem` from `./types`, now carrying `productId?`, `productName?`, `unitPrice?`.

- [ ] **Step 1: Write the failing tests**

Add to `frontend/src/features/orders/OrderDetail.test.tsx`, following the file's existing MSW/render helpers:

```tsx
it('shows the product name and unit price when the order has a snapshot', async () => {
  renderOrderDetail({
    id: 'c0ffee00-0000-4000-8000-000000000001',
    sku: 'SKU-1',
    quantity: 2,
    placedAt: '2026-09-01T12:00:00Z',
    productId: 'p0000000-0000-4000-8000-000000000001',
    productName: 'Widget',
    unitPrice: 12.5,
  });

  expect(await screen.findByRole('heading', { name: 'Widget' })).toBeInTheDocument();
  expect(screen.getByText('SKU-1')).toBeInTheDocument();
  expect(screen.getByText('£12.50')).toBeInTheDocument();
  expect(screen.getByText('£25.00')).toBeInTheDocument();
});

it('falls back to the sku when the order predates the catalogue', async () => {
  renderOrderDetail({
    id: 'c0ffee00-0000-4000-8000-000000000002',
    sku: 'TooTH',
    quantity: 3,
    placedAt: '2026-09-01T12:00:00Z',
  });

  expect(await screen.findByRole('heading', { name: 'TooTH' })).toBeInTheDocument();
  expect(screen.queryByText(/£/)).not.toBeInTheDocument();
});
```

- [ ] **Step 2: Run to verify they fail**

```bash
npm test --prefix frontend -- OrderDetail
```

Expected: FAIL — heading is `SKU-1`, not `Widget`.

- [ ] **Step 3: Implement**

Create `frontend/src/features/orders/money.ts`:

```ts
/** One formatter, created once: constructing Intl.NumberFormat per render is measurably slow. */
const formatter = new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP' });

export function formatMoney(amount: number): string {
  return formatter.format(amount);
}
```

In `OrderDetail.tsx`, replace the heading and add two facts:

```tsx
        <div>
          <h1 className="page-title">{data.productName ?? data.sku}</h1>
          <p className="page-subtitle">Order detail</p>
        </div>
```

and inside the `<dl className="order-facts">`, before `Quantity`:

```tsx
          <div>
            <dt>Sku</dt>
            <dd>
              {data.productId === undefined ? (
                data.sku
              ) : (
                <Link to={`/products/${data.productId}`}>{data.sku}</Link>
              )}
            </dd>
          </div>
          {data.unitPrice !== undefined && (
            <>
              <div>
                <dt>Unit price</dt>
                <dd>{formatMoney(data.unitPrice)}</dd>
              </div>
              <div>
                <dt>Total</dt>
                <dd>{formatMoney(data.unitPrice * data.quantity)}</dd>
              </div>
            </>
          )}
```

In `OrderList.tsx`, render `item.productName ?? item.sku` in the sku cell's link text, keeping the link target unchanged.

- [ ] **Step 4: Run to verify they pass**

```bash
npm test --prefix frontend -- OrderDetail OrderList
npm run lint --prefix frontend
```

Expected: PASS, lint clean.

- [ ] **Step 5: Commit**

```bash
git add frontend/src
git commit -m "feat(frontend): show the product snapshot on orders, falling back to the sku"
```

---

### Task 7: The place-order form offers the catalogue

**Files:**
- Modify: `frontend/src/features/products/queries.ts`
- Modify: `frontend/src/features/orders/PlaceOrderForm.tsx`, `frontend/src/features/orders/PlaceOrderForm.test.tsx`
- Modify: `frontend/e2e/specs/orders/place-order.spec.ts`, `frontend/e2e/screens/orders.ts`

**Interfaces:**
- Produces: `useAllProducts(): UseQueryResult<Product[], ApiError>` in `features/products/queries.ts`.

- [ ] **Step 1: Write the failing tests**

Add to `frontend/src/features/orders/PlaceOrderForm.test.tsx`:

```tsx
it('offers the catalogue and submits the selected sku', async () => {
  const user = userEvent.setup();
  renderForm({ products: [{ id: 'p1', sku: 'SKU-1', name: 'Widget', price: 12.5 }] });

  await user.selectOptions(await screen.findByLabelText('Product'), 'SKU-1');
  await user.clear(screen.getByLabelText('Quantity'));
  await user.type(screen.getByLabelText('Quantity'), '2');
  await user.click(screen.getByRole('button', { name: 'Place order' }));

  await waitFor(() => {
    expect(placed).toEqual({ sku: 'SKU-1', quantity: 2 });
  });
});

it('disables submit and explains itself when the catalogue is empty', async () => {
  renderForm({ products: [] });

  expect(await screen.findByText(/no products in the catalogue/i)).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Place order' })).toBeDisabled();
  expect(screen.getByRole('link', { name: /add a product/i })).toBeInTheDocument();
});

it('renders the error state when the catalogue cannot be loaded', async () => {
  renderForm({ productsFail: true });

  expect(await screen.findByRole('alert')).toHaveTextContent(/products/i);
});
```

- [ ] **Step 2: Run to verify they fail**

```bash
npm test --prefix frontend -- PlaceOrderForm
```

Expected: FAIL — `Unable to find a label with the text of: Product`.

- [ ] **Step 3: Add the hook**

In `frontend/src/features/products/queries.ts`, beside `useProducts`:

```ts
/**
 * The whole catalogue, for the order form's picker — a select needs every option, not a page.
 *
 * The paging loop lives in the queryFn rather than in an effect driving fetchNextPage, because
 * this repo does not fetch from useEffect (frontend/CLAUDE.md) and TanStack Query owns server
 * state. Its own cache entry, so it never collides with the paged list on the catalogue screen.
 *
 * A select stops being the right control somewhere in the hundreds of products. That is the
 * point to replace this with a search endpoint and an autocomplete, not to paginate the select.
 */
export function useAllProducts(): UseQueryResult<Product[], ApiError> {
  return useQuery({
    queryKey: [...productKeys.all, 'every'] as const,
    queryFn: async () => {
      const items: Product[] = [];
      let cursor: string | undefined;

      do {
        const page = await listProducts({ cursor });
        items.push(...page.items);
        cursor = page.nextCursor ?? undefined;
      } while (cursor !== undefined);

      return items;
    },
  });
}
```

- [ ] **Step 4: Rebuild the form**

In `PlaceOrderForm.tsx`, replace the `sku` text input with a select and add the three states. Keep every other field, the `useId` pattern, and the live-region error markup exactly as they are.

```tsx
  const { data: products, isPending: productsPending, error: productsError } = useAllProducts();
  const [sku, setSku] = useState('');
  const selected = products?.find((p) => p.sku === sku);
```

Above the `<form>`:

```tsx
      {productsError && (
        <p className="alert" role="alert">
          The products could not be loaded. {productsError.message}
        </p>
      )}
```

The field itself:

```tsx
          <div className="field">
            <label className="field__label" htmlFor={skuId}>
              Product
            </label>
            <select
              className="input"
              id={skuId}
              value={sku}
              disabled={productsPending || products === undefined || products.length === 0}
              aria-invalid={skuErrors.length > 0}
              aria-describedby={skuErrors.length > 0 ? skuErrorId : undefined}
              onChange={(e) => {
                setSku(e.target.value);
              }}
            >
              <option value="">Choose a product…</option>
              {products?.map((product) => (
                <option key={product.id} value={product.sku}>
                  {product.name} — {product.sku} — {formatMoney(product.price)}
                </option>
              ))}
            </select>
            {products?.length === 0 && (
              <p className="field__hint">
                There are no products in the catalogue yet.{' '}
                <Link to="/products/new">Add a product</Link> first.
              </p>
            )}
            <div className="field__errors" id={skuErrorId} aria-live="polite">
              {skuErrors.map((message) => (
                <p key={message}>{message}</p>
              ))}
            </div>
          </div>
```

The total, after the quantity field:

```tsx
          {selected && quantity > 0 && (
            <p className="order-form__total">
              Total: {formatMoney(selected.price * quantity)}
            </p>
          )}
```

And the submit button's `disabled`:

```tsx
            <button
              className="btn btn--primary"
              type="submit"
              disabled={mutation.isPending || sku === '' || products?.length === 0}
            >
```

Add `.field__hint` and `.order-form__total` rules to `frontend/src/features/orders/orders.css`, matching the file's existing spacing and muted-text variables.

- [ ] **Step 5: Point the e2e spec at the picker**

In `frontend/e2e/screens/orders.ts`, change `skuField` to the select and add a chooser:

```ts
export const productField = (p: Page): Locator => p.getByLabel('Product');

export async function placeOrder(
  p: Page,
  order: { sku: string; quantity: number },
): Promise<void> {
  await p.goto('/orders/new');
  await productField(p).selectOption(order.sku);
  await quantityField(p).fill(String(order.quantity));
  await submitButton(p).click();
}
```

`place-order.spec.ts` then creates a product via the api fixture and passes its sku, which `placeOrder` now selects rather than types. `validation.spec.ts` keeps the real sku it gained in Task 4 and still submits quantity 0.

- [ ] **Step 6: Run everything**

```bash
npm test --prefix frontend
npm run lint --prefix frontend
npm run build --prefix frontend
API_PORT=5299 npm run e2e --prefix frontend
```

Expected: all PASS, 15 e2e tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(frontend): choose a catalogue product when placing an order"
```

---

### Task 8: Full verification and the docs that point at this

**Files:**
- Modify: `CLAUDE.md` (the Caching section's order/product paragraph)
- Modify: `docs/superpowers/specs/2026-09-12-orders-linked-to-the-catalogue-design.md` (status line)

- [ ] **Step 1: Run everything CI runs, in both configurations**

```bash
./scripts/stop-dev.ps1     # the dev API locks src/Api/bin/Debug
dotnet test -c Debug
dotnet test -c Release
npm run lint --prefix frontend
npm test --prefix frontend
npm run build --prefix frontend
API_PORT=5299 npm run e2e --prefix frontend
```

Expected: zero warnings, zero failures throughout.

- [ ] **Step 2: Confirm the generated artefacts are current**

```bash
dotnet run --project src/Api -- codegen write
git diff --exit-code
```

Expected: no diff. If `openapi/AiFramework.Api.json` or `frontend/src/api/schema.d.ts` differ, re-run Task 5 Step 4 and commit the result — CI fails on any diff.

- [ ] **Step 3: Update the two docs**

In `CLAUDE.md`, extend the Caching section to record why no new eviction was needed:

```markdown
`PlaceOrder` snapshots the product's name and price onto the order, so a later `UpdateProduct`
cannot change what an existing order says it cost — and cannot stale a cached order page either.
That is what keeps product writes out of the order cache's eviction path entirely.
```

Change the spec's status line to `**Status:** Implemented`.

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md docs/superpowers/specs/2026-09-12-orders-linked-to-the-catalogue-design.md
git commit -m "docs: record the catalogue link's effect on order caching"
```

---

## Self-Review

**Spec coverage.** Snapshot over join/FK → Task 1, 4. Unconditional invariant with nullable persistence → Task 2, 3. Value object → Task 1. Sku from the product → Task 2 (signature), Task 4 (handler). `orders.unknown_sku` as Validation → Task 4. Additive migration + backfill including the sku rewrite → Task 3. Picker with loading/error/empty → Task 7. Snapshot rendering with sku fallback → Task 6. The 21 call sites → Task 2. The 7 integration files → Task 4. `validation.spec.ts` fixed → Task 4. Caching unchanged, recorded → Task 8.

**One deliberate deviation from the spec.** The spec says the picker "fetches pages until `hasNextPage` is false", which reads as an effect driving `fetchNextPage`; `frontend/CLAUDE.md` forbids `useEffect` data fetching. Task 7 gets the same behaviour by looping inside a `queryFn` (`useAllProducts`), which leaves TanStack Query owning the state. Same outcome, inside the conventions.

**Known rough edge, deliberately kept.** Task 2 Step 5 leaves `PlaceOrderHandler` constructing a throwaway snapshot for exactly one commit, so the `Order.Place` signature change is reviewable on its own rather than tangled with the catalogue lookup. Task 4 deletes it. It is marked TEMPORARY in the code.

**Type consistency.** `OrderedProduct(Guid ProductId, string Name, decimal UnitPrice)` is used with those three names in Tasks 1, 2, 3, 4. `Order.Place(id, userId, quantity, placedAt, product, sku)` keeps that argument order in Tasks 2 and 4. `AnOrderedProduct.Any()` is defined once per test project and called identically. `CatalogueSetup.CreateProductAsync(client, price)` is defined in Task 4 and called in Task 5. `formatMoney` is created in Task 6 and reused in Task 7. The DTO members are `ProductId`/`ProductName`/`UnitPrice` in C# and `productId`/`productName`/`unitPrice` in TypeScript throughout.
