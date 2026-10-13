# 0036. Instant answers before the AI

**Date:** 2026-10-13
**Status:** Accepted

## Context

Every assistant question went through Claude's tool-use loop (ADRs 0033, 0034): seconds per answer,
and every figure restated by a model. Most questions members ask are application-specific ("has my
last order shipped?", "is my export ready?") and need a fast, exact answer with a link to the screen
where the work happens, not generated prose. Claude should remain for the long tail, but not be the
default and not be a hidden cost behind a question the application could answer itself.

Constraints that were real: no labelled training data; a small catalogue (a few hundred phrases);
read-only (ADR 0033 stands); the assistant must never take a pod out of rotation; transcripts are
append-only because Claude rejects a history whose earlier turns changed.

Design: `docs/superpowers/specs/2026-10-12-assistant-instant-answers-design.md`.
Amends ADR 0033 and ADR 0034.

## Decision

- **A local, in-process embedding model recognises the question.** `bge-small-en-v1.5`, quantised
  (34 MB, MIT), pinned by Hugging Face commit and SHA-256, fetched by
  `scripts/fetch-assistant-model.ps1` and copied into the API image. Nothing is downloaded at runtime.
  It runs through ONNX Runtime behind an Application port (`ITextEmbedder`), in the **API host only**.
- **The index is in memory; there is no vector database.** About 250 phrase vectors (under 400 KB)
  are built while the API starts, so a pod either answers instantly or knows it cannot. Nearest
  neighbour is a plain loop. The growth path: revisit when the catalogue passes a few thousand
  phrases (an approximate index in process first, a vector store only after that).
- **A threshold, a margin and an evaluation gate decide.** The best phrase must score at least 0.80
  and lead the runner-up intent by 0.04; between 0.55 and the threshold the reply is suggestions
  ("Did you mean..."), below it nothing is guessed. A labelled evaluation set runs in CI with floors
  on the share answered instantly and on typos, and requires that every unsure question stays
  unanswered. A wrong instant answer fails the build.
- **Instant answers are read-only and caller-scoped.** The composer dispatches the same read-only
  queries the AI tools use, scoped to `ICurrentUser`, and builds the answer from the query's own
  values. An operator intent never appears for a member. No command is dispatched.
- **Claude runs only on request.** Quick questions use their own endpoint,
  `POST /api/assistant/quick` (120 an hour per user); `POST /api/assistant/messages` stays the AI
  endpoint (20 an hour). The quick path never calls Claude, so its budget cannot spend the AI's. A
  question that is not recognised gets suggestions and an *Ask the AI* button.
- **Quick replies are two new append-only turn types**, `QuickAnswerTurn` and `SuggestionsTurn`,
  each storing the answer as rendered plus a replay text fixed at write time. The request mapper
  sends that text to Claude verbatim on a later *Ask the AI*; a stored turn is never re-rendered.
- **English only.** Content lives under `en/` so Danish can be added; no language detection.
- **Spelling** is an in-house edit-distance corrector over the catalogue's own vocabulary, used only
  when the corrected question produces an instant match under the same threshold and margin.
  Product names are matched with `pg_trgm` `word_similarity`; no trigram index yet.

## Consequences

Easier: a recognised question answers in about two milliseconds of model time plus its query, every
figure comes from a query, and a wrong answer is caught by CI rather than by a user. Claude's spend
now follows explicit clicks.

Costs:

- A 34 MB model in the API image and about 60 MB resident per pod.
- Catalogue curation is manual: an intent needs at least 10 phrases, a composer case and evaluation
  questions; a help article is a Markdown file.
- A question outside the catalogue needs a click to reach the AI.
- The replay text of a quick answer reaches Anthropic if the user later asks the AI in the same
  conversation. It is the asker's own data, which ADR 0034 allows.
- The evaluation set holds 88 questions, not the roughly 150 the spec asked for. This is a known
  gap; the floors are measured on a smaller sample (instant 69/73 = 0.945 against a floor of 0.89,
  typos 10/10 against 0.95, 15/15 unsure questions unanswered) and should be re-measured as the set grows.
- ONNX Runtime's native library in the chiseled runtime image is exercised by no unit test; it is
  checked by hand on the kind cluster.
- Product search scans without a trigram index. Revisit past roughly 10,000 products.
- Cancelling an order is not a help article: members have no cancel button, so the article is
  "signing out everywhere" instead.

Rules out: guessing an answer below the threshold, an automatic fall-through to Claude, and any
write action from the assistant.

## Alternatives considered

- **No LLM at all.** Cannot answer the long tail; rejected, Claude stays on request.
- **Claude for every question.** Seconds per answer, figures restated by a model, and spend with no
  user decision behind it.
- **Keyword rules.** Brittle against rephrasing and typos, and every miss is a code change.
- **A trained classifier.** There is no labelled data yet, and retraining becomes part of every
  catalogue change.
- **A vector database.** About 250 vectors fit in 400 KB; a database adds an operational dependency
  for no gain at this size.
- **A separate embedding service.** A network hop and a new thing to deploy and keep healthy, for a
  model small enough to live in the API process.
