# The assistant for everyone — design

**Date:** 2026-10-11
**Status:** Draft for review.
**Type:** this document is `docs(assistant)`; the build is two `feat` pull requests (§6).
**Builds on:** `docs/superpowers/specs/2026-10-11-ops-assistant-design.md` and ADR 0033 (the ops
assistant, built in #116 and #117), ADR 0020 (the Admin role), ADR 0024 (capability policies).
Everything there stays in force unless this document changes it; where they disagree, this
document wins.

## Intent

The ops assistant shipped as an administrator's tool, reached from the Monitoring pages. It should
be **everyone's** assistant: any signed-in user opens it from an icon in the header, beside the
notification bell and their name, and gets a full page that works like Claude desktop — a sidebar
of past conversations and a chat column with formatted answers.

A member asks about **their own** orders and exports and about the shared catalogue and population
statistics. An administrator can ask all of that and keeps the eight monitoring tools.

**Success means:**
- every signed-in user can open the assistant from the header and ask a question;
- a member's assistant can read that member's own orders and exports and nobody else's, enforced
  by a test;
- an administrator's assistant can still use every monitoring tool, and loses them on the next
  question after a demotion;
- past conversations are listed in a sidebar for 30 days after their last message, and can be
  reopened, continued, renamed and deleted;
- answers render as Markdown, and no HTML from the model ever reaches the page.

### Decided in conversation

| Question | Decision |
|---|---|
| Who may use it | **Every signed-in user.** |
| What a member can ask about | **Their own orders and exports, plus shared data** (catalogue, population statistics). Admins additionally keep the monitoring tools. |
| Shape | **One assistant whose toolbox is chosen by the asker's capability on every question** (approach 1). Rejected: two separate assistants (duplicates the loop, the stream and the history); frontend-only (the API refuses non-admins). |
| History retention | **30 days after the last message**, and the user can delete a conversation sooner. |
| Answer formatting | **Markdown** via `react-markdown` + `remark-gfm`, raw HTML disabled. |
| Conversation titles | **The first question**, trimmed to 60 characters; the user can rename. No model-written titles. |
| Entry point | **An icon button labelled "Assistant"** between the notification bell and the user's name. (The header has no profile icon; this is the nearest place to "beside the profile icon".) |
| Look | **Style B, "app-native"**: Claude desktop's layout in the app's own tokens (indigo accent, cool greys, sans-serif). Mockup: `.superpowers/brainstorm/*/content/assistant-page.html` (not committed). |

## 1. Access and the toolbox

### Who reaches the endpoints

`AssistantController` moves from `[Authorize(Policy = Monitoring.Read)]` to plain `[Authorize]`
(any signed-in user). This covers `GET /api/assistant`, `POST /api/assistant/messages` and the new
history endpoints (§3). The `assistant` rate limit stays at 20 questions per user per hour; the
Console workspace's spend limit stays the hard cap.

### Which tools a question gets

Application cannot see roles: `ICurrentUser` exposes only `Id`, and capabilities are checked at the
API (ADR 0024). So:

- `AskAssistant` gains `ToolScope Scope` — `Member` or `Operator`.
- The controller sets it from `IAuthorizationService.AuthorizeAsync(User, Monitoring.Read)`. The
  policy reads the role fresh from the database on every request (ADR 0022), so a demoted
  administrator gets `Member` on their very next question.
- `AssistantToolbox.Definitions` becomes `AssistantToolbox.For(ToolScope)`. `Member` gets the
  member tools; `Operator` gets the member tools plus the eight monitoring tools.
- `RunAsync` refuses a tool outside the question's scope with an `is_error` result, so a model that
  names a monitoring tool it was not offered gets nothing.
- The system prompt gains a scope section: a member's assistant is told it helps with the user's
  own orders, exports, the catalogue and population statistics; the operator section keeps today's
  monitoring guidance.

### The member tools

Each dispatches an **existing** query through `IQueryDispatcher`, and each query is already scoped
to `ICurrentUser` where the data is personal. The tool inherits "only your own rows" from the query,
the same way the monitoring tools inherit "only what the Monitoring page shows".

| Tool | Query | Returns (projection in `Assistant/ToolResults/`) |
|---|---|---|
| `my_orders` | `GetOrders(Limit, Cursor)` | Page of the user's orders: id, sku, quantity, status, placed at |
| `get_order` | `GetOrder(Id)` | One order: id, sku, product name, quantity, unit price, total, status, placed at. Another user's id → `NotFound` → `is_error` |
| `my_exports` | `GetOrderExports` | The user's exports: id, status, requested at, completed at, row count |
| `list_products` | `GetProducts(Limit, Cursor)` | Page of the catalogue: id, sku, name, price |
| `population` | `GetPopulation(Area)` | The population figures the Population page shows |

`GetProducts` has no search, so `list_products` pages (25 per page, as the monitoring tools do);
search is a later change if answers need it. No member tool writes, and no member tool reads
another user's anything.

### The personal-data boundary (ADR 0034)

ADR 0033 said no user's personal data leaves for Anthropic. That changes, narrowly: **the asker's
own order and export data may leave, for the asker's own question.** Nothing about any other user
does, and identifiers still do not:

- Member projections carry no `UserId`, `OwnerId`, `Username`, `IpAddress` or `UserAgent`, so the
  existing `AssistantToolResultsBoundaryTests` keeps passing over them unchanged.
- The monitoring tools keep their projections and their scrubbing (`OperationalText.Scrub`).
- The question text and the stored transcript are now personal data of every user who asks, held
  30 days (§2).

ADR 0034 records this and supersedes the "operational data only" row of ADR 0033; the rest of
0033 stands.

## 2. Conversations and retention

- **`assistant_conversations` gains `Title`** (`varchar(60)`, not null). A new migration adds it,
  filling existing rows with `'Conversation'`. Set from the first question, trimmed to 60
  characters at a word boundary with `…`, when the conversation is created. Never derived from the
  model.
- **Retention is 30 days after `LastActiveAt`.** `AssistantConversationRetention.Retention` goes
  from 24 hours to 30 days; the worker's existing `PruneAssistantConversations` job does the rest.
- **Transcripts stay append-only** (ADR 0033). Rename changes only the conversation row; delete
  removes the row and its turns; nothing rewrites a stored turn.
- **The 20-question cap per conversation stays.** A full conversation's reply stays
  `assistant.conversation_full`, and the page offers **New chat**.

## 3. History endpoints

All under `[Authorize]`, all scoped to the caller: another user's id is `404`, the same as a
missing one, so ids cannot be probed.

| Method and path | Returns |
|---|---|
| `GET /api/assistant/conversations` | The caller's conversations, newest `LastActiveAt` first: `{ id, title, lastActiveAt }`. At most 30 days of them, so no paging. |
| `GET /api/assistant/conversations/{id}` | `{ id, title, messages: [{ role, text, tools[] }] }` — the display projection |
| `PATCH /api/assistant/conversations/{id}` | Body `{ title }` (1–60 characters). `204`. |
| `DELETE /api/assistant/conversations/{id}` | `204`. |

**The display projection** is read, never written: Application asks a new port method
(`IAssistantConversations.ReadForDisplayAsync`) and Infrastructure walks the stored turns through
`StoredBlocks`. A user turn that carries the question becomes a `user` message; a user turn that
carries only `tool_result` blocks is not shown. Every assistant turn between two questions folds
into one `assistant` message: its text blocks joined in order, its `tool_use` names collected as
`tools`. Thinking blocks, tool inputs and tool results are not shown. A turn whose blocks it
cannot read is skipped, never rewritten.

New queries and commands follow the repo's pattern (`GetAssistantConversations`,
`GetAssistantConversation`, `RenameAssistantConversation`, `DeleteAssistantConversation`), with
validators, DTOs with DataAnnotations, and `[ProducesResponseType]` so the contract carries them.
None is `ICacheable`: the list changes with every question.

