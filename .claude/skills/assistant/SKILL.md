---
name: assistant
description: Use when touching the ops assistant - AskAssistant's tool-use loop, the assistant tools and their personal-data boundary, the Claude adapter, transcripts and preserved thinking, Assistant__* configuration, the SSE endpoint, FakeMessagesApi, or the Monitoring panel (frontend/src/features/assistant).
---

# The ops assistant

An administrator asks a question; Claude answers by calling monitoring queries as read-only
tools, streamed back as server-sent events. ADR 0033 has the reasons; this is the how.

## Where things live

| Path | What |
|---|---|
| `src/Application/Assistant/AskAssistant.cs` | The command, its validator, and the loop (`AskAssistantHandler`) |
| `src/Application/Assistant/AssistantToolbox.cs` | The tool definitions and their execution |
| `src/Application/Assistant/ToolResults/` | What each tool returns — and therefore what leaves for Anthropic |
| `src/Application/Assistant/AssistantPorts.cs` | `IAssistantModel`, `IAssistantOutput`, `IAssistantConversations`, `IAssistantConversationRetention` |
| `src/Application/Assistant/AssistantPrompt.cs` | The system prompt, a constant |
| `src/Infrastructure/Assistant/` | The Claude adapter (`ClaudeAssistantModel`, the request mapper, the stream accumulator, `StoredBlocks`), the transcript store, retention, options, metrics |
| `src/Api/Assistant/` | `AssistantController`, `SseAssistantOutput`, DTOs, the rate-limit policy name |
| `tests/PartnerSimulator/FakeMessagesApi.cs` | A scripted stand-in for the Messages API |

## Adding a tool

1. A projection record in `ToolResults/` — never return a monitoring view.
   `AssistantToolResultsBoundaryTests` reads every type in that namespace and fails on any property
   named like `UserId`, `OwnerId`, `Username`, `IpAddress` or `UserAgent`.
2. Free text goes through `OperationalText.Scrub`.
3. A definition in `AssistantToolbox.Definitions`: every property listed in `required` (use
   `["string","null"]` for an optional filter) and `additionalProperties: false` — strict tool use
   needs both, and a test checks them. Bounds go in the description and are enforced in the
   toolbox, because strict schemas do not carry them.
4. A case in `RunAsync` that validates the input, dispatches a **query** through
   `IQueryDispatcher`, and projects. Never a command: the assistant only reads.
5. Toolbox tests for the projection and for bad input.

## Transcripts are append-only

Claude rejects a history whose earlier turns changed (preserved thinking). So:

- Never update, trim, reorder or re-serialize a stored turn. `ModelTurn.Blocks` is opaque to
  Application; only `StoredBlocks` reads it, and only to replay it.
- A block type the accumulator does not know is stored raw and replayed raw. Do not "clean up"
  unknown blocks.
- `Payload` is `text`, not `jsonb`, on purpose.
- One question is one transaction: the handler appends everything once, and the unit of work
  commits it. A failed question stores nothing. Do not add a mid-loop save.
- `assistant.rejected` (a 503) means the provider refused our request — a bug, or a conversation it
  no longer accepts. The admin is told to start a new conversation.

## Switching it on locally

Create a key in the `aiframework-dev` workspace, save it to a file **outside the repo**, then set
`Assistant__Enabled=true` and `Assistant__ApiKeyFile=<path>` (user-secrets or the environment).
With it off, `GET /api/assistant` says so and `POST /api/assistant/messages` is a 404.

The stream sends a `: keep-alive` comment every `Assistant__HeartbeatSeconds` (default 15) while
nothing else is written, so a proxy's read timeout never closes it mid-thought. Something that
throws after the stream started is turned into a final `error` event by
`AssistantStreamExceptionFilter`: UseExceptionHandler does not run once a response has started.

## Testing

No test calls Anthropic. `FakeMessagesApi.StartAsync()` listens on plain HTTP on loopback; point
`Assistant__BaseUrl` at it, script replies with `Enqueue(FakeReply.Text(...))`,
`FakeReply.ToolUse(...)`, `FakeReply.Status(429)` and friends, and read what the adapter sent from
`Requests`. Set `Assistant__MaxRetries=0` so error tests don't wait out the SDK's backoff.
`Api.IntegrationTests` references the simulator with `Aliases="simulator"` — its top-level
`Program` would clash with the API's — so a test there starts with `extern alias simulator;`.

## The panel

**Monitoring → Ask the assistant** (`/monitoring/assistant`), in `frontend/src/features/assistant`.

- **The conversation is a reducer** (`conversation.ts`, driven by `useAssistantConversation`), not
  TanStack Query: a stream is not a cacheable query. The conversation id lives in memory only, so
  a reload starts a new conversation.
- **`SseParser` must keep partial chunks.** A network chunk can end mid-line, mid-JSON or between
  a CR and its LF; only a blank line ends an event. `: keep-alive` comments are dropped.
- **Stop, New conversation and leaving the page abort the request**, and the server then saves
  nothing for that question — so after Stop the next question still sends the conversation id and
  continues from the last saved answer.
- `askAssistant` goes through `client.ts`'s `requestStream`, so a failure before the stream
  starts (400, 404, 409, 429, 503) is an ordinary `ApiError`; after that it is an `error` event.
- Answers render as plain text (`white-space: pre-wrap`): model output is untrusted.
- **e2e** runs `tests/PartnerSimulator -- messages <port>` as a webServer: `FakeMessagesApi` with
  `DefaultReply = FakeMessagesScript.ToolThenAnswer` (a `job_health` call, then a fixed answer),
  because a Playwright run cannot script it per test. `assistant.spec.ts` is `@local-only`.

## Spend

The Console workspace's spend limit is the hard cap. In the app: 20 questions per administrator
per hour (`RateLimiting__Assistant__PermitLimit`), 8 tool rounds per question, 20 questions per
conversation. Tokens and tool calls are metered as `aiframework.assistant.tokens{kind}` and
`aiframework.assistant.tool_calls{tool}`.
