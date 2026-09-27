# 0024. Capability-named policies, and the operator ships

**Date:** 2026-09-27
**Status:** Accepted

**Supersedes** ADR 0019's *"Shipping is owner-scoped, and that is a stopgap with a sharp edge"*
paragraph, and only that paragraph. ADR 0013's "writable by any signed-in caller" still stands:
the catalogue lock-down is a separate change (see *Consequences*).

## Context

ADR 0020 introduced the `Admin` role and one policy, `Monitoring`, and ADR 0022 added the screen
that grants it. Three earlier decisions had been parked "until roles exist", and nothing had
picked them up:

- **ADR 0019** — `POST /api/orders/{id}/ship` let a buyer ship their own order. `Order.Ship` is
  terminal, so that permanently blocked the operator's legitimate ship: there was no transition
  left to make. ADR 0019 named `ShipOrder` as "the one command whose authorization changes when
  roles land".
- **ADR 0007** — every `IOrderRepository` read takes an `ownerId`, so no administrator could see
  which orders were waiting to ship. Listing across owners "needs a new method and a conscious
  decision".
- **ADR 0013** — the catalogue is writable by any signed-in caller.

A second pressure came from the policy's own name. `Monitoring` is fine for one area, but
fulfilment is not monitoring, and naming a policy after a page or a role (`"Admin"`) would mean
renaming it at every call site the day ADR 0020's exit is taken: "`Role` becomes one input to a
requirement handler … the policy name at the call sites does not change". That promise only holds
if the names describe what an endpoint allows rather than who happens to be allowed it today.

## Decision

**Policies are named for capabilities, all mapped to `Admin` in one place, and shipping moves
behind one of them.**

### Capability-named policies

`AuthorizationPolicies` in `src/Api/Auth` replaces `Monitoring` with five names:

| Policy | Allows | Applied to |
|---|---|---|
| `Orders.Fulfil` | Seeing every buyer's waiting orders, and shipping them | `FulfilmentController` |
| `Catalogue.Manage` | Creating and editing products | Nothing yet — see *Consequences* |
| `Monitoring.Read` | Everything beneath `api/monitoring` that only inspects | Each monitoring controller |
| `Monitoring.Operate` | Retrying a dead letter, triggering a job | Those two actions, on top of `Monitoring.Read` |
| `Users.Manage` | Granting the role, ending someone's sessions | `MonitoringUsersController` |

Program.cs registers every name in `AuthorizationPolicies.All` in a single loop, each as
`RequireAuthenticatedUser().RequireRole(Admin)`. Nobody's access changes. The policy stays at
**controller** level so an action added later is gated by default; `Monitoring.Operate` is the one
applied per action, so those two writes need both it and `Monitoring.Read`.
`AuthorizationPolicyTests` reflects over the constants and fails on one that is unregistered,
missing from `All`, or admits a member.

### The operator ships

- `ShipOrder` is admin-scoped. Its only caller is `FulfilmentController`
  (`api/fulfilment/orders`, `[Authorize(Policy = Orders.Fulfil)]`), and `OrdersController` no
  longer has a ship action. **Cancelling stays with the buyer** (ADR 0019).
- `IOrderRepository` gains two **explicitly named** cross-owner methods: `GetForFulfilmentAsync`
  (tracked, by id) and `ListForFulfilmentAsync` (by `OrderStatus`, keyset-paged, **oldest first**,
  because a fulfilment queue is FIFO). The owner-scoped methods are unchanged. ADR 0007's property
  — a caller cannot forget the owner filter — survives because there is still no overload that
  drops it: the cross-owner reads are separate names, visible at the call site in review.
- The queue joins each order's buyer username with a LEFT join. There is no foreign key between
  `orders` and `users`, and a missing user row must not hide an order from the queue that ships it.
  `IX_Orders_Status_PlacedAt_Id` serves it.
- `GetOrdersToFulfil` is **not** `ICacheable`. An operations view has to be current; a thirty-second
  stale queue shows an order another operator has just shipped as still waiting.

### The cache consequence, accepted

`IInvalidatesCache` evicts the **caller's** entries only, and HybridCache is L1-only per pod
(ADRs 0009, 0010). When an administrator ships someone else's order, the eviction reaches the
administrator's cache, not the buyer's: the buyer's cached `GetOrder`/`GetOrders` can show
*Placed* for up to the thirty-second TTL. This is the same limit ADR 0013 accepted for the
catalogue, for the same reason: an unscoped eviction path would give up the property that makes
this cache safe to use without thinking. `ShipOrder` keeps its tags, so an administrator shipping
their **own** order sees it at once. The `OrderShipped` notification is unaffected: it is written
by the outbox pump, and the feed is never cached.

### A permission model is still declined

ADR 0020 declined named permissions and roles composed of them, and this ADR does not revisit that.
The capability names are the *seam* for one, not the start of it. **The trigger** is a person who
needs to fulfil orders or manage the catalogue without being a full administrator — someone who
should ship but must not grant roles. At that point `Role` becomes one input to a requirement
handler behind these same names, and no controller changes.

## Consequences

- A buyer can no longer block fulfilment by shipping their own order. The old route answers 404.
- An operator has a queue: every buyer's waiting orders, oldest first, with who placed them, and a
  ship action with a confirmation that names the order and the buyer.
- A new privileged endpoint must pick, or add, a capability. `[Authorize(Roles = "Admin")]` would
  compile and work, and it is exactly what this rules out; review and the `auth` skill carry that.
- **The buyer's view can lag the ship by up to thirty seconds** in production. Tests do not see
  it, because the cache is off under test.
- The e2e suite needs an administrator to ship, so the shipped-notification test and the fulfilment
  queue tests are `@local-only`. The buyer's cancellation test was split out to keep running on the
  kind cluster.
- **`Catalogue.Manage` is defined but not applied.** Applying it means every e2e spec that creates
  a product needs an administrator, which is about 16 of the 26 tests a kind run executes, and only
  the Playwright-managed stack names one. That lock-down is its own `feat(products)!:` change, which
  will supersede ADR 0013's "writable by any signed-in caller".
- Five policies that are one policy today is some ceremony. It is paid once, here, rather than at
  every call site later.

## Alternatives considered

**One `Admin` policy, applied everywhere.** Least code today. Rejected because it names the role,
not the capability, so ADR 0020's exit would mean touching every controller. That is the
rename this ADR exists to avoid.

**`[Authorize(Roles = "Admin")]` on the new controller.** The same objection, more directly: the
role name at the call site is precisely what the permission model would have to find and replace.

**Owner-scoped ship, plus an admin override inside the handler.** The Application layer has no
notion of roles (`ICurrentUser` carries an id and nothing else), so this would mean either a new
port for "is the caller an administrator" or leaking the role into Application. It would also keep
the buyer able to ship their own order, which is the defect.

**A `bool crossOwner` parameter on the existing repository methods.** Rejected for the reason
`IOrderRepository` already gives about tracking: a flag at the call site is invisible in review,
and the whole of ADR 0007's guarantee is that forgetting the owner cannot compile.

**Caching the fulfilment queue with a short TTL.** Cheap reads for a screen an operator refreshes.
Rejected: two operators working the same queue would each see orders the other had shipped, and
the second ship would answer 409.

**Bundling the catalogue lock-down here.** Rejected for this change: the e2e cost above is large
enough to deserve its own PR, and either way of paying it (naming the e2e administrator in the kind
overlay's `Admin__Usernames`, or tagging most of the kind run `@local-only`) is a decision in its
own right.
