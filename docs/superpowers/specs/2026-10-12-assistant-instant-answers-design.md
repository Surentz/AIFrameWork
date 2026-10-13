# Assistant: instant answers before the AI

**Type:** `feat(assistant)`, delivered as two PRs, each opened against `main` (PR 2 after PR 1 merges).
**Amends:** ADR 0033 (an ops assistant on Claude) and ADR 0034 (the assistant for every user).

## Rulings made in the plan

These replace what the sections below say wherever they differ.

1. **Separate endpoint, not a `mode` field.** The rate limiter runs before model binding, so quick
   and AI questions could not get separate limits. Quick: `POST /api/assistant/quick` (120 an hour);
   AI: `POST /api/assistant/messages`, unchanged (20 an hour, 404 when the AI is off). There is no
   `assistant.ai_disabled` 400.
2. **No automatic fall-through to the AI.** When the router is unavailable the quick endpoint says so
   and offers *Ask the AI*; it never calls Claude itself.
4. **In-house spelling corrector** (edit distance over the catalog's own vocabulary), not SymSpell
   and a common-English list; a correction is used only when it produces an instant match.
5. **No trigram index yet.** Product search uses `pg_trgm`'s `word_similarity` through raw SQL; the
   migration enables the extension only. Revisit past roughly 10,000 products.
7. **The index is built at start-up** (a hosted service awaiting the build), not lazily.
8. **"Signing out everywhere" replaces "cancelling an order"** as a help article: members have no
   cancel button.

Known gap: the evaluation set holds 88 questions, not about 150.

## Intent

The assistant exists to help customers do their work in the application. Their questions are
application-specific ("has my last order shipped?", "is my export ready?"), and the answers must be
**fast** and **as precise as possible**. Today every question goes through Claude's tool-use loop:
seconds per answer, and every figure is restated by a model.

This change answers most questions **without generating text**. A local, in-process intent router
recognises the question, runs the same read-only tool the AI would have used, and returns a
templated answer built from the query's own values, with a link to the screen where the work
happens. Claude stays available for the long tail, **only when the user asks for it**.

Success:

- A recognised question is answered in well under a second, every figure in it comes straight from
  a query, and it links to the right screen.
- An unrecognised question never gets a guessed answer: it gets suggestions, and the user may then
  ask the AI.
- CI fails if the evaluation set shows any question answered instantly with the **wrong** intent.
- Administrators can see which questions were missed, so the catalog improves over time.

## Decisions (agreed in brainstorming)

| Topic | Decision |
|---|---|
| What the assistant may do | **Read-only: answer and link to the right screen.** No tool dispatches a command (ADR 0033 stands). |
| Low confidence | **Suggestions first** ("Did you mean…" chips), then **Ask the AI** on request, which runs today's Claude loop for that question only. |
| Language | **English only** for now. Content lives under an `en/` folder so Danish can be added later; no language detection is built. |
| How intent is recognised | **Nearest neighbour over example phrases**, embedded by a small local model, with a **threshold and a margin**. Not keyword rules (brittle), not a trained classifier (no labelled data yet). |
| Where the model runs | **In-process**, ONNX Runtime, behind an Application port, **API host only**. |
| Vector storage | **In memory. No vector database.** About 250 vectors at launch (under 400 KB, microseconds to search). See "Growth" for when that changes. |
| Curation | Intents, example phrases and help articles are **files in the repo**, reviewed in PRs and gated in CI. No in-app editing yet. |
| Spelling | **A correction pass from day one** (in-house edit-distance corrector, ruling 4), plus fuzzy matching of product and area names. |
| Badges | Kept: every reply says whether it is an *Instant answer*, *Help*, *Not sure* or an *AI answer*. |
| Links | One primary link per answer. |
| Administrators and question text | Administrators may read the text of **missed and thumbs-down questions**, scrubbed, read live from the transcript, gone when the conversation is deleted or expires. |

Mockup (gitignored): `.superpowers/brainstorm/325-1791830762/content/assistant-quick-answers.html`.

## Architecture

```
POST /api/assistant/quick     { conversationId?, question }      (ruling 1)
POST /api/assistant/messages  { conversationId?, question }      (the AI, unchanged)
        │
        ▼
AskAssistantHandler (messages) ─────────────────────────────► Claude tool loop (unchanged)
AskQuick (quick)
        ▼
SpellingCorrector ──► IntentRouter ──► ITextEmbedder (local ONNX model)
                         │  best phrase in memory, threshold + margin, original vs corrected
                         ├─ intent ───► ParameterResolver ──► AssistantToolbox.RunAsync ──► QuickAnswer
                         ├─ help ─────► help section ─────────────────────────────────────► QuickAnswer
                         └─ unsure ───► Suggestions (canonical questions of the closest intents)
```

### Application (`src/Application/Assistant/Quick/`)

- **`ITextEmbedder`** (new port): `Task<Result<float[]>> EmbedAsync(string text, CancellationToken)`.
  Fails with `ErrorKind.Unavailable` when the model is not loaded.
- **`IntentCatalog`**: the intents, written in C#. Each declares its id, scope (`Member` or
  `Operator`), the tool it runs, how it builds that tool's input from resolved parameters, its answer
  template, its link pattern and its **canonical question** (the text a suggestion chip sends).
  Intents are code because each is bound to a tool and a resolver, and the compiler should check
  that binding. Example phrases and help articles are **data files embedded in the assembly**
  (`Quick/Catalog/en/phrases.json`, `Quick/Catalog/en/help/*.md`).
