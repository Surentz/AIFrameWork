# Order ownership, and the current-user seam

**Date:** 2026-09-06
**Status:** Approved, not yet implemented

## Context

ADR 0006 shipped authentication: a cookie session, a `User` aggregate, `[Authorize]` on both
controllers. It did not ship authorisation, and nothing in the codebase noticed.

`[Authorize]` proves *someone* holds a session. Nothing narrows what they can reach:

- `PlaceOrder(Sku, Quantity)` takes no user, and `Order` has no owner field.
- `GetOrders(Limit, Cursor)` lists every order in the database.
- `GET /api/orders/{id}` serves any order to any signed-in caller.

So the second account to register sees the first account's orders. The integration tests do not
catch it because `ApiFactory.CreateAuthenticatedClientAsync` registers a fresh user per client and
each test only reads back what it just wrote — no test has ever asked whether one user can see
another's data.

This spec closes that hole and installs the seam the rest of the application will read the caller
from.

## Decisions

### Ownership is the placing user, full stop

`Order` gains a `UserId`, set once when the order is placed and never changed. No customer or
organisation concept, no reassignment, no sharing. If an org concept ever arrives it becomes a
second field rather than a rewrite of this one.

### A cross-user read is a 404, not a 403

Another user's order id is indistinguishable from an id that was never issued. This follows ADR
0006, where the uniform sign-in error and the dummy hash exist precisely so that responses do not
answer "does this exist?". Guids are not guessable, so the practical leak either way is small; the
principle is already established in this repo and consistency is worth more than the marginally
better status code.

This falls out of the design rather than needing a branch — see "Application" below.

### Ownership is enforced in the repository port, not in handlers

`IOrderRepository` grows an owner parameter on every read. There is no overload that returns an
order the caller does not own, so forgetting the filter is a compile error rather than a leak.

The alternative — leaving the port alone and comparing `order.UserId` inside each handler — was
rejected because it does not work for the list. `ListAsync` fetches `limit + 1` rows to decide
`hasMore`; discarding other users' rows afterwards would produce short pages, wrong `NextCursor`
values, and a keyset that skips. The list forces the filter into SQL regardless, so handler-side
checking would buy an inconsistency, not a simplification.

An EF global query filter (`HasQueryFilter`) was also rejected. It is automatic and unforgettable,
including for queries nobody has written yet — but it is evaluated per `DbContext` instance against
a request-scoped user, and `OutboxPollerService`, `OutboxWorkerService` and
`OrderPlacedAuditHandler` all run in scopes with no HTTP context. Those paths would silently filter
everything away, or need `IgnoreQueryFilters()` in places where its absence is invisible until data
goes missing.

### The caller reaches handlers through a port, not through the command

Handlers inject `ICurrentUser`. The command and query records are unchanged.

The alternative is what `AuthController` already does for `ChangePassword`: read the claim in the
controller and put a `UserId` on the command. Rejected here because it re-implements the security
boundary in every action, and because a command record carrying a caller-supplied `UserId` is one
matching DTO property away from letting someone place an order as another user. One source of
truth for "who is calling" is worth the inconsistency with `ChangePassword`, which this spec leaves
alone deliberately (see "Out of scope").

### `ICurrentUser.Id` is nullable

`Guid?`, not a `Guid` that throws when there is no session. The outbox pumps genuinely run without
a user, and a handler that returns `ErrorKind.Unauthorized` produces a 401 where a throw would
produce a 500.

### Existing orders are deleted, and the column is NOT NULL

The dev database on 55433 holds 3 owner-less orders and there is no deployment configuration in
this repo, so there is no data worth preserving. The migration deletes them and adds the column as
`NOT NULL`, which — together with the `Guid.Empty` guard in `Order.Place` — makes an unowned order
unrepresentable in both the model and the schema.

The alternative, backfilling to a seeded "legacy" user, buys safety this repo does not need yet and
costs a user row that exists only to own history, with a username and password hash nobody wants to
choose.

The migration leaves the 3 corresponding `order_audit` rows and any outbox rows behind, referencing
ids that no longer exist. Harmless in the absence of foreign keys, and preferable to a migration
that reaches into the outbox.

### No foreign key from `orders` to `users`

Not an oversight. No configuration in this repo declares one — `order_audit` already references
orders by bare id — so aggregates keep referencing each other by id only.

## The change, layer by layer

### Application (the port)

`src/Application/Abstractions/Ports.cs`, beside `IClock`:

```csharp
/// <summary>The signed-in caller, or null when there is no session — background work has none.</summary>
public interface ICurrentUser
{
    public Guid? Id { get; }
}
```

`src/Application/Orders/IOrderRepository.cs`:

```csharp
public Task<Order?> GetAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);

public Task<IReadOnlyList<Order>> ListAsync(
    Guid ownerId, int limit, (DateTimeOffset PlacedAt, Guid Id)? after, CancellationToken cancellationToken);
```

`AddAsync` is unchanged — the owner arrives on the entity.

### Domain

`Order` gains `public Guid UserId { get; private set; }`, and `Place` takes it:

```csharp
public static Order Place(Guid id, Guid userId, string sku, int quantity, DateTimeOffset placedAt)
```

with a guard beside the existing two:

```csharp
if (userId == Guid.Empty)
{
    throw new DomainException("An order needs a user.");
}
```

