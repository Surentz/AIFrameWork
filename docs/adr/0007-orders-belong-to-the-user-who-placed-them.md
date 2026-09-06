# 0007. Orders belong to the user who placed them

**Date:** 2026-09-06
**Status:** Accepted

## Context

ADR 0006 shipped authentication and said so explicitly: `[Authorize]` on `OrdersController` and
`AuthController` proves someone holds a session, and nothing more. It did not scope any data to
that session, and nothing in the codebase noticed. `PlaceOrder(Sku, Quantity)` took no user and
`Order` had no owner field; `GetOrders(Limit, Cursor)` listed every order in the database;
`GET /api/orders/{id}` served any order to any signed-in caller. The second account to register
could read the first account's orders in full.

No test caught this because every orders test used one user.
`ApiFactory.CreateAuthenticatedClientAsync` registers a fresh user per client, and each existing
test only ever read back what it had just written — nothing had asked whether one user could see
another's data, because nothing put two users in the same test.

## Decision

**Ownership is the placing user, set once.** `Order.Place(id, userId, sku, quantity, placedAt)`
takes a `userId` alongside the existing parameters, stores it in a private-set `UserId`, and
never reassigns it — the same shape as `Id` and `PlacedAt`. A `Guid.Empty` guard sits beside the
existing sku and quantity guards, so an unowned order is unrepresentable in the model. There is
no customer or organisation concept and no sharing; if one is ever wanted, it is a second field on
top of this one rather than a rewrite.

**Enforcement lives in `IOrderRepository`'s signature, not in the handlers.** `GetAsync` and
`ListAsync` both grew an `ownerId` parameter; there is no overload that can return an order the
caller does not own, so forgetting the filter on a future read is a compile error rather than a
leak. `AddAsync` was left alone — the owner arrives already set on the entity, so there is nothing
for it to be told separately. `GetOrderHandler` correspondingly has no ownership comparison of its
own: the repository cannot hand back someone else's row, so a cross-user id already produces the
same `order.not_found` failure as an id nobody ever issued, and the response is a 404. This is a
deliberate echo of ADR 0006, where the uniform sign-in error and the always-hashed password exist
so that a response never answers "does this exist?" — the same refusal, applied here to order ids
instead of usernames.

**The caller reaches handlers through a port, not through the command.** All three handlers —
`PlaceOrderHandler`, `GetOrderHandler`, `GetOrdersHandler` — inject `ICurrentUser` and read
`currentUser.Id`; the command and query records themselves are unchanged. `ICurrentUser` is an
Application port (`src/Application/Abstractions/Ports.cs`), a single `Guid? Id` property,
implemented in Api by `CurrentUser` (`src/Api/Auth/CurrentUser.cs`) over `IHttpContextAccessor`,
parsing the same `ClaimTypes.NameIdentifier` claim `AuthController` used to parse privately. That
private helper is deleted; the controller now injects the port too, so the claim is parsed in
exactly one place in the whole codebase.

