# 0030. Order exports are PDFs rendered by MigraDoc

**Date:** 2026-10-06
**Status:** Accepted

## Context

The order export (ADR 0029) produced a CSV. Users share their order history with people outside the
app, so the file has to be presentable on its own, and readable inside the app on any device,
including phones, where a CSV is not something anyone reads.

The worker that builds it runs in `aspnet:10.0-noble-chiseled`: no shell, no package manager, and
no system fonts. Whatever draws the document has to be managed code that carries its own font.

## Decision

**The export is an A4 PDF rendered in the worker by PDFsharp/MigraDoc 6.2.4** (MIT, managed), behind
`IOrderExportRenderer`, a port in Application implemented in Infrastructure, the only project that
references the library. `OrderExportReport` holds everything the document says, already formatted
(UTC dates, two-decimal amounts, the totals and their exclusions), so the renderer only places
strings and every rule is tested without drawing a page.

**Noto Sans (Regular and Bold, SIL OFL 1.1) is embedded in the Infrastructure assembly** and served
for **every** font family name. MigraDoc asks for "Courier New" as its own error font before it
draws anything, so a resolver that answered only for "Noto Sans" throws on the first render.
Italic is simulated.

**The file is stored as `bytea`.** `StoreOrderExportsAsBytes` drops the CSV `text` column and adds
`Document`, and deletes Ready exports first: they hold CSV, cannot be served as the PDF the API now
promises, are at most seven days old, and are one click to ask for again. Requested exports are kept;
their build now writes bytes.

**The app reads the PDF with react-pdf (pdf.js) in a native `<dialog>`.** The viewer is lazy-loaded
so pdf.js downloads only on the first View; the pdf.js worker is bundled and same-origin; nginx maps
`.mjs` to JavaScript, which its stock `mime.types` does not. The viewer fetches the same download
URL as bytes and hands pdf.js a copy, because pdf.js detaches the buffer it is given.

## Consequences

- **Text the font cannot draw renders as missing glyphs.** Latin, Greek and Cyrillic draw; CJK does
  not. It does not throw, and long text wraps inside its cell. A broader font is a size trade, not
  a bug.
- **The palette is a copy of `tokens.css`** (`OrderExportPalette`), because the backend cannot read
  CSS. A palette change in the app does not reach the PDF by itself; a test holds every text pair
  in the PDF to WCAG AA.
- **Old Ready exports are gone**, and a notification that still links to one lands on the exports
  page, where its download answers 404.
- **Rolling the deploy has a short seam.** A CSV-era worker's build writes a string the new column
  no longer holds (it fails and is retried), and a CSV-era API pod's download fails with a 500
  until it is replaced.
- **Every page renders in the viewer**; there is no windowing. An export of hundreds of pages is
  slow to open on a phone.
- **The formula-injection guard and the byte-order mark are gone with the CSV.** The PDF is static
  text and shapes: no links, scripts, attachments or form fields, so nothing a user typed becomes
  active content.
- The worker image carries one more dependency and about 1.2 MB of fonts.

## Alternatives considered

**QuestPDF.** Native Skia on a chiseled image with no fonts, and a revenue-based licence.

**Headless Chromium.** Best-looking output, but a browser in the worker image, which would no longer
be chiseled.

**The browser's built-in viewer.** Most mobile browsers cannot embed a PDF in a page, which was the
point of reading it in the app.

**Keeping the CSV beside the PDF.** Not wanted: two formats to build, store, test and explain.