`userId` goes second, next to `id`, because both are identity rather than order content.

`OrderPlaced(OrderId, Sku, Quantity)` is **unchanged**. No consumer wants a user id —
`IOrderAuditWriter.RecordAsync(messageId, orderId, ct)` does not take one — and adding it would
change the outbox payload shape for no current reader.

### Application (the handlers)

All three handlers gain `ICurrentUser` and the same guard:

```csharp
if (currentUser.Id is not { } userId)
{
    return Result.Failure<T>(new Error(
        ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
}
```

The message matches `ChangePasswordHandler`'s existing wording for the same condition.

- `PlaceOrderHandler` passes `userId` into `Order.Place`.
- `GetOrderHandler` passes it to `GetAsync`. **No ownership branch:** the repository cannot return
  another user's row, so the existing `order.not_found` failure already produces the 404 decided
  above.
- `GetOrdersHandler` passes it to `ListAsync`, after its limit and cursor validation.

### Infrastructure

`OrderRepository` filters on the owner in both reads, ahead of the keyset predicate:

```csharp
context.Orders.AsNoTracking().Where(o => o.UserId == ownerId)
```

`OrderConfiguration` marks the property required and replaces the index:

```csharp
builder.Property(o => o.UserId).IsRequired();

builder.HasIndex(o => new { o.UserId, o.PlacedAt, o.Id })
    .IsDescending(false, true, true)
    .HasDatabaseName("IX_Orders_UserId_PlacedAt_Id_Desc");
```

`IX_Orders_PlacedAt_Id_Desc` is dropped. It cannot serve a query whose leading column is now the
filter.

Migration `AddOrderOwner`, in this order: delete every row from `orders`; add `UserId` as
`NOT NULL`; drop the old index; create the new one. A new migration, never an edit to an applied
one.

### Api

`src/Api/Auth/CurrentUser.cs`:

```csharp
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? Id =>
        Guid.TryParse(
            accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;
}
```

Registered in `Program.cs` with `AddHttpContextAccessor()` and `AddScoped<ICurrentUser, CurrentUser>()`.

`AuthController`'s private `CurrentUserId()` helper is deleted and the controller injects the port,
so the claim is parsed in exactly one place. This is the only change to `AuthController`.

**No DTO changes.** `PlaceOrderRequest` stays `{Sku, Quantity}` and no response exposes `UserId` —
the caller is always the owner, so it carries no information. `openapi/AiFramework.Api.json` and
`frontend/src/api/schema.d.ts` are therefore expected to regenerate byte-identical, and the
frontend needs no work at all. Regenerate and confirm rather than assuming.

## Testing

The bug this fixes is "user A can see user B's data", so the tests that matter are the ones that
put two users in play. Everything else is threading a parameter.

**Domain** (`tests/Domain.Tests/Orders/OrderTests.cs`) — `userId` threaded through the existing
`Place` calls; a new test that `Guid.Empty` throws `DomainException`; the existing
`Place_RaisesOrderPlaced` assertion stays as it is, since the event did not change.

**Application** (`tests/Application.Tests/Orders/`) — `ICurrentUser` substituted with NSubstitute:

- `PlaceOrder` records the current user's id on the order handed to `AddAsync`.
- `GetOrder` and `GetOrders` pass the current user's id to the port.
- Each of the three returns `Unauthorized` when `ICurrentUser.Id` is null.

**Infrastructure** (`tests/Infrastructure.Tests/Persistence/`) — against real Postgres, the tests
that would have caught this:

- `GetAsync` returns null for an order owned by a different user.
- `ListAsync` never returns another user's orders.
- Paging stays correct with two users' orders interleaved in time: user A's pages contain only A's
  orders, in the right order, with no rows skipped or repeated across the cursor boundary.

**Api integration** (`tests/Api.IntegrationTests/Orders/`) — two clients from
`CreateAuthenticatedClientAsync`, which already registers a distinct user per client: A places an
order; B gets 404 for that id and an empty list. The existing orders tests thread an authenticated
client already, so they need no change beyond what the shared factory gives them.

**e2e** (`frontend/e2e/orders.spec.ts`) — registers its own user through `sign-up.ts`, so it should
pass untouched. Verify rather than assume.

## Verification

- `/verify` — both stacks.
- Release build as well as Debug; CI does both and Debug alone has hidden a Release break in this
  repo before (ADR 0005).
- Regenerate the OpenAPI document and `schema.d.ts`, and confirm the diff is empty.
- No Wolverine handler changes, so `codegen write` should be a no-op; `WolverineCodegenTests` will
  say otherwise if that is wrong.

## Out of scope

- **`ChangePassword(UserId, ...)`** keeps taking the id as a command field, inconsistent with the
  pattern this spec establishes. Aligning it is a follow-up, not a reason to widen this diff.
- **Roles and an admin "see all" view.** The port's owner parameter is a deliberate speed bump: an
  admin listing across users will need a new method and a conscious decision.
- **Brute-force protection, session revocation, account lifecycle, data-protection key
  persistence** — the other gaps ADR 0006 and the audit that produced this spec identified. Each is
  its own slice.

## Follow-on

An ADR (0007) recording ownership-by-placing-user, the 404 choice, and the `ICurrentUser` seam.
ADR 0006 explicitly left this ground open, and a decision of this size is documented in this repo.
