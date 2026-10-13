---
name: assistant
description: Use when touching the ops assistant, its conversation history and member tools - AskAssistant's tool-use loop, the assistant tools and their personal-data boundary, the Claude adapter, transcripts and preserved thinking, Assistant__* configuration, the SSE endpoint, FakeMessagesApi, or the Monitoring panel (frontend/src/features/assistant).
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
| `src/Application/Assistant/AssistantPrompt.cs` | The system prompts, one constant per scope |
| `src/Application/Assistant/AssistantHistory.cs` | The history port, display DTOs, and the list/read/rename/delete queries and commands |
| `src/Infrastructure/Assistant/` | The Claude adapter (`ClaudeAssistantModel`, the request mapper, the stream accumulator, `StoredBlocks`), the transcript store, retention, options, metrics |
| `src/Infrastructure/Assistant/AssistantHistoryStore.cs`, `AssistantDisplay.cs` | The sidebar reads, rename and delete; the display projection of stored turns |
| `src/Api/Assistant/` | `AssistantController`, `AssistantConversationsController`, `SseAssistantOutput`, DTOs, the rate-limit policy name |
| `tests/PartnerSimulator/FakeMessagesApi.cs` | A scripted stand-in for the Messages API |

## Adding a tool

0. Decide its scope: a member tool goes in `MemberDefinitions` and may read only the caller's own
   rows through a query scoped to `ICurrentUser`; anything else goes in `MonitoringDefinitions`.
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

## The page

`/assistant` (a new chat) and `/assistant/:id` (a saved one), opened for every user from the header
icon (`AssistantLink`), in `frontend/src/features/assistant`: `ConversationSidebar` beside a `Chat`.

- **The conversation is a reducer** (`conversation.ts`, driven by `useAssistantConversation`), not
  TanStack Query: a stream is not a cacheable query. `fromSaved` seeds it from a saved conversation.
- **`useChatSession` is why a new chat survives getting its id** (the URL becomes `/assistant/<id>`
  without remounting the chat or cutting the stream) and why every other navigation — the sidebar,
  New chat, the header icon — is a fresh chat. It waits for the address to arrive at the adopted id
  before it treats leaving it as leaving the chat.
- **`useSavedConversation` reads once** (`gcTime: 0`, no background refetch): after that the chat owns
  the messages, and a refetch must never replace a chat that is streaming.
- **The composer's draft lives in the page**, so it survives New chat; a failed question is put
  back in it. Send stays in place, disabled while streaming, with Stop beside it (a button that
  changed into Stop under the pointer let a double-click stop what it just sent).
- **`SseParser` must keep partial chunks.** A network chunk can end mid-line, mid-JSON or between
  a CR and its LF; only a blank line ends an event. `: keep-alive` comments are dropped.
- **Stop, New chat and leaving the page abort the request**, and the server then saves nothing for
  that question — so after Stop the next question still sends the conversation id and continues
  from the last saved answer.
- `askAssistant` goes through `client.ts`'s `requestStream`, so a failure before the stream
  starts (400, 404, 409, 429, 503) is an ordinary `ApiError`; after that it is an `error` event.
- **Answers go through `AssistantMarkdown` only** (`react-markdown` + `remark-gfm`): model output is
  untrusted, so there is no `rehype-raw`, ever, and links open with `rel="noopener noreferrer"`.
- **e2e** runs `tests/PartnerSimulator -- messages <port>` as a webServer: `FakeMessagesApi` with
  `DefaultReply = FakeMessagesScript.ToolThenAnswer`, which picks `job_health` or `my_orders` from the
  tools the request offers, then a fixed answer — a Playwright run cannot script it per test.
  `assistant.spec.ts` is `@local-only`.

## Scope and history

`ToolScope` is decided by the API for every question from `Monitoring.Read` (the role is read from
the database, so a demoted administrator is a member on their next question). The handler offers
`AssistantToolbox.For(scope)` and `AssistantPrompt.For(scope)`; a tool outside the scope is an
`is_error` result, never run. A conversation is titled by its first question (60 characters), kept
30 days after its last question, and its owner can list, reopen, rename and delete it; another
user's id is a 404. Rename touches only the conversation row, and the display projection reads
stored turns without ever rewriting them.

## Spend

The Console workspace's spend limit is the hard cap. In the app: 20 AI questions per **user**
per hour (`RateLimiting__Assistant__PermitLimit`), 8 tool rounds per question, and 50 questions
per conversation in total, of which at most 20 go to the AI (`AssistantLimits`; instant answers
have their own 120 an hour, `RateLimiting__AssistantQuick__PermitLimit`). Tokens and tool calls are metered as `aiframework.assistant.tokens{kind}` and
`aiframework.assistant.tool_calls{tool}`.

## Instant answers (ADR 0036)

Most member questions are answered without Claude. The slice lives in
`src/Application/Assistant/Quick` (router, index, composer, replay text) and
`src/Infrastructure/Assistant/Quick` (ONNX embedder, catalog loader, registration).
`POST /api/assistant/quick` serves it (120 an hour); the AI stays on `/messages` (20 an hour).

- **Adding an intent.** A `QuickIntents` entry, at least 10 phrases in `Catalog/en/phrases.json`
  including its canonical question, a case in `QuickAnswerComposer`, and evaluation questions in
  `tests/Infrastructure.Tests/Assistant/Quick/evaluation.en.json`. An in-scope evaluation question
  is never the same wording as a phrase, or the gate tests the catalog against itself. The only
  deliberate repeats are operator phrases asked as a member, labelled `unsure`: they must not answer.
- **Adding a help article.** A Markdown file under `Catalog/en/help/`: front matter (title, link),
  then `##` sections, plain text, using the real button labels. Each section is indexed separately
  and a help answer quotes the matched section verbatim.
- **The model.** `scripts/fetch-assistant-model.ps1` downloads and hash-checks bge-small-en-v1.5 into
  `models/assistant` (git-ignored); the build copies it as `assistant-model/`. A test that needs it
  fails when it is missing.
- **Tuning.** `QuickRouting` (Threshold 0.80, Margin 0.04, SuggestionFloor 0.55) is overridable under
  `Assistant:Quick`. `QuickEvaluationTests` holds the floors; re-measure before changing either.
- **Only the API host builds the index** (`AddQuickAnswerIndexing`, called from `Program.cs`). The
  worker must never load the model.
- **Transcripts.** Quick replies are `QuickAnswerTurn` and `SuggestionsTurn`; their replay text is
  fixed at write time and sent to Claude verbatim. Never re-render one from a template.
