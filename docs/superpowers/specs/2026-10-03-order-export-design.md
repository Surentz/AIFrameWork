# Order export — design

**Date:** 2026-10-03
**Status:** Approved and built (2026-10-04). Recorded as ADR 0029. Where the build departed from
this document, the text below says so.
**Type:** `feat`, one pull request on `claude/order-export`
**Process:** built straight from this spec, test-first. There is no separate implementation plan
(decided 2026-10-03); the build order is in the [build order](#build-order) section.

## Intent

A signed-in user asks for a file of their own orders. A job in the worker builds it, and the user
is told it is ready through the notification feed, pushed live over SignalR. They download it from
a small Exports screen.

This is the feature ADR 0028 cleared the way for: "the worker finishes, the user is told" is
exactly the path whose push that ADR fixed (the worker writes the outbox row, an API replica
delivers it and pushes). It is also the first real use of the slow job queue.
`RebuildOrderReport` has been a placeholder there since ADR 0016: it pages through a user's orders,
logs a count, and is never enqueued.

**Success means:**
- one click produces a CSV of every order the user has placed;
- the user is told when it is ready, without polling, whichever host built it;
- no one can read another user's export;
- a lost or duplicated message never loses an export, and never produces two "ready" notifications;
- copies of someone's order history are deleted after a fixed retention period.

### Decided in conversation

| Question | Decision |
|---|---|
| Contents | **All** of the caller's orders, every status. No filters |
| Storage | **A Postgres table**; the CSV text is a column. No object storage |
| Retention | **7 days**, configurable as `OrderExports__RetentionDays` |
| Architecture | **Everything through the outbox** (approach A below) |
| Plan | **None**: build from this spec |

### Out of scope

Email delivery (the repository has no mail provider; `LoggingOrderNotifier` only logs), filters
of any kind, cancelling an export in progress, formats other than CSV, a size cap, and
administrators exporting other users' orders.

## Approach

Three wirings were considered.

- **A — everything through the outbox (chosen).** The request commits an export row and an
  `OrderExportRequested` outbox row together. A domain event handler enqueues the job. The job
  finishes through a command whose `OrderExportCompleted` event becomes the notification. This is
  the repository's rule for any job that must not be lost (root `CLAUDE.md`, "Jobs"). A request
  can't commit without its job, and a finished export can't exist without its notification.
- **B — enqueue the job straight from the request.** One hop shorter, but `IJobScheduler.EnqueueAsync`
  is not transactional with the command: a failed commit still runs a job for a row that does not
  exist, and a crash between the two loses the job.
- **C — the job writes the file and the notification itself.** Fewest parts, but the worker cannot
  push (ADR 0028), so "ready" would reach the user only on their next 30-second poll — the bug
  #86 fixed.

## Data flow

```
POST /api/orders/exports
  └─ RequestOrderExport (command)          API · request transaction
       ├─ an export already in progress? → return it (no new row, no event)
       └─ OrderExport.Request()            → row (Requested) + outbox: OrderExportRequested
                                                       │
        API outbox pump ◄──────────────────────────────┘
  └─ OrderExportJobEnqueuer                → IJobScheduler: BuildOrderExport(ExportId, OwnerId)
                                                       │  RabbitMQ, heavy lane
        worker ◄───────────────────────────────────────┘
  └─ BuildOrderExportHandler
       ├─ export already Ready? → stop (absorbs a duplicate enqueue)
       ├─ page GetOrderExportRows → OrderExportCsv.Build(rows)
       └─ CompleteOrderExport (command)    → row (Ready, Content) + outbox: OrderExportCompleted
                                                       │  written by the worker
        API outbox pump ◄──────────────────────────────┘
  └─ OrderExportReadyNotifier              → feed row (OrderExportReady) + SignalR push

GET /api/orders/exports                     list (no file content)
GET /api/orders/exports/{id}/download       text/csv, owner only
PruneOrderExports (daily 03:40, worker)     deletes rows older than RetentionDays
```

## Domain — `src/Domain/Orders/OrderExport.cs`

An aggregate deriving from `Entity`:

| Member | Notes |
|---|---|
| `Id`, `UserId`, `RequestedAt` | `required`; set by `Request` |
| `Status` | `OrderExportStatus.Requested` or `Ready`. **Failed is never stored** — see below |
| `CompletedAt?`, `RowCount?` | set by `Complete` |
| `Content?` | the CSV text; set by `Complete` |
| `static Request(Guid userId, DateTimeOffset now)` | raises `OrderExportRequested(ExportId, UserId)` |
| `Complete(string content, int rowCount, DateTimeOffset now)` | `Requested` → `Ready`, raises `OrderExportCompleted(ExportId, UserId, RowCount)`. **On an export that is already `Ready` it does nothing and raises nothing** — jobs are delivered at least once, and one export must never produce two notifications |
| `IsFailed(DateTimeOffset now)` / `IsInProgress(now)` | `Requested` and older / younger than `StaleAfter` |
| `static readonly TimeSpan StaleAfter = 45 minutes` | see "Failure" |

The domain has no clock, so `now` is passed in, as `Order.Cancel` already does. A negative row
count or null content is a broken invariant (`DomainException`).

### Failure, and why 45 minutes

A job that throws is retried by the worker's global policy — after 1, 5 and 30 minutes
(`JobRegistration`) — and then dead-lettered. Catching the failure to store a `Failed` status would
need `catch (Exception)`, which is not allowed here. So instead, **an export still `Requested`
after `StaleAfter` reads as Failed**: the list endpoint reports it that way, and it no longer
counts as in progress, so asking again creates a fresh export.

`StaleAfter` must outlast the retry schedule, or the screen would say Failed while a retry was
still coming, and "Try again" would start a second build alongside it. 1 + 5 + 30 = 36 minutes of
retry waits, plus time for the builds themselves, makes **45 minutes**. (The conversation first
said 15 minutes; that was before the retry schedule was checked.) A test in `Infrastructure.Tests`
pins `StaleAfter` above the sum of the retry delays, so changing one without the other fails the
build.

If a late retry does succeed, `Complete` still moves the export to `Ready` and the user is
notified: the status only ever moves forward. The dead letter remains visible on the monitoring
page either way.

## Persistence — `src/Infrastructure`

- Table `order_exports`: `Id` (pk), `UserId`, `Status` (text, stored by name like
  `NotificationKind`), `RequestedAt`, `CompletedAt` null, `RowCount` null, `Content` text null.
  Index `(UserId, RequestedAt desc)`. *As built:* columns keep EF's default names, as every other
  table here does, and there is **no foreign key** to `users`, because no table in this repository
  declares one.
- `OrderExportConfiguration` maps it; EF migration **`AddOrderExports`** (generated, never
  hand-edited once committed).
- `IOrderExportRepository` (Application port), implemented in `Persistence/`:
  - `AddAsync(export)`;
  - `GetInProgressAsync(userId, now)` → the caller's export that is `Requested` and younger than
    `StaleAfter`, if any;
  - `GetForUpdateAsync(id, userId)` → tracked, owner-scoped;
  - `ListAsync(userId)` → metadata projection, **never `Content`**;
  - `GetContentAsync(id, userId)` → `Content` of a `Ready` export owned by the caller, else null.
- `IOrderExportRetention` (port), `PruneAsync(olderThan)` with `ExecuteDeleteAsync`, the same
  shape as `ISignInEventRetention`. It prunes by `requested_at`, so stale (failed) exports go too.
- `OrderExportOptions` (`OrderExports:RetentionDays`, default 7, at least 1) bound and validated at
  startup, like the other retention settings.

## Application — `src/Application/Orders/`

*As built:* flat in `Orders/`, as every feature folder here is, rather than an `Exports/` subfolder.
One query was added to the table below: `GetOrderExport(Id)`, through which the build job reads the
export's status before doing any work.

| Type | Kind | Notes |
|---|---|---|
| `RequestOrderExport` | `ICommand<OrderExportView>` | Returns the caller's in-progress export if one exists; otherwise creates one. No validator (no input) |
| `CompleteOrderExport(ExportId, Content, RowCount)` | `ICommand<bool>` | Loads the caller's export for update; `NotFound` if it is not theirs; calls `Complete`. Validator: `RowCount >= 0` |
| `GetOrderExports` | `IQuery<IReadOnlyList<OrderExportView>>` | Newest first. **Not `ICacheable`** |
| `GetOrderExportContent(Id)` | `IQuery<OrderExportFile>` | `NotFound` unless the caller's and `Ready`. **Not `ICacheable`** |
| `GetOrderExportRows(Limit, Cursor)` | `IQuery<OrderExportRowPage>` | Keyset-paged, owner-scoped through `ICurrentUser` exactly like `GetOrders`. Its own query because `OrderListItem` lacks the shipped/cancelled fields |
| `OrderExportView` | record | `Id, Status ("Requested" \| "Ready" \| "Failed"), RequestedAt, CompletedAt?, RowCount?` — Failed derived from `IClock` |
| `OrderExportCsv` | static, pure | Builds the CSV from rows (below) |
| `BuildOrderExport(ExportId, OwnerId)` | `IUserScopedJob`, **heavy** lane | **Replaces `RebuildOrderReport`** and its log-only `IOrderReportWriter`/`LoggingOrderReportWriter` |
| `OrderExportJobEnqueuer` | `IDomainEventHandler<OrderExportRequested>` | Enqueues `BuildOrderExport`. A redelivery enqueues twice; the job's early exit absorbs it |
| `OrderExportReadyNotifier` | `IDomainEventHandler<OrderExportCompleted>` | Writes an `OrderExportReady` notification via `NotificationFanOut` (saves its own work; idempotent on `(SourceMessageId, UserId)`) and pushes |
| `PruneOrderExports` | `IJob`, light lane, scheduled `0 40 3 * * ?` | Deletes exports older than `RetentionDays` |

Every command, query and validator is registered in `AddMessaging()`, and both jobs in the `Jobs`
list and `IncludeJobHandlers`; the existing completeness tests fail on any that are missed.

`BuildOrderExportHandler` reads the export's status before doing anything (stopping quietly if it
is Ready or gone, and throwing on any other failure, `Unauthorized` above all, which is what a
missing caller produces), pages
`GetOrderExportRows` at its maximum page size (500), builds the CSV, and dispatches
`CompleteOrderExport`. It throws on any failed `Result`, never returns quietly, and writes no log
of its own (`Behaviors.LoggedAsync` and Wolverine already do).