**`ICurrentUser.Id` is nullable**, not a `Guid` that throws when there is no session. The outbox's
two `BackgroundService` pumps and `OrderPlacedAuditHandler` resolve DI scopes with no HTTP context
at all, so a non-nullable `Id` would have to throw there, and a thrown exception out of a handler
becomes a 500 through the global `IExceptionHandler`. Instead every handler starts with the same
guard — `if (currentUser.Id is not { } userId)` returning `ErrorKind.Unauthorized` — so a caller
whose session has gone stale gets a 401, not a crash. The error code and message are the ones
`ChangePasswordHandler` already uses for its own stale-session case (`auth.failed`, "That session
is no longer valid."), even though that handler reaches the check by a different route: it takes
`UserId` as a field on the command and fails when `IUserRepository.GetAsync` returns null, rather
than reading `ICurrentUser`.

**The migration deletes the existing rows rather than backfilling them.** The dev database on
55433 held three orders with no owner, and nothing in this repo is deployed, so there was nothing
worth preserving. `AddOrderOwner` deletes every row from `orders`, then adds `UserId` as `uuid NOT
NULL` with no default — there is no legacy user to backfill to and inventing one just to own
history buys safety this repo does not need yet. `IX_Orders_PlacedAt_Id_Desc` is dropped and
replaced with `IX_Orders_UserId_PlacedAt_Id_Desc` over `(UserId, PlacedAt DESC, Id DESC)`, because
the owner is now the leading equality predicate on every list query and the old index cannot serve
it. The corresponding `order_audit` rows and any outbox rows are left behind, referencing ids that
no longer exist — harmless with no foreign keys declared, and preferable to a migration that
reaches into the outbox as well.

**No foreign key from `orders` to `users`.** Not an oversight: no configuration in this repo
declares a foreign key between aggregates today, and `order_audit` already references `orders` by
a bare id with the same absence. `UserId` follows the convention already in place rather than
introducing a new one.

No Api DTO changed as part of this. `PlaceOrderRequest` is still `{Sku, Quantity}` and no response
exposes `UserId` — the caller is always the owner, so the field carries no information worth
returning. `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` were regenerated and
came back byte-identical; no frontend file needed touching.

## Consequences

The three development orders that predated this change are gone; the migration's `Down` cannot
restore them, and that is accepted.

`Order.Place` grew a parameter, and every call site across all four test projects had to thread a
`userId` through — `Domain.Tests`, `Application.Tests` (now substituting `ICurrentUser` with
NSubstitute), `Infrastructure.Tests` (new cases against real Postgres: `GetAsync` returns null for
another user's order, `ListAsync` never returns another user's rows, and paging stays correct with
two users' orders interleaved in time), and `Api.IntegrationTests` (two authenticated clients, one
places an order and the other gets a 404 for it and an empty list).

An admin "see all" view is not free to add later. The repository's owner parameter is a deliberate
speed bump: listing across users needs a new method and a conscious decision to add it, rather than
an existing overload that already has the reach.

`ChangePassword(UserId, CurrentPassword, NewPassword)` still carries its user id as a command
field, read from the claim inside `AuthController` and handed in — the same shape this ADR moves
away from everywhere else. It is now the one inconsistent case in the codebase rather than the
established pattern, and aligning it is a deliberate follow-up, not a reason to have widened this
change to touch authentication code that was working.

## Alternatives considered

**Filtering inside the handlers, leaving `IOrderRepository` untouched.** This is what a first
instinct suggests: load whatever the repository returns and compare `order.UserId` in
`GetOrderHandler`. It does not work for the list. `GetOrdersHandler` fetches `limit + 1` rows to
decide `hasMore` without a second `COUNT` query; discarding another user's rows from that page
after the fact produces short pages, `NextCursor` values computed from the wrong last row, and a
keyset that silently skips rows across the boundary. The list forces the filter into SQL
regardless of what the single-order read could get away with, so handler-side checking would have
bought an inconsistency between the two reads, not a simplification of either.

**An EF global query filter (`HasQueryFilter`).** Automatic and unforgettable, including for
queries nobody has written yet — an attractive property. Rejected because a global filter is
evaluated per `DbContext` instance against whatever it is configured to read at the time, and
`OutboxPollerService`, `OutboxWorkerService`, and `OrderPlacedAuditHandler` all resolve their own
scopes with no HTTP context to read a user from. Those paths would either silently see no rows at
all, or need `IgnoreQueryFilters()` called at each of them — and the absence of that call is
invisible right up until data starts going missing in production. The explicit `ownerId` parameter
makes the same guarantee visible in the signature instead of buried in model configuration.

**Putting `UserId` on the command record**, the shape `ChangePassword` already uses: read the claim
in the controller action, pass it in as a field. Rejected because it re-implements the security
boundary once per controller action instead of once, and because a command record with a
caller-supplied `UserId` property is one matching DTO field away from letting a request place an
order, or read one, as another user — nothing stops a client from setting that field to any guid it
likes if a future DTO or model-binding change ever exposes it. Reading the caller from a single
port gives the codebase one source of truth for "who is calling," at the cost of leaving
`ChangePassword` as the one place that still does it the old way.