## 4. Frontend

### Entry point and routes

- **Header icon.** In `AppLayout`'s `shell__session`, between `NotificationBell` and the display
  name: an icon-only `NavLink` to `/assistant`, `aria-label="Assistant"`, a line chat-bubble SVG
  (inline, `currentColor`), highlighted while on an assistant route. Rendered only when
  `useAssistantStatus()` reports `enabled`; no error branch, for the reason the Monitoring tile had
  none (an optional feature never puts an error panel in the shell).
- **Routes**, under `RequireAuth` and not `RequireRole`: `/assistant` (a new chat) and
  `/assistant/:conversationId` (a saved one).
- **Removed:** the Monitoring overview's Assistant tile. `/monitoring/assistant` becomes a
  `<Navigate to="/assistant" replace />`.

### Layout (style B)

A full-height page inside the shell (the page fills the viewport below the header; only the
message column scrolls).

- **Sidebar**, about 260px, `--color-surface-sunken` with a right border:
  - **New chat** at the top (accent text, a plus icon), going to `/assistant`.
  - Conversations grouped **Today**, **Previous 7 days**, **Previous 30 days** by `lastActiveAt`,
    each row the title, ellipsized; the active row in `--color-accent-soft`.
  - A **⋯** menu per row: **Rename** (inline input; Enter saves, Escape cancels) and **Delete**
    (a confirmation, then back to `/assistant` if it was open).
  - Below 768px the sidebar collapses behind a toggle button in the chat header.
- **Chat column**, centred, `max-width: 48rem`:
  - The user's message: a rounded bubble in `--color-accent-soft`, aligned right.
  - The answer: full-width prose, no bubble, rendered as Markdown (below), with its tool chips
    above it ("Checked your orders", "Checked job health", …) and "Thinking…" until the first word.
  - Stopped / capped / truncated notes as today.