- **`PhraseIndex`**: the in-memory vectors and the brute-force cosine search
  (`System.Numerics.Tensors.TensorPrimitives`). It is the single seam a vector database would
  replace later; nothing else knows how vectors are stored.
- **`SpellingCorrector`**: An in-house edit-distance corrector over the catalog's own vocabulary
  (phrases, intent and screen names; ruling 4). It corrects only words it does
  not know, and never touches ids, SKUs, numbers or quoted text.
- **`IntentRouter`**: embeds the original question and, if the corrector changed it, the corrected
  one; scores both against the index; keeps the better. A match is **instant** only if its score
  is at least `Threshold` **and** beats the best phrase of any *other* intent by at least
  `Margin`. Otherwise the result is *unsure*, carrying the closest distinct intents for
  suggestions. Help sections compete in the same index, so "how do I…" questions land on help.
- **`ParameterResolver`**: rules, not generation.
  - Order: a GUID or its short prefix in the question; "my last order" resolves by query to the
    newest order.
  - Time windows: "today", "in the last hour", "this week" map to the tools' `window_hours`.
  - Product: the new `FindProducts` query (below), by name or SKU, fuzzy.
  - Population area: matched in memory against `GetPopulationAreas`, with English aliases
    (Copenhagen → København), fuzzy.
  - A value that cannot be resolved, or two candidates that tie, ends in **suggestions or a
    "Did you mean X or Y?" reply**. Nothing is guessed.
- **`QuickAnswerComposer`**: runs the intent's tool through the existing **`AssistantToolbox`**,
  so per-user scoping, `ToolScope` and the `ToolResults` projections are exactly as for the AI.
  It fills the intent's template from the projection and builds the answer: a sentence, optional
  facts (label/value pairs) or a table, the link, and *Answered as* when the correction won.

### Infrastructure (`src/Infrastructure/Assistant/Quick/`)

- **`OnnxTextEmbedder`**: ONNX Runtime with an English sentence-embedding model of the
  bge-small / MiniLM class (MIT or Apache-2.0 licensed; the exact model is chosen in the plan, by
  the evaluation set). Tokenised with `Microsoft.ML.Tokenizers`. Loaded once.
- **`QuickCatalogLoader`**: at startup, reads the embedded phrases and help sections, embeds them,
  builds the `PhraseIndex` and the spelling dictionary.
