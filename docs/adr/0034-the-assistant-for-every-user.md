# 0034. The assistant for every user

**Date:** 2026-10-11
**Status:** Accepted; amended by 0036 and 0037

## Context

ADR 0033 made the assistant an administrator's tool over operational data only: it sat behind the
`Monitoring.Read` policy and no user's personal data was allowed to leave for Anthropic. The
assistant should be everyone's: any signed-in user asks about their own orders and exports and about
the shared catalogue and population figures, and administrators keep the monitoring tools.

That changes who can ask, which tools a question may use, and what data leaves. Application cannot
see roles (`ICurrentUser` exposes only `Id`; capabilities are checked at the API, ADR 0024).

Design: `docs/superpowers/specs/2026-10-11-assistant-for-everyone-design.md`.

## Decision

- **Every signed-in user may ask.** `AssistantController` is `[Authorize]`. The 20-questions-per-user-per-hour
  limit and the workspace spend cap stay the bounds.
- **Tools are chosen per question by `ToolScope`** (`Member` or `Operator`). The API decides it
  from `Monitoring.Read`, whose role is read from the database on every request (ADR 0022), so a
  demoted administrator is a `Member` on their next question. `AskAssistant` carries the scope;
  the handler offers `AssistantToolbox.For(scope)` and the matching prompt, and a tool outside the
  scope is answered with `is_error` and never run or shown.
- **A member's tools** (`my_orders`, `get_order`, `my_exports`, `list_products`, `population`)
  dispatch existing queries that are already scoped to `ICurrentUser`, plus the shared catalogue
  and population figures. Another user's order is the same `NotFound` as an unknown id.
- **The asker's own order and export data may leave for Anthropic, for the asker's own question.
  Nothing about any other user does**, and identifiers still never do: member projections carry no
  `UserId`, `OwnerId`, `Username`, `IpAddress` or `UserAgent` (`AssistantToolResultsBoundaryTests`).
  This supersedes ADR 0033's "operational data only"; the rest of 0033 stands.
- **Transcripts are every asker's personal data.** A conversation is titled by its first question
  (never by the model, 60 characters), kept 30 days after the last question, and deletable by its
  owner. Rename changes only the conversation row; no stored turn is ever rewritten (ADR 0033).
  The history endpoints under `/api/assistant/conversations` answer another user's id with 404.
- **Answers are Markdown.** The prompts ask for it; the page renders it with raw HTML disabled.

## Consequences

- Wider spend: every user can now cause token cost. Bounded by the per-user hourly limit, 8 tool
  rounds per question, 20 questions per conversation, and the workspace limit.
- The endpoint's audience changes, and its 403 is gone: a breaking API change.
- Question text and transcripts of every asker are held 30 days instead of 24 hours.
- **Unverified:** whether the live Messages API accepts a history that names a tool no longer
  offered (an administrator demoted mid-conversation). The request stays well-formed — every
  stored `tool_use` keeps its `tool_result` — but this has not been run against Anthropic.
- A tool added later is a member tool only if it reads the caller's own rows through a query scoped
  to `ICurrentUser`; anything else is a monitoring tool.

## Alternatives considered

- **Two separate assistants.** Duplicates the loop, the stream and the history.
- **Frontend-only gating.** The API would still serve the monitoring tools to anyone who asked.
- **Model-written titles.** One more model call per conversation, and model text in a place the
  user did not ask for it.
