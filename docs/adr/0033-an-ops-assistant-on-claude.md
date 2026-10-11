# 0033. An ops assistant on Claude

**Date:** 2026-10-11
**Status:** Accepted

## Context

Administrators can see what is wrong on the Monitoring pages, but answering "why did jobs fail
last night?" means clicking through jobs, dead letters, traffic and integrations and correlating
them by hand. This is also the repository's first use of a large language model, so whatever it
does becomes the pattern later AI features copy: where the provider sits, what data may leave,
how a conversation is stored, and how spend is bounded.

The monitoring data is not all operational. `sign_in_events` and the admin audit hold usernames
and IP addresses; `JobRunView` carries an owner id; dead letters and job errors carry free-text
exception messages.

Design: `docs/superpowers/specs/2026-10-11-ops-assistant-design.md`.

## Decision

**Claude, through a company-owned Claude Console organization.** One workspace per environment
(`aiframework-dev`, `aiframework-prod`), each with its own keys and spend limit. The key is a file
whose path is `Assistant__ApiKeyFile` — never a value in configuration (ADR 0031). Default model
`claude-opus-5-5`, effort `medium` set explicitly, adaptive thinking, and the server-side refusal
fallback (`fallbacks: "default"`).

**Processing is US or global, by necessity.** Checked against the live data-residency
documentation on 2026-10-11: `inference_geo` accepts only `"us"` and `"global"`, and a workspace's
data at rest is US-only. There is no EU option on Anthropic's own API. If an EU rule applies later,
the route is Claude on Microsoft Foundry, Vertex AI or Bedrock in an EU region; only the adapter's
client construction changes, because the provider sits behind `IAssistantModel`.

**Operational data only.** Eight read-only tools, each one monitoring query plus its own
projection record in `Application/Assistant/ToolResults`. Per-user tables are not tools, and no
tool dispatches a command. `AssistantToolResultsBoundaryTests` fails on any projection property
named like a user identifier. Free text (job errors, exception messages, external-system
descriptions) goes through `OperationalText.Scrub`, which masks email and IP addresses and
truncates to 500 characters. Tools run through `IQueryDispatcher` as the caller, under the
`Monitoring.Read` policy, so the assistant sees exactly what that administrator's Monitoring page
shows.

**Accepted risk: names inside free text.** The scrubber matches patterns, not names, so an
exception message such as `User 'bob' not found` reaches the provider. Exception messages are the
most useful input for "why did it fail?"; sending only the exception type was rejected.

**The loop lives in Application, behind a one-turn port.** `AskAssistantHandler` calls
`IAssistantModel.NextTurnAsync`, runs the tools, and goes round again, at most 8 tool rounds and
then one tools-off call so the admin still gets an answer. Rejected: the SDK's beta
`BetaToolRunner` (the loop would be untestable without the SDK and the caps outside our code) and
Microsoft.Extensions.AI's `IChatClient` (it normalises content, which breaks the next decision).

**Transcripts are append-only and replayed byte-for-byte.** Claude Opus 5.5 rejects a history
whose earlier turns changed (preserved thinking). A model turn is stored as our own JSON of its
blocks — thinking text and signatures, tool calls, and any block type we do not recognise kept
raw — in a `text` column (`jsonb` reorders keys), and is never updated, trimmed or re-serialized.
A provider 400 surfaces as `assistant.rejected` ("start a new conversation"), not a 500.

**One question is one transaction.** The handler collects the question, every model turn and
every tool result, and appends them once; the unit of work commits with the command. A failed,
refused or abandoned question stores nothing, so the conversation stays valid at the end of the
previous answer. A concurrent question in the same conversation loses on the conversation row's
`xmin` and gets a 409. Conversations idle for 24 hours are deleted hourly by the worker
(`PruneAssistantConversations`): the questions are free text an administrator typed.

**Not an `ExternalSystemsBuilder` client.** The official SDK takes an injected `HttpClient`, and
the adapter gives it one carrying `OutboundTrafficHandler`, so calls appear on the Integrations
page and in the external-system call metric (and its 20% alert). Retry is the SDK's own, which
honours Anthropic's `retry-after` and should-retry signals. The builder is Refit-only, and its
standard resilience handler would retry a non-idempotent streaming POST and cut a long stream at
its attempt timeout. This is the one exception to "every partner client goes through
`AddClient<TApi>`".

**Server-sent events from the API.** `POST /api/assistant/messages` streams `text`, `tool`,
`done` and `error` events. The response starts lazily, so a failure before the first event is an
ordinary ProblemDetails response; `X-Accel-Buffering: no` stops ingress-nginx buffering the
stream; `done` is written only after the command returned, so `done` means saved. An SSE comment
(`: keep-alive`) every 15 s (`Assistant__HeartbeatSeconds`) keeps the stream alive while the model
thinks, under ingress-nginx's 60 s read timeout; the first one starts the stream. The OpenAPI
document carries the `text/event-stream` schema (`AssistantEventResponse`). Not a worker job: the
turn is interactive, and jobs cannot stream back (ADR 0016).

**Spend is capped three ways:** the workspace spend limit, 20 questions per administrator per
hour (`RateLimiting__Assistant__PermitLimit`, partitioned by user), 8 tool rounds per question
and 20 questions per conversation. `aiframework.assistant.tokens` and
`aiframework.assistant.tool_calls` meter it; question outcomes are already command traffic.

**No vector database.** The data is structured and queryable by time window; a tool call gives an
exact answer where retrieval would give a fuzzy, stale copy. The documentation corpus (about 120K
tokens) fits in context if it is ever wanted.

## Consequences

- The feature ships switched off (`Assistant__Enabled=false`); CI, contract generation and a fresh
  checkout need no key. CI and e2e never call Anthropic: `tests/PartnerSimulator`'s
  `FakeMessagesApi` streams scripted answers to the real adapter.
- Question text, tool results and answers leave for Anthropic (US/global), retained under the
  organization's Anthropic data-retention terms. Usernames, user ids, IPs and user agents do not,
  except as the accepted free-text risk above.
- A tool added later must return a projection from `ToolResults/`; the boundary test reads every
  type in that namespace.
- Editing a stored turn — a migration that rewrites `assistant_turns`, a "cleanup" that trims old
  turns — breaks every later question in that conversation. The `assistant` skill and the root
  CLAUDE.md say so.
- Both hosts register `AskAssistantHandler`, so both need an `IAssistantOutput`: the API's writes
  SSE, the worker registers `NoAssistantOutput`.

## Alternatives considered

- **Azure OpenAI / Foundry for EU residency.** Not chosen: no EU rule applies today, and the
  first-party API gets new features first. It remains the route if one does.
- **A local model (Ollama).** Nothing would leave the cluster, but tool use is weaker and dev and
  kind would need a GPU-capable container.
- **Single question, single answer.** Simpler, but follow-ups ("and yesterday?") would need the
  context retyped.
- **Pseudonymised personal data in tools.** More useful for "who is being brute-forced", more to
  get right, and a larger GDPR surface.