- **`FindProducts`** query support: `pg_trgm` `word_similarity` on product name and SKU, with no index yet (ruling 5). The
  migration enables the extension; `pg_trgm` ships with the standard Postgres images, including `postgres:17-alpine`).
- Registered **in the API host only**, like `ICurrentUser`. The worker never answers questions
  and must not load the model.

### API

- `GET /api/assistant` reports both switches, `{ quick, ai }`, instead of one `enabled` flag. The
  page and the header icon read it.
- `POST /api/assistant/quick` answers quick questions (ruling 1); `POST /api/assistant/messages`
  stays the AI endpoint and is a 404 when the AI is off. There is no `mode` field.
- The SSE stream gains two events alongside the existing ones:
  - `answer`: `{ kind: "instant" | "help", text, answeredAs?, facts?, table?, link: { label, to }, turn }`
  - `suggestions`: `{ text, options: string[], aiAvailable, turn }`
- The API contract changes, so `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts`
  are regenerated (`regenerate` skill).

## Intents at launch

| Intent | Scope | Example | Tool | Answer | Link |
|---|---|---|---|---|---|
| `order_status` | Member | "Has my last order shipped?" | `get_order` (or newest via `my_orders`) | Sentence + facts | `/orders/{id}` |
| `recent_orders` | Member | "Show my recent orders" | `my_orders` (first 5) | Table | `/orders` |
| `export_status` | Member | "Is my export ready?" | `my_exports` (latest) | Sentence + facts | `/orders/exports` |
| `product_info` | Member | "How much is the Widget?" | `FindProducts` (new) | Facts | `/products/{id}` |
| `population` | Member | "Population of Copenhagen" | `population` | Sentence | `/statistics/population` |
| `job_health` | Operator | "Any failed jobs today?" | `job_health` (24 h) | Facts | `/monitoring/jobs` |
| `failed_jobs` | Operator | "Which jobs failed?" | `list_job_runs` (Failed) | Table | `/monitoring/jobs` |
| `dead_letters` | Operator | "Anything dead-lettered?" | `list_dead_letters` | Table | `/monitoring/jobs` |
| `traffic` | Operator | "How is the API doing?" | `traffic_summary` (1 h) | Facts | `/monitoring/traffic` |
| `integrations` | Operator | "Are the partner systems up?" | `external_systems` | Table | `/monitoring/integrations` |
| `sign_ins` | Operator | "Any lockouts?" | `sign_in_health` (24 h) | Facts | `/monitoring/logins` |

**Help articles at launch**: placing an order, exporting your orders, signing out everywhere (ruling 8; replaces cancelling an order), changing your password, notifications, the catalogue. One Markdown
file each, with a title, a link and sections; **each section is indexed separately**, and a help
answer quotes the matched section verbatim.

An operator gets every intent; a member gets member intents only. An operator intent never
appears in a member's suggestions.

`FindProducts(string Text, int Limit)` is a new Application query (internal; no API contract
change of its own), answered with trigram similarity. A best match above the similarity bar is
answered and named ("Widget Pro, the closest match"); a tie or a weak match asks "Did you mean…".

## Conversations, limits and transcripts

Transcripts stay **append-only** (ADR 0033). Two new turn types:

- **`QuickAnswerTurn`**: the intent or help id, the score, and the answer **as rendered**: text,
  facts or table, link, *answered as*.
- **`SuggestionsTurn`**: the suggestion text and options.

Both store a **replay text**, fixed at write time. When a later *Ask the AI* sends the conversation
to Claude, the request mapper sends that replay text as a plain assistant message, verbatim.
**A stored turn is never re-rendered from a template**: a template change must not change history,
because Claude rejects a history whose earlier turns changed. User and assistant turns alternate
as Claude requires (question → quick answer; question → suggestions → question again → AI answer).

Limits:

