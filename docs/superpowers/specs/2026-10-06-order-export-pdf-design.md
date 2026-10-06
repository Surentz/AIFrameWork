# Order export as a PDF, viewable in the app — design

**Date:** 2026-10-06
**Status:** Approved in conversation, section by section (2026-10-06). Awaiting review of this
written spec, then an implementation plan.
**Type:** `feat(orders)!`, one pull request on `claude/order-export-pdf`
**Builds on:** [the order export design](2026-10-03-order-export-design.md) and ADR 0029, which
stay in force for everything this document does not change.

## Intent

The order export (ADR 0029) produces a CSV. It becomes a **PDF meant to be shared**: something a
user sends to an accountant or a manager, which has to look presentable and stand on its own. The
user can also **read it inside the app**, in a viewer that works the same on desktop and phone,
as well as download it.

**Success means:**
- one click still produces an export of every order the user has placed, now as a PDF;
- the PDF is a presentable document, coloured like the app, that a reader with no access to the
  app understands;
- the user can open it on the exports page, on any device, without downloading it;
- every guarantee ADR 0029 makes (owner-only access, at-least-once safety, one notification,
  seven-day retention, failure by staleness) still holds.

### Decided in conversation

| Question | Decision |
|---|---|
| CSV as well, or instead? | **Instead.** CSV goes away entirely. |
| What is the PDF for? | **Sharing with someone else**: a formatted, stand-alone document. |
| How does viewing work? | **The same viewer on every device**: pdf.js through react-pdf, not the browser's built-in viewer (which most mobile browsers cannot embed). |
| PDF library | **PDFsharp + MigraDoc 6.2.4** (MIT, fully managed). Not QuestPDF (native Skia on a chiseled image with no fonts, revenue-based licence), not headless Chromium (a browser in the worker image). |
| Look | **Coloured to match the app's light theme**, with a brand header band, summary cards and status pills. |
| Delivery | **One pull request**, not split at the viewer. |

## 1. The document

**Page:** A4 landscape.

**Header band (page 1 only):** full width, solid brand indigo `#3b2fa8` (solid, not the app's
gradient: MigraDoc cannot fill a gradient, and a flat band prints reliably). On it, **Order
history** large in white bold; beneath, the owner's display name and "Generated 6 Oct 2026, 18:04
UTC" in softer white. **All times in the document are UTC**, said once here: the worker does not
know the reader's time zone.

**Summary: three cards** on pale indigo `#eef1ff`, each a small grey label over a large bold value:
- **Orders** — the count, with the period beneath ("12 Jan 2026 – 4 Oct 2026").
- **Total value** — quantity × unit price summed over every order that is **not cancelled and has
  a recorded price**. The card says "excludes cancelled orders"; when any order has no recorded
  price (`UnitPrice` is nullable) it adds "and N without a recorded price".
- **By status** — "Placed 12 · Shipped 30 · Cancelled 3".

**Table**, newest first (the orders page's order; the rows arrive oldest first and are reversed in
memory, which already holds them all):

| Placed | Order | Product | Qty | Unit price | Total | Status |
|---|---|---|---|---|---|---|

- **Order** is a short reference: the first 8 characters of the id, upper case (`3F2A9C01`). No
  full GUID appears anywhere in the document.
- **Product** is the product name, with the SKU beneath it in small grey. An order with no product
  name shows the SKU alone.
- **Unit price** and **Total** show `–` when there is no recorded price.
- **Status** is a coloured pill with the word inside it, so it reads in greyscale print and for
  colour-blind readers: Shipped `#0f6b3f` on `#f0fdf5`, Cancelled `#912018` on `#fef4f3`, Placed
  `#4f46e5` on `#eef1ff`. Beneath the pill: "Shipped 3 Mar 2026", or "Cancelled 5 Mar 2026" and the
  cancellation reason in small italic.
- The header row repeats on every page: small uppercase grey text on `#f8f9fb`. Rows are
  zebra-striped (white and `#f8f9fb`) with `#e4e7ec` hairlines. Numbers are right-aligned.
- A **grand total** row closes the table, in bold above a stronger `#cfd4dd` rule. It repeats the
  Total value card's figure and rule.

**Footer, every page:** a thin rule, then "Order history · Jane Doe · Page 2 of 5" in `#878e9b`.

**Amounts:** two decimals, invariant culture, with a thousands separator (`1,234.50`), and no
currency symbol, because the domain has no currency (the CSV printed amounts the same way).

**No orders:** the header band and a single card reading "No orders yet."; no table.

**Kept out on purpose:** full GUIDs, links, JavaScript, attachments and form fields. The document is
static text, tables and filled shapes, so nothing a user typed (product names, cancellation
reasons) can become active content. This replaces the CSV's formula-injection guard, which no
longer applies.

