# Orders linked to the catalogue

**Date:** 2026-09-12
**Status:** Implemented

## Context

ADR 0013 shipped the product catalogue as a vertical slice deliberately disconnected from
orders. Its own commit message says so: "Orders is otherwise untouched: `Order.Sku` stays free
text and `PlaceOrder` does not validate against the catalogue."

So the two aggregates share a vocabulary and nothing else. An order's sku is whatever the caller
typed, in whatever case they typed it, naming a product that may never have existed. The live dev
database makes the gap concrete:

| orders | products | orders whose sku matches a product |
|---|---|---|
| 2 | 1 | 0 |

The orders are `Razor` and `TooTH`; the only product is `FFD`. `TooTH` also shows the second half
of the problem — `Order.Sku` preserves the typed case while `Product.Sku` is normalized
upper-case by `Product.NormalizeSku`, so even a real match would not compare equal without
folding.

This spec connects them: an order may only be placed for a product in the catalogue, and it
records what that product was at the moment it was placed.

## Decisions

### The order snapshots the product; it does not reference it

`PlaceOrder` refuses an unknown sku, and the order stores the product's name and unit price as
they were at that instant. The alternative — storing only the sku and joining to `products` on
read — was rejected because a later `UpdateProduct` would silently rewrite history: an order
placed at 3.00 would start displaying whatever the product costs today.

A database foreign key to `Product.Id` was also rejected. It couples two aggregate roots at the
storage layer, which is the thing DDD most consistently argues against, and it would permanently
block deleting a product. A soft reference carries the useful part of the relationship without
the rigidity.

This choice has a consequence worth stating on its own, because it is the reason the feature is
cheap: **the snapshot removes any need for cross-aggregate cache invalidation.** `GetOrders` and
`GetOrder` stay `ICacheable`, `UpdateProduct` keeps evicting only product entries, and repricing
a product cannot stale a cached order page, because the order does not read the product. Had we
taken the join, every product write would have had to evict order caches for every user who had
ever ordered it — which `CacheScope`'s per-caller tagging cannot express at all.

### The new invariant is unconditional; only persistence is nullable

`Order.Place` takes a **non-nullable** `OrderedProduct`. From here on there is no such thing as
an order without a catalogue product, and the domain says so without qualification.

`Order.Product` is nonetheless declared `OrderedProduct?`, for one reason only: the table already
holds rows written before the rule existed, and EF must materialize them with something. The
nullability is a fact about history, not a weakening of the invariant, and confining it to the
persistence boundary keeps the domain honest.

### `OrderedProduct` is a value object

`record OrderedProduct(Guid ProductId, string Name, decimal UnitPrice)`, in `Domain/Orders`,
validating in its constructor that the name is non-empty and the price is non-negative and
within `Product.PriceScale`. A record because it has no identity: two snapshots with the same
three values are the same snapshot.

Keeping the three fields in one object is what makes "all three or none" structural rather than a
convention `Order` has to police. The alternative of three loose nullable scalars on `Order` was
the fallback if EF's optional-owned-type mapping proved hostile; it does not, because
`OrderedProduct`'s properties are all non-nullable CLR types and EF uses exactly those to decide
whether the dependent is present. `OptionalDependentWithAllNullPropertiesWarning` applies to
dependents with no required properties, which this is not. **Confirm this in the first
implementation step regardless** — warnings are errors in this repo, and a wrong guess here is
better found immediately than after the mapping is wired through four layers.

### The sku comes from the product, not from the caller

`PlaceOrderHandler` looks the product up by `Product.NormalizeSku(command.Sku)` and then passes
`product.Sku` to `Order.Place`. The stored sku is therefore the catalogue's own normalized form,
and it cannot disagree with the snapshot beside it. This delivers sku normalization without a
second normalization step, and without `Order` needing to know the normalization rule at all.

### An unknown sku is a validation failure

`ErrorKind.Validation`, code `orders.unknown_sku`, message naming the normalized sku. Not
`NotFound`: from the caller's position this is a bad value in a submitted field. In practice the
form renders it as a generic banner rather than beside the field — `PlaceOrder`'s error carries no
per-property `Details`, unlike an invalid quantity's FluentValidation failure — which is accepted
rather than fixed: the picker makes this error near-unreachable in the first place, which was
always the stronger argument for it than field-level placement.

The check-then-insert has no uniqueness guard behind it, unlike `CreateProduct`'s, whose comment
explains that the unique index is the real defence. That asymmetry is deliberate, not an
oversight: the only race here is a product deleted between the check and the insert, and the
catalogue has no delete. If one is ever added, this is the site that needs revisiting.

`OrderPlaced` is unchanged. Adding `ProductId` is tempting and no consumer needs it; the outbox
contract is not worth migrating for a field nothing reads.

### Existing rows are backfilled where they match, and left alone where they do not

One additive migration: three nullable columns, then

```sql
UPDATE orders o
SET "ProductId"   = p."Id",
    "ProductName" = p."Name",
    "UnitPrice"   = p."Price",
    "Sku"         = p."Sku"
FROM products p
WHERE upper(trim(o."Sku")) = p."Sku";
```

A matched row has its sku rewritten to the catalogue's form too, so that sku and snapshot agree
for every row that carries one. This does edit historical user input, which is accepted on the
grounds that a row carrying a catalogue link should be internally consistent.

Rows that do not match keep their sku and their nulls, and the UI renders them exactly as it does
today — the bare sku, no price. This is the honest outcome: we do not know what those cost, and
inventing a number from today's catalogue would assert something false. On the current data the
backfill matches nothing at all; it exists for a deployment where orders and the catalogue have
genuinely overlapped.