- **Empty state** (`/assistant`, no messages): "How can I help you today?" and three suggestion
  chips that fill the composer: for everyone "Where is my latest order?", "Which of my exports are
  ready?", "Show me the catalogue"; an operator's third chip is "Did any background job fail
  today?".
- **Composer**, pinned to the bottom of the column: a rounded box with an auto-growing textarea
  (up to about 8 lines, then it scrolls) and a round send button (accent, arrow-up icon) that turns
  into a stop button (a square icon, labelled "Stop") while an answer streams. **Enter** sends;
  **Shift+Enter** inserts a newline. The character count appears only past 1,800 of 2,000. The note
  under it reads **"Answers are generated by Claude from your data. Check before acting."**
- Everything is styled with the existing tokens in `styles/tokens.css`; no UI library.

### Markdown

`react-markdown` with `remark-gfm` (tables, strikethrough, task lists), no `rehype-raw`, so raw HTML
in a model's answer renders as literal text. Links get `target="_blank"` and
`rel="noopener noreferrer"`. Code blocks are plain monospace (no highlighter). The renderer lives
in its own component in `features/assistant`, so the dependency is imported in one place.

### State

- `useAssistantConversations()` (TanStack Query, `assistantKeys.conversations()`) feeds the
  sidebar. Rename and delete are mutations that invalidate it; so does every `done`.
- `/assistant/:conversationId` loads `GET …/{id}` through
  `useAssistantConversation(conversationId)` (a query) and seeds the existing reducer with its
  messages and id; new questions then stream through the existing `askAssistant` and reducer,
  unchanged.
- A new chat's first `done` replaces the URL with `/assistant/<id>` (`navigate(…, { replace: true })`)
  without remounting the conversation, so the stream is never cut.
- The parser, `askAssistant`, the abort rules (Stop, New chat, leaving the page) and the error
  handling all carry over from #117.

## 5. Tests

**No test calls Anthropic.**

| Project | Proves |
|---|---|
| `Application.Tests` | `AssistantToolbox.For`: `Member` gets exactly the five member tools, `Operator` those plus the eight monitoring tools. A monitoring tool named in a `Member` question is an `is_error`. Each member tool dispatches its query and projects it; bad input is refused. The boundary test covers the new projections. The title is the first question, trimmed at a word boundary. The new queries and commands. |
| `Infrastructure.Tests` | The 30-day prune. The list returns only the owner's conversations, newest first. Rename changes only the conversation row (turn payloads byte-identical before and after). Delete removes the conversation and its turns. The display projection from real stored transcripts, with thinking blocks and tool rounds. The migration. |
| `Api.IntegrationTests` | A member can `GET /api/assistant` and ask (200); anonymous is 401. A member's question runs with `Member` scope and an admin's with `Operator`, read through the fake Messages API's recorded tool list. A member's `get_order` for another user's order returns `is_error`. Another user's conversation id is 404 on GET, PATCH and DELETE. |
| Vitest + MSW | The header icon renders for a member when enabled and not when disabled. The sidebar groups by date; rename and delete invalidate it; delete of the open conversation returns to `/assistant`. Opening `/assistant/:id` renders its history. A new chat's first `done` moves the URL. Enter sends; Shift+Enter does not. Markdown renders a table and a list; raw `<script>`/`<img onerror>` in an answer renders as text. `/monitoring/assistant` redirects. |
| Playwright | As a member (`signedInPage`, not the admin): open the assistant from the header icon, ask, see the answer and a tool chip; the conversation appears in the sidebar; after a reload it reopens with its history. The fake's script gains a member path (a `my_orders` call, then an answer). |

## 6. Delivery

Two `feat` pull requests against `main`, not stacked (memory: #115 merged into its base branch
and missed `main`):

1. **`feat(assistant)!: open the assistant to every user, with history`** — §1–3 and their tests:
   `[Authorize]`, `ToolScope`, the member tools, the prompt's scope section, the history endpoints,
   the `Title` migration, 30-day retention, ADR 0034, the regenerated API contract, the `assistant`
   skill and the CLAUDE.md "Rules that fail silently" entry (members' own data may leave; nobody
   else's). Breaking (`!`): the endpoint's audience changes.
2. **`feat(assistant): a Claude-desktop-style assistant page for everyone`** — §4, its Vitest
   tests and the Playwright spec. Opened after PR 1 merges.

## Out of scope

- Model-written conversation titles; search across conversations; pinning or folders.
- Any tool that writes (placing or cancelling an order, retrying a dead letter).
- Other users' data in any form, including for administrators outside the monitoring tools.
- Syntax highlighting in code blocks; attachments; voice.
- Sharing a conversation with another user.
- Conversation history beyond 30 days.