| | Today | After |
|---|---|---|
| AI questions per user per hour | 20 | 20 (AI mode only) |
| Quick questions per user per hour | — | 120 (abuse bound only) |
| Questions per conversation | 20 | 50 in total, of which at most 20 AI |
| Tool rounds per AI question | 8 | 8 |
| Retention | 30 days after the last question, deletable by owner | unchanged |

The cap of 50 exists because the whole conversation is replayed to Claude on *Ask the AI*.

## Feedback and insights (PR 2)

- **Ratings**: 👍/👎 on every answer. `PUT /api/assistant/conversations/{id}/turns/{n}/rating`
  with `up`, `down` or `null`; owner only (another user's conversation is a 404). Stored in a new
  `assistant_ratings` table, never in the transcript.
- **Outcome log**: one `assistant_outcomes` row per question: conversation id, turn, time, path
  (`instant`, `help`, `suggested`, `ai`), intent or article id, best score, margin, whether the
  spelling correction won, scope. **No question text and no user id.** Rows are deleted with their
  conversation (by the owner, or by the 30-day retention), so the insights page covers at most the
  last 30 days of activity. That is accepted: it keeps the log from outliving the transcript it
  describes.
- **Assistant insights** (`/monitoring/assistant-insights`, administrators only; an entry in the
  Operations group of `frontend/src/navigation.ts`, so it reaches the sidebar and the dashboard):
  - instant-answer rate over time, by path and by intent;
  - **the misses list**: questions that ended in suggestions or AI, and every 👎. The question text
    is **read live from the transcript** and passed through `OperationalText.Scrub`; no copy is
    kept, so it disappears when the owner deletes the conversation or it expires.
- This is new access to personal data, so ADR 0037 records it as an amendment to ADR 0034.

## Failure behaviour

| Situation | Reply |
|---|---|
| A parameter cannot be resolved, or two candidates tie | Suggestions or "Did you mean…". Never a guess. |
| The order is not the caller's, or does not exist | "I couldn't find order 3f2a… among your orders" + **All orders →** (the same not-found as today's tools). |
| The tool's query fails | "Your orders couldn't be loaded right now" + the screen's link. No silent switch to the AI. |
| The model did not load | The router reports itself unavailable. The quick endpoint says instant answers are unavailable and offers *Ask the AI* (ruling 2); it never calls Claude itself. **`/health/ready` is not affected**: the assistant must never take a pod out of rotation. |
| The spelling correction wins but is doubtful | It is held to the same threshold and margin; if it fails them, the reply is suggestions. When it wins, the answer shows *Answered as: …*. |

Logging follows the repo rule: `Behaviors.LoggedAsync` already logs the command; the question text
is never logged.

## Model delivery

- The model and its vocabulary are **pinned by version and SHA-256**.
- `scripts/fetch-assistant-model.ps1` (also called by `scripts/install-prereqs.ps1`, by CI's
  `backend` job and by the `e2e` workflow, whose API must answer quick questions) downloads them
  into a git-ignored folder and verifies the hash.
- `Dockerfile.api` copies them into the API image. Nothing is downloaded at runtime.
- A test that needs the model **fails** when the model is missing; it never silently skips.

## Frontend (`frontend/src/features/assistant`)

- The conversation reducer handles `answer` and `suggestions` events.
- An answer renders as a **typed card**: badge, sentence, facts or table, *Answered as* when
  present, and one primary link. It is built from structured data, **never Markdown or HTML**.
  The link is a router `Link` whose path must match an allow-list of route patterns; anything
  else is not rendered as a link.
- Suggestion chips send their canonical question to the quick endpoint, which always answers instantly.
- **Ask the AI** re-sends the question to the AI endpoint; shown only when `aiAvailable`.
- 👍/👎 (PR 2) call the rating endpoint.
- Badges: *Instant answer*, *Help*, *Not sure what you mean*, *AI answer*.
- The header's assistant icon shows when **either** quick answers or the AI is enabled (today: the
  AI only).