`NotificationKind` gains **`OrderExportReady`**, appended. Kinds are stored by name, so the name is
a stored contract. Title "Your order export is ready"; body "{n} orders, ready to download.";
`SubjectId` = the export id.

### The CSV — `OrderExportCsv`

- Header and one row per order:
  `OrderId,Sku,Product,Quantity,UnitPrice,Total,Status,PlacedAt,ShippedAt,CancelledAt,CancellationReason`.
- RFC 4180: comma-separated, CRLF line endings, a field quoted when it contains a comma, quote, CR or
  LF, with embedded quotes doubled. UTF-8 **with** a byte-order mark, so Excel reads non-ASCII
  product names correctly. *As built:* the mark is added by the download endpoint, not stored, so
  the stored text stays plain.
- Timestamps ISO 8601 UTC (`2026-10-03T19:05:01Z`); decimals with `.` whatever the server's culture
  (`CultureInfo.InvariantCulture`); empty fields for nulls. `Total` = `UnitPrice × Quantity`, empty
  when the price is unknown.
- **Formula-injection guard:** `Sku`, `Product` and `CancellationReason` are typed by users. A value
  starting with `=`, `+`, `-`, `@`, tab or CR is prefixed with `'` so a spreadsheet shows it as text
  instead of evaluating it.