**Additive-only is also what satisfies ADR 0010.** Through Phase B and the rollout that follows,
the previous generation's pods keep inserting orders that set none of these columns. Nullable
columns let those inserts succeed instead of erroring for the length of the rollout — but nothing
revisits those rows afterward. There is no follow-up backfill once the rollout completes, so an
order placed during that window stays snapshot-less permanently, exactly like a genuinely
pre-catalogue order. That is accepted, not an oversight: it is a short, bounded window, and the
alternative is a second migration for a handful of rows nobody has asked to fix.

### The form offers the catalogue instead of accepting a string

`PlaceOrderForm` replaces the free-text sku input with a `<select>` fed by the existing
`useProducts()`, showing name, sku and price, with a line total computed client-side once a
quantity is entered. Making the invalid-sku error nearly unreachable is better than rendering it
well: the set of valid answers is known and finite, so asking the user to guess it was always the
weaker design.

Three states the form does not have today and now needs: loading, query error, and an **empty
catalogue** — select disabled, submit disabled, and a link to "Add a product". Without it the
form is unusable and gives no reason why.

`useProducts` is an infinite query, so the picker fetches pages until `hasNextPage` is false.
That is correct at this size and wrong eventually: a `<select>` stops being the right control
somewhere in the hundreds of products. That is the point at which a search endpoint and an
autocomplete — considered and set aside here — become worth their cost. Recorded so the next
reader meets a known limit rather than a surprise.

`OrderDetail` and `OrderList` show `productName` when present and fall back to the bare `sku`
when null. Unit price and line total appear only when present. Detail links to
`/products/:productId` when there is a snapshot.

## Shape of the change

| Layer | Change |
|---|---|
| `Domain` | `OrderedProduct` value object; `Order.Product`; `Order.Place` takes the snapshot and sets `Sku` from it |
| `Application` | `PlaceOrderHandler` gains `IProductRepository`, looks up, fails with `orders.unknown_sku`; `OrderView`/`OrderListItem` gain three nullable fields |
| `Infrastructure` | `OrderConfiguration.OwnsOne`; one additive migration with the backfill |
| `Api` | `OrderResponse`/`OrderListItemResponse` gain three nullable fields; regenerate the OpenAPI document and `schema.d.ts` |
| `frontend` | `PlaceOrderForm` becomes a picker with loading/error/empty states; `OrderDetail`/`OrderList` render the snapshot with a sku fallback |

No new index. "Orders for a product" is not a query anyone makes, and adding one for it now would
be speculative.

## The ripple through existing tests

This is larger than the feature and belongs in the plan rather than being discovered inside it.
`Order.Place`'s signature change breaks **21 call sites across 7 test files**, and **7 API
integration test files** post orders with arbitrary skus the catalogue will now reject.

Three helpers keep it mechanical rather than a rewrite:

- a domain test helper returning a default `OrderedProduct`, so each of the 21 sites is a
  one-argument edit;
- an integration-test helper that creates a catalogue product and returns its sku, so every
  "place an order" setup is one call;
- the same in `frontend/e2e/fixtures/api.ts`, which already has `createProduct` from ADR 0013.

A sharp edge that turned out not to be one, recorded because the reasoning is worth keeping:
**`frontend/e2e/specs/orders/validation.spec.ts` posts `SKU-E2E-INVALID` with quantity 0** to
assert the quantity message, and this spec originally claimed that after the catalogue check it
would fail on the *sku* instead and pass for the wrong reason. That is wrong. `Behaviors.cs`'s
validation step runs ahead of the handler and returns a failure `Result` the moment
FluentValidation fails, so a quantity of 0 never reaches the catalogue lookup at all. The spec
was given a real product anyway — harmless, and it stops depending on that ordering — but the
test was never at risk. The general lesson stands even though this instance did not: a test whose
setup becomes invalid under a new rule can start passing for a new reason, and the validation
pipeline's ordering is what decides whether it does.

## Testing

| Layer | Cases |
|---|---|
| `Domain` | `OrderedProduct` rejects an empty name, a negative price, a third decimal; `Order.Place` refuses a null snapshot; `Sku` is taken from the product |
| `Application` | Unknown sku returns `orders.unknown_sku`; a lower-case sku matches an upper-case product; the snapshot carries the product's name and price |
| `Infrastructure` | Snapshot survives a round-trip; a row with all three columns null materializes as `Product == null` |
| `Api` | `POST` with an unknown sku is 400 with the right code; a placed order reads back with its snapshot; legacy rows serialize with nulls |
| `frontend` | Picker renders options and submits the selected sku; empty-catalogue state disables submit; detail falls back to the sku when the snapshot is null |
| `e2e` | Place an order by selecting from the catalogue; `validation.spec.ts` fixed to use a real product |

No test asserts a cached order changes after a product edit, because it must not: that
non-behaviour is the snapshot decision working.

## Alternatives considered

Argued where the decisions are made above: joining on read rather than snapshotting, a hard
foreign key, three nullable scalars instead of a value object, backfilling unmatched rows from
today's prices, and keeping the free-text sku input with a rendered server error. A search
endpoint with autocomplete is deferred rather than rejected — named above as the exit when the
catalogue outgrows a `<select>`.

See also ADR 0013, whose deliberate decoupling this spec closes, ADR 0009 for the caching
behaviour that the snapshot leaves untouched, and ADR 0010 for the rollout obligation the
additive migration satisfies.