## Configuration

| Key | Meaning | Default |
|---|---|---|
| `Assistant__Quick__Enabled` | The router answers `auto` questions. Needs no Anthropic key. | `true` in dev and e2e |
| `Assistant__Quick__Threshold` | Minimum similarity for an instant answer | set from the evaluation set |
| `Assistant__Quick__Margin` | Minimum lead over the next intent | set from the evaluation set |
| `Assistant__Quick__ModelPath` | Folder holding the model and vocabulary | the image's model folder |
| `Assistant__Enabled` (existing) | Now means *Ask the AI is available* | unchanged |
| `RateLimiting__AssistantQuick__PermitLimit` | Quick questions per user per hour | 120 |

## Testing

- **Catalog tests (Application)**: every intent has at least 10 phrases and a canonical question;
  no phrase appears under two intents; every link pattern matches a real route; every intent's
  tool exists in its scope; every help article has a link and at least one section.
- **Router tests (Application)**: threshold and margin decisions with a fake embedder of
  hand-made vectors; original vs corrected question; help sections competing with intents; a
  member never routed to an operator intent.
- **Resolver tests (Application)**: order ids and prefixes, "last order", time windows, product
  and area ties.
- **Composer tests (Application)**: each intent against a substituted query dispatcher: the
  sentence's figures equal the projection's values; not-found and failed queries.
- **Handler tests (Application)**: `auto` and `ai` modes, the turns appended, the caps.
- **Evaluation gate (Infrastructure.Tests, real model)**: a labelled set of about 150 realistic
  questions plus a **typo section**, each labelled with its expected intent, help article or
  `unsure`. The test **fails if any question is answered instantly with the wrong intent**, and
  reports the instant rate overall and for typos. Thresholds are chosen from this run.
- **Replay tests (Infrastructure.Tests)**: a conversation with quick answers, suggestions and an
  AI answer is sent to `FakeMessagesApi`; the request alternates roles correctly and carries the
  stored replay text byte for byte.
- **API integration tests**: the new SSE events; `mode=ai` with the AI off; the two rate limits;
  (PR 2) ratings owner-only, insights administrators-only.
- **Frontend tests**: card rendering, the link allow-list, chips, Ask the AI, (PR 2) ratings.
- **E2E**: a member asks "has my last order shipped?", gets an instant answer and follows the
  link; a vague question shows chips and *Ask the AI* gets the fake AI's answer; (PR 2) an
  administrator sees that miss on the insights page.

## PR split

1. **Instant answers**: the port and ONNX adapter, model delivery, catalog, spelling, router,
   resolvers, `FindProducts` and its migration, composer, the two turn types and their replay,
   `mode`, the SSE events, limits, configuration, the frontend card, chips and Ask the AI, the
   evaluation gate, and ADR 0036 *Instant answers before the AI* (amends 0033: the router answers
   first, the AI only on request).
2. **Feedback and insights**: ratings, the outcome log, the insights page and its navigation
   entry, and ADR 0037 *Administrators read missed assistant questions* (amends 0034: the new,
   scrubbed, transcript-bound access to members' question text).

## Growth

- **More intents**: in memory stays fine into the thousands of phrases (6,000 vectors is about
  9 MB and about 1 ms per search). Past a few thousand phrases, compute the vectors **at build
  time** and ship them, so pod start-up does not embed them.
- **Runtime-edited or very large content** (manuals, tens of thousands of sections): replace
  `PhraseIndex` with pgvector and an HNSW index, behind the same seam.
- **Ambiguity** is the real limit. In order: better phrases (from the misses list), two-step
  routing (area, then intent), then a trained classifier once the outcome log holds enough
  labelled questions. Each step is measured by the evaluation set.

## Out of scope

Languages other than English; actions (the assistant stays read-only); in-app editing of the
catalog; a vector database; training or fine-tuning a model; removing the AI path.