## API — `src/Api/Orders/OrderExportsController.cs`

`[Route("api/orders/exports")]`, `[Authorize]` (any signed-in user; no capability policy, since it
reads only the caller's own data).

| Endpoint | Success | Failure |
|---|---|---|
| `POST /api/orders/exports` | **202 Accepted**, `Location` → the list, body `OrderExportResponse` | 401 |
| `GET /api/orders/exports` | 200, `OrderExportResponse[]` | 401 |
| `GET /api/orders/exports/{id}/download` | 200, `text/csv; charset=utf-8`, `Content-Disposition: attachment; filename="orders-YYYY-MM-DD.csv"` (the request date, UTC) | **404 if it is someone else's or not Ready** — the same answer for both, so an id reveals nothing (ADR 0007) |

Every response carries `[ProducesResponseType]`, so the contract and `schema.d.ts` are regenerated.
Responses send `Cache-Control: no-store`.

## Frontend — `frontend/src`

- Route **`/orders/exports`** → `OrderExportsPage`, declared **before `/orders/:id`** (otherwise
  "exports" is read as an order id — the trap `/products/new` already avoids). A header link,
  **Exports**, sits next to Orders.
- The page: an **Export my orders** button (disabled while an export is in progress), then a table of
  exports: requested at, status, orders, and a **Download** link (`<a href="/api/orders/exports/{id}/download">`;
  the session cookie is same-origin, so no blob handling) when Ready. When the newest export
  Failed, the button reads **Try again**.
- `api/orders.ts`: `requestOrderExport()`, `listOrderExports()`. `features/orders/types.ts`: aliases
  over the regenerated `schema.d.ts`. `features/orders/queries.ts`: `orderExportKeys`,
  `useOrderExports()` — **refetches every 5 s only while an export is in progress**, so the screen
  reaches Ready without push — and `useRequestOrderExport()`, which invalidates the list.
- `NotificationList`'s exhaustive `subjectPath` switch gains `OrderExportReady` → `/orders/exports`.
- Every query and mutation renders its error state through `ErrorPanel`.

## Testing

| Project | Covers |
|---|---|
| `Domain.Tests` | `Request` raises its event; `Complete` moves to Ready and raises once; a second `Complete` is a no-op; `IsFailed`/`IsInProgress` either side of `StaleAfter` |
| `Application.Tests` | `OrderExportCsv` — header, quoting, CRLF, BOM, invariant decimals, UTC timestamps, nulls, the formula guard for each leading character; `RequestOrderExport` returns the in-progress export instead of a new one; `BuildOrderExportHandler` pages to the end, completes, stops early when Ready, throws on a failed page; `CompleteOrderExport` not-found; the enqueuer; the notifier; `OrderExportView`'s derived Failed |
| `Infrastructure.Tests` | mapping and migration; `GetOrderExportRows` keyset paging and owner scoping; `ListAsync` never loads `Content`; retention prune; `OrderExport.StaleAfter` exceeds the sum of `JobRegistration`'s retry delays (it lives here because the delays are Infrastructure's, which `Domain.Tests` cannot see — the retry schedule becomes a named constant for it) |
| `Api.IntegrationTests` | 202 and the returned in-progress export on a second POST; list; download content type, filename and body after draining the outbox and completing; 404 for another user's export and for one not Ready; 401 signed out; the `OrderExportReady` notification after `DrainOutboxUntilEmptyAsync` |
| `Worker.IntegrationTests` | `BuildOrderExport` reaches its handler on the heavy lane and leaves an `OrderExportCompleted` outbox row; `JobDeliveryTests` and anything else that used `RebuildOrderReport` move to it; `PruneOrderExports` is scheduled |
| Vitest | `OrderExportsPage`: in progress (button disabled), Ready (download link), Failed (Try again), list error; the route beats `/orders/:id`; `NotificationList` links `OrderExportReady` |
| Playwright | `e2e/specs/orders/exports.spec.ts`, **`@local-only`** (needs the worker, like `jobs.spec.ts`): request, wait over HTTP until Ready, download, and assert the CSV contains the order's sku. Runs as `workerUser`, so it spends no auth permits |