**Document metadata:** title "Order history", author the owner's display name, creator
"AIFrameWork".

**Font:** Noto Sans Regular and Bold, embedded (subset) by the renderer. It covers Latin, Greek and
Cyrillic. **Chinese, Japanese and Korean characters render as missing-glyph boxes**; this is
accepted and recorded in ADR 0030.

**Palette:** the hex values above are the light theme of `frontend/src/styles/tokens.css`, copied
into one class, `OrderExportPalette`, whose comment names that file. The backend cannot read CSS,
so the copy is deliberate; a palette change in the app does not reach the PDF by itself. Every text
and background pair the document uses meets WCAG AA (4.5:1), and a test holds it to that.

## 2. Backend

### Domain

`OrderExport.Content` (`string?`) becomes **`Document` (`byte[]?`)**. `Complete(byte[] document,
int rowCount, DateTimeOffset completedAt)` refuses a null or empty document and keeps today's
idempotency: completing a Ready export changes nothing and raises nothing. There is no format
column: every export is a PDF.

### Application

- **`OrderExportReport`** (record) and **`OrderExportReport.Create(rows, ownerDisplayName,
  generatedAt)`**, pure. It computes everything in section 1 that is data rather than layout: the
  newest-first row order, short references, counts by status, the total value and how many orders
  it excludes, the period, and whether the export is empty. Formatting of dates and amounts lives
  here too, so the renderer only places strings.
- **`IOrderExportRenderer`**, a port: `byte[] Render(OrderExportReport report)`.
- **`BuildOrderExportHandler`** pages the rows exactly as today, then reads the owner's display name
  through the existing **`GetUser(job.OwnerId)`** query, which returns `DisplayName` (the job
  already runs as the owner, through `JobUserMiddleware`), builds the report, renders it, and completes the export with the bytes.
  Every failure still throws, for Wolverine's retry policy to see.
- **`GetOrderExportFile`** returns the bytes and the file name `orders-yyyy-MM-dd.pdf` (the request
  date, as today).
- **Deleted:** `OrderExportCsv` and its tests.

### Infrastructure

- **`MigraDocOrderExportRenderer`** implements the port with PDFsharp/MigraDoc 6.2.4. All layout and
  colour lives here and in `OrderExportPalette`.
- **Fonts:** Noto Sans Regular and Bold as embedded resources, served by an `IFontResolver`, with
  the SIL Open Font License text committed beside the font files. PDFsharp's font resolver is a
  process-wide setting: it is set once, guarded so that the Api and the worker, and test hosts in
  one process, can all register it without throwing.
- **Registration:** in the shared Infrastructure registration, since the renderer is stateless; only
  the worker calls it.
- **Storage:** a new migration replaces `content text` with **`document bytea`**, and **deletes
  existing Ready exports**: they hold CSV text, are at most seven days old, cannot be served as
  PDFs, and are one click to request again. Exports still `Requested` are kept; their job now
  produces a PDF. A notification that points at a deleted export behaves as one for a pruned export
  already does (ADR 0029): the link opens the exports page and the download answers 404.

### API

`GET /api/orders/exports/{id}/download` stays the one file endpoint. It answers
**`application/pdf`**, `Content-Disposition: attachment; filename=orders-2026-10-06.pdf`, with the
same 404 for an export that is not the caller's or not built. The UTF-8 byte-order mark code goes.
`[ProducesResponseType]` changes to `application/pdf`, so the OpenAPI document and
`frontend/src/api/schema.d.ts` are regenerated. The viewer fetches this same URL; there is no new
endpoint. The controller's "nothing here may be stored by a cache" rule is unchanged.

### Rollout

The migration runs before new pods start (the cluster's migration Job; the dev database by
`dotnet ef database update`). Until the old pods are gone:
- a CSV-era worker fails writing the dropped `content` column; the job throws and is retried a
  minute later, by then on a new worker;
- a CSV-era API pod answers a download with 500 until it is replaced.

Accepted for this application, rather than a two-release migration (add `document`, ship, then drop
`content`).

## 3. Frontend

**Exports table:** a Ready row has **View** (a button) and **Download** (the existing link, now a
`.pdf`). Each carries a per-row accessible name ("View export requested …"), as Download already
does.

**The viewer is a native `<dialog>` opened with `showModal()`**, the app's first modal. The
platform supplies the focus trap, Esc to close, an inert page behind, and focus returning to the
View button on close. Desktop: a large centred panel over a dimmed backdrop. Below the existing
`40rem` breakpoint: full screen.

**Inside it:**
- **Toolbar:** the title ("Export requested 6 Oct 2026, 18:04"), "Page 2 of 5" for the page most in
  view, zoom out, zoom in, fit to width, Download, Close. It opens at fit-to-width and refits when
  the window resizes.
