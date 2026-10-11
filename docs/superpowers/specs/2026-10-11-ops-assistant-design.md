# Ops assistant — the first LLM feature — design

**Date:** 2026-10-11
**Status:** Approved 2026-10-11; amended while planning and building PR 1 (see "Amended while
planning" below — where it and a later section disagree, the amendment wins). Backend built (PR 1).
**Type:** this document is `docs(assistant)`; the build is two `feat` pull requests (§7).
**Builds on:** ADR 0014 (retry and resilience), ADR 0015 (logging), ADR 0016 (jobs in the worker),
ADR 0021/0027 (operational telemetry and metrics), ADR 0024 (capability policies), ADR 0031
(outbound integrations), all of which stay in force.

## Intent

Administrators can already *see* what is wrong on the Monitoring pages, but answering "why did
jobs fail last night?" means clicking through jobs, dead letters, traffic and integrations and
correlating them by hand. This design adds an **ops assistant**: an administrator asks a question
in plain language, and Claude answers by calling the existing monitoring queries as tools.

It is also the repository's **first LLM integration**, so it establishes the pattern later AI
features reuse — a provider port, a provider adapter, the API key as a file, a tool-use loop owned
by Application, token metrics, and a fake provider for tests — the same "plumbing plus a pilot"
shape the external-systems work used with Statistics Denmark.

**Success means:**
- an administrator gets a correct, sourced answer to an operational question in under a minute,
  with visible progress while it works;
- no username, user id, IP address or user agent is ever sent to the provider, enforced by a test;
- the assistant can never see more than the Monitoring page shows that administrator, and can
  never change anything;
- spend is bounded per question, per administrator, and per workspace;
- CI and e2e exercise the real adapter and never call Anthropic.

### Decided in conversation

| Question | Decision |
|---|---|
| Pilot | **Ops assistant** for administrators, over the monitoring queries. (Rejected for now: product description drafting, a user-facing order assistant, a background summary job.) |
| Provider | **Anthropic Claude**, via the official `Anthropic` NuGet SDK. |
| Account | **A company-owned Claude Console organization**, with one workspace per environment (`aiframework-dev`, `aiframework-prod`), each with its own keys and spend limit. Not a claude.ai/Claude Code subscription. |
| Data residency | Anthropic's own API. The bundled API reference lists only `us` and `global` for `inference_geo` — **no EU pin**. Accepted; re-check against live docs (§6, probe 5). If an EU rule appears, the route is Microsoft Foundry / Vertex / Bedrock, which changes only the adapter's client construction. |
| What may leave | **Operational data only.** No usernames, user ids, IPs or user agents; per-user tables are not tools. |
| Free-text exception messages | **Sent, scrubbed and truncated.** Residual risk — a name inside a message — is accepted and recorded in ADR 0033. |
| Conversation shape | **Multi-turn, server-held, short-lived** (24 h), browser holds only a conversation id. |
| Where the loop lives | **Application** (approach A). Infrastructure makes exactly one model call per turn. Rejected: the SDK's beta `BetaToolRunner` (loop untestable, caps outside our code); Microsoft.Extensions.AI `IChatClient` (normalises content, which breaks preserved thinking). |
| Transport | **Server-sent events from the API.** Not a worker job: the turn is interactive, and jobs cannot stream back. |
| Vector database | **None.** The data is structured and queryable; the documentation corpus (~120K tokens) fits in context if ever wanted. Revisit for semantic catalogue search. |
| Documentation tool (`read_doc` over ADRs) | **Out of scope** for the pilot. |

### Amended while planning

Recorded in `docs/superpowers/plans/2026-10-11-ops-assistant-backend.md` and ADR 0033.

1. **One question commits once.** Command handlers never save (`src/Application/CLAUDE.md`), so
   the handler appends the whole question — question, model turns, tool results — once, and the
   unit of work commits it. A failed, refused or abandoned question stores nothing. This replaces
   "persist each tool round-trip as it happens" in §1.
2. **Two tables.** `assistant_conversations` (owner, counts, last activity, an `xmin` row version
   that turns a concurrent question into a 409) and `assistant_turns` (surrogate key, a
   non-unique `(ConversationId, Seq)` index, a **`text`** payload). Probe 3 answered: `text`, not
   `jsonb`, which reorders keys.
3. **Probe 1 answered: the SDK takes an injected `HttpClient`.** It carries
   `OutboundTrafficHandler`; retry stays the SDK's own (`Assistant__MaxRetries`, default 2). Not an
   `ExternalSystemsBuilder` client: Refit-only, and its resilience handler would retry a streaming
   POST and cut long streams at its attempt timeout.
4. **Probe 6 answered:** `Fallbacks` is a typed property on the beta request, so the adapter uses
   the beta Messages API.
5. **Limits are constants**, not configuration (Application cannot read options):
   8 tool rounds, 20 questions per conversation, 2,000 characters, 24-hour retention.
   `Assistant__MaxTurns` is dropped.
6. **No `assistant.questions` metric:** every `AskAssistant` dispatch is already command traffic.
7. **Each SSE `data:` payload carries its `type`**, as well as the `event:` line.
8. **The port is `IAssistantOutput`** (CA1711 forbids a non-`Stream` type ending in "Stream"); the
   worker registers `NoAssistantOutput` because both hosts register the handler.
9. **Probe 4 answered:** the OpenAPI document carries the `text/event-stream` schema.
   **Probe 5 answered** (live docs, 2026-10-11): `inference_geo` is `"us"` or `"global"` only, and
   workspace data at rest is US-only — no EU option on the first-party API.

## 1. Backend structure

### Where things live

| Layer | Contents |
|---|---|
| `src/Application/Assistant` | `AskAssistant` command and handler (the loop); ports `IAssistantModel`, `IAssistantConversations`, `IAssistantOutput`; the tool catalogue (`AssistantToolbox`) and one projection record per tool; `OperationalText.Scrub`; `PruneAssistantConversations` job. |
| `src/Infrastructure/Assistant` | `ClaudeAssistantModel : IAssistantModel` (the SDK); `AssistantTranscripts : IAssistantConversations` (EF); `AssistantOptions`; the `assistant_turns` mapping and its migration; retention. |
| `src/Api/Assistant` | `AssistantController`; `SseAssistantOutput : IAssistantOutput` (registered per request); the rate-limit policy. |
| `src/Worker` | Registers and schedules `PruneAssistantConversations`. Nothing else. |

No Domain type: a transcript is operational data, not business state — the same reasoning that
keeps `sign_in_events` out of Domain.

### Ports

```csharp
// One model turn. Text deltas are pushed to the stream as they arrive; the returned turn holds
// the provider's content blocks verbatim (opaque to Application) plus the parsed tool calls.
public interface IAssistantModel
{
    Task<Result<AssistantModelTurn>> NextTurnAsync(
        IReadOnlyList<AssistantTurn> transcript,
        AssistantToolSet tools,          // AssistantToolSet.None for the final, tools-off call
        IAssistantOutput stream,
        CancellationToken cancellationToken);
}

public sealed record AssistantModelTurn(
    string Blocks,                                   // provider JSON, stored and replayed byte-for-byte
    IReadOnlyList<AssistantToolCall> ToolCalls,      // empty when the model answered
    AssistantStopReason StopReason,
    AssistantUsage Usage);

public interface IAssistantConversations
{
    Task<Result<IReadOnlyList<AssistantTurn>>> LoadAsync(Guid conversationId, Guid ownerId, CancellationToken ct);
    // Appends atomically. A tool-requesting model turn is only ever appended together with its
    // tool results (see "Persistence" below).
    Task<Result> AppendAsync(Guid conversationId, Guid ownerId, IReadOnlyList<AssistantTurn> turns, CancellationToken ct);
}

public interface IAssistantOutput
{
    Task TextAsync(string delta, CancellationToken ct);
    Task ToolAsync(string toolName, CancellationToken ct);
}
```

The exact member shapes may move during planning; the boundaries do not. Application never
references the `Anthropic` package.

### The loop (`AskAssistantHandler`)

`AskAssistant(Guid? ConversationId, string Question) : ICommand<AssistantAnswer>`, dispatched
through the normal command pipeline (validation, logging).

1. Validate: question 1–2,000 characters after trimming. A new conversation gets a fresh id; an
   existing one is loaded with the caller as owner — absent or someone else's is `NotFound`.
   A conversation with 20 questions already is `Validation` ("start a new conversation").
2. Append the user question to the in-memory transcript.
3. Call `IAssistantModel.NextTurnAsync` with the full tool set.
4. If the turn has tool calls: validate each input, dispatch each tool's query through
   `IQueryDispatcher`, project and scrub the result, and build one `tool_result` per call (a
   failed `Result` becomes `is_error: true` with the error's message). Persist the question (if
   not yet persisted), the model turn and the tool results **in one append**, report each tool
   name to the stream, then go to 3.
5. If the turn has no tool calls: persist it and return `AssistantAnswer(ConversationId, Usage)`.
6. **Turn cap.** After `Assistant:MaxTurns` (default 8) tool-using turns, make one final call with
   `AssistantToolSet.None` (`tool_choice: none`) so the model answers with what it has.

Tool calls in one turn run sequentially; they are cheap reads, and sequential keeps the
`DbContext` single-threaded.

### Persistence

Table `assistant_turns`: `conversation_id uuid`, `seq int`, `owner_id uuid`, `role text`
(`user` / `assistant`), `blocks jsonb`, `created_at timestamptz`. Primary key
`(conversation_id, seq)`; index on `created_at` for the prune.

- **Append-only, byte-for-byte.** Claude Opus 5.5 runs *preserved thinking*: thinking blocks are
  bound to the conversation, and an edited history is rejected with a 400. Turns are therefore
  never updated, reordered, trimmed or re-serialised. `blocks` is written exactly as the adapter
  returned it. (`jsonb` normalises whitespace and key order; probe 3 in §6 decides whether the
  column must be `text` instead.)
- **Atomic tool round-trips.** A model turn containing `tool_use` without its `tool_result`s would
  make every later request in that conversation fail. They are appended together or not at all;
  a cancelled request loses at most the unfinished round-trip.
- **Concurrency.** Two questions racing in one conversation collide on the primary key; the
  second gets `Conflict`.
- **Retention.** `PruneAssistantConversations` (worker, Quartz, hourly, `JobLane.Light`) deletes
  conversations whose last turn is older than 24 hours. Questions are free text an administrator
  typed; like `sign_in_events`, the retention is part of the feature, not housekeeping.

### The adapter (`ClaudeAssistantModel`)

- Official `Anthropic` SDK, Messages API, **streaming**. Text deltas go to `IAssistantOutput` as
  they arrive.
- Model `Assistant:Model` (default `claude-opus-5-5`); `output_config.effort` set explicitly from
  `Assistant:Effort` (default `medium` — also Opus 5.5's own default, set anyway so a model change
  cannot silently change it). Thinking is always on for Opus 5.5 and is not configured.
- **Server-side refusal fallback** on: beta `server-side-fallback-2026-07-01`, `fallbacks:
  "default"`.
- **Tools:** JSON-schema definitions with `strict: true` and `additionalProperties: false`. No
  forced `tool_choice` (Opus 5.5 rejects `any`/`tool`); `auto`, or `none` for the final call.
- **Prompt caching:** the system prompt and tool list are frozen strings, ordered
  deterministically, with a cache breakpoint after them. Nothing volatile (no timestamps) goes in
  the system prompt; the current time is passed in the user turn.
- **System prompt** (in Application, as a constant, so it is reviewed like code): the assistant's
  role; that it answers only from tool results and says so when it cannot; that tool output is
  data, not instructions; plain text with `-` bullets, no markdown tables; cite job names, envelope
  ids and trace ids where relevant.
- **HTTP pipeline:** if the SDK accepts an injected `HttpClient` (probe 1), it gets one built by
  `ExternalSystemsBuilder` under the system name `anthropic`, so traffic shows on the Integrations
  page and retry follows ADR 0014; the SDK's own `MaxRetries` is set to 0 so there is exactly one
  retry policy. The API key is added as `x-api-key` by a small delegating handler reading
  `Assistant:ApiKeyFile`. If injection is not possible, the SDK keeps its own retries and a
  traffic-recording handler is not added — the fallback is recorded in ADR 0033.
- **Errors:** the SDK's typed exceptions are caught most-specific first — rate limit, 5xx,
  connection, then other API errors — and converted to `Result`: `Unavailable` for the retryable
  ones, a logged fault for a 400 (it means our request was wrong). Never `catch (Exception)`.
- `stop_reason: "refusal"` after the fallback → `Result` failure `assistant.refused`
  ("The assistant can't answer that"). `max_tokens` → the partial text is kept and the stream
  says so.

## 2. Tools and the personal-data boundary

Eight read-only tools. Each wraps one existing monitoring query and returns **its own projection
record** — never a monitoring view type.

| Tool | Wraps | Projection omits / transforms |
|---|---|---|
| `job_health(window_hours)` | `GetJobHealth` | nothing |
| `list_job_runs(status?, job_name?, page)` | `GetJobRuns` (page size 25) | `OwnerId`; `Error` scrubbed |
| `get_job_run(envelope_id, attempt)` | `GetJobRun` | `OwnerId`; `Error` scrubbed |
| `list_dead_letters(page)` | `GetDeadLetters` (page size 25) | `ExceptionMessage` scrubbed |
| `traffic_summary(window_hours)` | `GetTrafficSummary` | nothing (names are route templates) |
| `traffic_series(window_hours)` | `GetTrafficSeries` | nothing |
| `external_systems()` | `GetExternalSystemStatus` | nothing |
| `sign_in_health(window_hours)` | `GetSignInHealth` | `LockedOutUsers` replaced by its count |

**Never tools:** `GetSignInEvents`, `GetUserActions`, `ListUsers`, and every command —
`RetryDeadLetter`, `TriggerJob`, anything that writes.

**Inputs:** `window_hours` 1–168; `page` ≥ 1; `status` is the `JobRunStatus` enum's names;
`envelope_id` a GUID. Validated in Application before dispatch even though `strict: true` already
constrains the shape.

**Authorization.** The endpoint requires `AuthorizationPolicies.Monitoring.Read`, and every
wrapped query is one the Monitoring page serves under that policy. Tools run through
`IQueryDispatcher` as the caller, so the assistant sees exactly what that administrator's
Monitoring page shows.

**Enforcement, not intention:**
1. **Architecture test:** reflects over every tool projection type (recursively) and fails on any
   property whose name matches `UserId`, `OwnerId`, `Username`, `IpAddress`, `UserAgent`
   (case-insensitive, as prefix or suffix). Adding a field to a monitoring view cannot leak
   through the assistant without failing CI.
2. **`OperationalText.Scrub`:** masks email addresses and IPv4/IPv6 addresses, then truncates to
   500 characters. Applied to every free-text field before it leaves a tool.
3. **Prompt injection:** tool output can carry partner- or attacker-influenced text (a dead
   letter's exception message). The system prompt marks tool output as data, and because no tool
   writes, the worst outcome is a misleading answer — which the UI's "check before acting" note
   addresses.

**Accepted risk (ADR 0033):** the scrubber matches patterns, not names. An exception message such
as `User 'bob' not found` passes through. Exception messages are the most useful input for "why
did it fail?", and the alternative (type only) was rejected.

## 3. API, failures, configuration, cost

### Endpoints

| Method and path | Policy | Returns |
|---|---|---|
| `GET /api/assistant` | `Monitoring.Read` | `{ enabled: bool }` — 200 even when disabled, so the UI can hide itself |
| `POST /api/assistant/messages` | `Monitoring.Read`, rate limit `assistant` | `text/event-stream` |

Request body: `{ conversationId?: uuid, question: string }` (DataAnnotations on the DTO).

SSE events, each `event: <name>` + one JSON `data:` line:

| Event | Payload |
|---|---|
| `text` | `{ delta }` |
| `tool` | `{ name }` — shown as "Checking job runs…" |
| `done` | `{ conversationId }` |
| `error` | `ProblemDetails` |

The payload types are declared with `[ProducesResponseType(..., "text/event-stream")]` so they
reach `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` (probe 4).

### Failures

- **Before the first byte** (validation, `NotFound`, disabled, 429): an ordinary HTTP
  `ProblemDetails` response through the existing exception and result mapping.
- **After the first byte:** an `error` event carrying `ProblemDetails`, then the stream closes.
- **Client disconnect:** `HttpContext.RequestAborted` cancels the handler; persistence rules in §1
  keep the transcript valid.
- **Disabled** (`Assistant:Enabled=false`): `POST` returns 404; `GET` returns `{ enabled: false }`.

### Configuration

| Key | Default | Notes |
|---|---|---|
| `Assistant__Enabled` | `false` | Backend CI and contract generation leave it off — no new placeholder switch needed. The e2e stack turns it on, pointed at the simulator (§5). |
| `Assistant__ApiKeyFile` | — | A path, never the key (ADR 0031). Required when enabled; `ValidateOnStart`. |
| `Assistant__BaseUrl` | SDK default | Points the SDK at the simulator in tests. |
| `Assistant__Model` | `claude-opus-5-5` | |
| `Assistant__Effort` | `medium` | |
| `Assistant__MaxRetries` | `2` | The SDK's own retry (429, 5xx, connection). Tool rounds (8) are a constant — amendment 5. |

Only the API reads these (the worker needs only the prune job, which uses the database). Dev:
the key file lives outside the repo, path in user-secrets or the environment. Kind/prod: a
mounted Secret, like the external systems' files.

### Cost and observability

- **Spend caps:** the Console workspace's spend limit (hard); the ASP.NET rate limiter, 20
  questions per administrator per hour (partitioned by user id); the turn cap per question; 20
  questions per conversation.
- **Metrics (OpenTelemetry, ADR 0027):** `assistant.tokens` (counter, tag `kind` = `input` /
  `output` / `cache_read` / `cache_write`), `assistant.tool_calls` (counter, tag `tool`). Question outcomes are command traffic
  already (amendment 6).
- **Logging:** `Behaviors.LoggedAsync` logs the command as it logs every command; the question
  text, tool results and answers are never logged.
- **Health:** no Anthropic check in `/health/ready` — a provider outage must not pull pods.

## 4. Frontend

- **Route** `/monitoring/assistant`, inside the existing `RequireRole allow="Admin"` block; an
  "Ask the assistant" tile on `MonitoringPage`. Both hidden when `getAssistantStatus()` reports
  disabled.
- **`src/api/assistant.ts`:** `getAssistantStatus()` (TanStack Query) and
  `askAssistant({ question, conversationId }, { signal, onEvent })` — `fetch` plus a stream reader,
  not `EventSource` (which cannot POST). Reuses `client.ts`'s defaults and its `ProblemDetails`
  handling for pre-stream failures.
- **`useAssistantConversation()`** — `useReducer`, not TanStack Query: messages, conversation id
  (memory only; a reload starts a new conversation), tool-progress chips, streaming status, and an
  `AbortController` behind a **Stop** button.
- **Rendering:** answers as plain text (`white-space: pre-wrap`). Model output is untrusted; no
  HTML surface and no new dependency. `react-markdown` is a later, self-contained change if
  answers need tables.
- **Errors** through `ErrorPanel` (trace links included); 429 shown as "You've asked a lot in the
  last hour — try again later."
- **Input:** disabled while streaming; shows the 2,000-character limit. **New conversation**
  button. A one-line note: "Answers are generated by Claude from monitoring data. Check before
  acting."

## 5. Tests

**No test calls Anthropic.** `tests/PartnerSimulator` gains a **fake Messages endpoint** that
streams scripted responses (a `tool_use`, then text), and the real adapter reaches it through
`Assistant:BaseUrl`. This exercises the SDK, the adapter and the SSE path; a scripted
`IAssistantModel` compiled into the API would test none of them.

| Project | Proves |
|---|---|
| `Application.Tests` | The loop with a fake `IAssistantModel`: tools dispatched and results appended; the cap ends with a tools-off call; a failed tool becomes `is_error`; another owner's conversation is `NotFound`; the 20-question limit; a tool round-trip is appended in one call; cancellation. Tool input bounds. `OperationalText.Scrub` fixtures (emails, IPv4, IPv6, truncation). **The projection architecture test.** |
| `Infrastructure.Tests` | `ClaudeAssistantModel` against the simulator: request shape (model, effort, `strict` tools, cache breakpoint, fallback beta); text deltas and `tool_use` parsed; 429/5xx/connection → `Unavailable`, 400 → fault; **blocks round-trip byte-for-byte**. `AssistantTranscripts` and the prune against Postgres, following the existing repository tests. |
| `Api.IntegrationTests` | 401 anonymous, 403 non-admin, 404 disabled; pre-stream `ProblemDetails`; SSE framing and event order; 429 from the rate limiter; `Conflict` on a concurrent question. |
| `Worker.IntegrationTests` | `PruneAssistantConversations` is registered and scheduled. |
| Vitest + MSW | **SSE parser with chunk boundaries splitting an event mid-line**; the reducer; streamed text, tool chips, Stop aborts, `error` → `ErrorPanel`; tile hidden when disabled. |
| Playwright | One spec: an administrator asks, sees "Checking job health…", then the answer — against the simulator; the e2e stack runs with the assistant enabled and `Assistant__BaseUrl` on the simulator. |

**Out of scope here:** an answer-quality eval. It needs real transcripts and real spend; it is the
first follow-up once the pilot runs against real data.

## 6. Probes to run before building on an assumption

Each is the first task of the PR that depends on it; a failed probe changes the plan, not the
code after the fact.

1. **Can the `Anthropic` SDK's client take an injected `HttpClient`?** Decides whether calls go
   through `ExternalSystemsBuilder` (traffic + ADR 0014 retry) or keep the SDK's own retry.
2. **Does `BaseUrl` redirect the SDK to the simulator**, streaming included?
3. **Can the adapter get each response's content blocks as raw JSON and send them back
   unchanged?** And does `jsonb`'s normalisation break preserved thinking? If either answer is
   bad, store `text` and/or re-serialise only through the SDK's own types.
4. **Can Swashbuckle describe the `text/event-stream` payloads** so the contract carries them? If
   not, a hand-written union in `frontend/src/features/assistant/types.ts`, pinned by a test.
5. **Live docs: `inference_geo` values** on the Claude API. If an EU value exists, set it.
6. **C# SDK support for `fallbacks: "default"`** with its beta header — typed property, or an
   additional body property.

## 7. Delivery

Two `feat` pull requests; this spec is its own `docs(assistant)` PR.

1. **`feat(assistant): answer operational questions with Claude over the monitoring queries`** —
   §1–3 and the backend half of §5: Application, Infrastructure (migration included), Api, the
   worker's prune job, the simulator's fake Messages endpoint, regenerated Wolverine adapters and
   API contract, **ADR 0033**, an `assistant` skill, and a CLAUDE.md "Rules that fail silently"
   entry (transcripts are append-only; tool projections never carry user fields). Ships with
   `Assistant__Enabled=false`.
2. **`feat(assistant): ask the ops assistant from the monitoring pages`** — §4, the frontend half
   of §5, and the Playwright spec.

Before the first real use: create the Console organization and the `aiframework-dev` workspace,
set its spend limit, and place the key file.

## Out of scope

- A vector database, embeddings, and a documentation (`read_doc`) tool.
- Any tool that writes (retry a dead letter, trigger a job).
- Per-user data in any form, including pseudonymised.
- Conversation history beyond 24 hours, or a list of past conversations.
- Markdown rendering.
- An answer-quality eval (first follow-up).
- EU data residency via Foundry / Vertex / Bedrock (the port allows it; nothing is built).
- Other AI features (product descriptions, a user-facing order assistant, semantic catalogue
  search).