Durable Wolverine, outbox draining, and the RabbitMQ containers follow `tests/CLAUDE.md` as they
stand; nothing new is needed in either factory.

## Generated artifacts

In this order (the `regenerate` skill): the EF migration `AddOrderExports`; both Wolverine adapter
trees (`src/Api` for the new event handlers, `src/Worker` for the jobs); then the OpenAPI document
and `schema.d.ts`, before the frontend uses the new endpoints.

## Documentation

- **ADR 0029 — Order exports are stored in Postgres and fail by staleness**: the storage choice,
  `StaleAfter` derived from the retry schedule instead of a stored Failed status, and the 7-day
  retention of personal data.
- `RebuildOrderReport` → `BuildOrderExport` wherever it is named as the reference heavy job: the
  `jobs` skill, `/job`, `src/Worker/CLAUDE.md`, `tests/CLAUDE.md`.
- `notifications` skill: the kinds list. README: the API table and the routes table.

## Build order

Test-first throughout; each step ends green.

1. Domain: `OrderExport`, its events, `OrderExportStatus`, `NotificationKind.OrderExportReady`.
2. `OrderExportCsv` (pure).
3. Persistence: configuration, repository, retention, options; generate `AddOrderExports`.
4. Application: queries, commands, validators, enqueuer, notifier, registrations.
5. Jobs: `BuildOrderExport` replacing `RebuildOrderReport`; `PruneOrderExports`; regenerate the worker
   tree.
6. API: controller and DTOs; regenerate the Api tree, the contract and `schema.d.ts`.
7. Frontend: API calls, types, queries, page, route, nav, notification link.
8. e2e spec.
9. ADR 0029 and the documentation updates.
10. `/verify`, `dotnet-reviewer` and `react-reviewer`, then the PR.