- **Body:** every page in one scrolling column, rendered by react-pdf with the **text layer on**
  (text can be selected and copied) and the annotation layer off (the document has no links).

**Fetching the file:**
- **`requestBytes(path)`** in `api/client.ts` returns an `ArrayBuffer` with the same `ApiError`
  handling as `request()`.
- **`useOrderExportDocument(id)`**, a TanStack Query hook keyed under `orderKeys`, enabled only while
  the viewer is open, with **`gcTime: 0`** so the bytes are dropped when it closes (the file is
  someone's order history) and no refetch while open (a Ready export never changes).
- pdf.js **takes ownership of (detaches) the buffer it is given**, so the viewer passes a copy;
  passing the cached buffer would break the viewer the second time it opens.

**Bundle:**
- The viewer is **`React.lazy`-loaded**, so pdf.js (about 1 MB) downloads only on the first View; the
  rest of the app is unchanged. This is the app's first lazy-loaded module.
- The pdf.js worker is bundled by Vite and served from the app's own origin, never a CDN.
- `frontend/nginx.conf` must serve the worker's `.mjs` with a JavaScript MIME type; the plan
  verifies this against the production image.
- `pdfjs-dist` is the version react-pdf 11 depends on; it is not pinned separately.

**States:**
- Loading: a spinner and "Loading export…".
- Fetch error (for example 404, pruned while the list was open): the existing `ErrorPanel`.
- pdf.js cannot draw the file: "This export couldn't be displayed" and the Download link.

**Copy:** the exports page subtitle becomes "A PDF of every order you have placed, ready to share.
You are notified when it is ready; exports are kept for seven days."

## 4. Testing

| Project | Covers |
|---|---|
| `Domain.Tests` | `Complete` refuses a null or empty document; completing a Ready export is a no-op |
| `Application.Tests` | `OrderExportReport`: newest-first order, short reference, total excluding cancelled and unpriced orders, the exclusion wording, counts by status, the period, the empty export, date and amount formatting |
| `Application.Tests` | `BuildOrderExportHandler` with the renderer substituted: reads the display name, renders the report, completes with the bytes, throws on every failure |
| `Infrastructure.Tests` | `OrderExportPalette`: every text/background pair the document uses has a WCAG contrast ratio of at least 4.5:1 |
| `Infrastructure.Tests` | The real renderer, read back with **PdfPig** (Apache-2.0, test-only): a valid PDF; title, owner name and SKUs present; a long export spans pages, each with "Page x of N" and the table header repeated; `Smørrebrød` and `Ελληνικά` come back as text (the font covers them) |
| `Infrastructure.Tests` | The document round-trips through `document bytea` |
| `Api.IntegrationTests` | Download: `application/pdf`, attachment `orders-….pdf`, body starts `%PDF-`; another user's export is still 404 |
| `Worker.IntegrationTests` | The existing delivery test ends with a Ready export holding a PDF |
| Vitest | View opens the dialog; the right URL is requested (through MSW, as the repo requires); loading and error states; Esc closes; focus returns to View; the subtitle |
| Playwright | Download is a `.pdf` starting `%PDF-`; View renders the first page, whose text layer contains "Order history" and the order's SKU |

**Vitest stubs react-pdf**, because pdf.js cannot draw in jsdom. The repo forbids mocking the *API
client* (`react-testing` skill: that seam sits above `fetch`); a rendering library is not that
seam, and the test says so in a comment. Real rendering is Playwright's job.

## 5. Docs

- **ADR 0030, "Order exports are PDFs rendered by MigraDoc":** the library choice and its reasons
  (licence, the chiseled image, no native code), the bundled font and its CJK limit, the Ready
  exports the migration deletes, and the viewer's pdf.js choice. It supersedes ADR 0029's
  CSV-specific consequences (format, formula guard, byte-order mark). **ADR 0029 still governs
  storage, the outbox, staleness and retention**, and gains a status line pointing to 0030.
- **README:** the export section.
- **`frontend/CLAUDE.md`:** the lazy viewer, the pdf.js worker and its MIME type, the detached-buffer
  gotcha, and why react-pdf is stubbed in unit tests.
- **Skills, commands and agents:** none mention the CSV export (checked 2026-10-06), so none change.

## Out of scope

- Keeping CSV, or a format choice.
- Per-user time zones or locales in the document.
- Fonts beyond Noto Sans (CJK coverage).
- Object storage, streaming, or a size cap (ADR 0029's large-export consequence is unchanged).
- Windowed rendering of very long documents in the viewer: every page renders. Revisit if exports
  run to hundreds of pages.
