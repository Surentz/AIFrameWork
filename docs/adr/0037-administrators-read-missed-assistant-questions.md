# 0037. Administrators read missed assistant questions

**Date:** 2026-10-16
**Status:** Accepted

## Context

Instant answers (ADR 0036) are tuned on a hand-written evaluation set of 88 questions. Improving the
catalogue needs the questions real users asked that the assistant missed, and a signal for answers
that were confidently wrong. Those questions are members' own words, which makes them personal data
under ADR 0034: until now nobody but their author could read them.

Constraints that were real: transcripts are append-only and expire with their conversation (30 days
after the last question); a copy of the question text anywhere else would outlive that; an
administrator cannot open another user's conversation.

Design: `docs/superpowers/specs/2026-10-12-assistant-instant-answers-design.md`.
Amends ADR 0034.

## Decision

- **Members rate answers.** `PUT /api/assistant/conversations/{id}/turns/{turn}/rating` stores
  👍 or 👎, or clears it, in `assistant_ratings`. Owner only: another user's conversation is a 404,
  the same as a missing one. A turn that is not an answer is a 400 `assistant.not_an_answer`. A
  rating is never written into the transcript.
- **An answer is identified by its turn number.** A quick reply or suggestions reply is rated on its
  own turn, an AI answer on its final model turn. The number is on the SSE `answer`, `suggestions`
  and `done` events and on each saved message (`turn`, `rating`).
- **Every answered and saved question leaves one outcome row** in `assistant_outcomes`: path, target
  (intent or article), best score, margin, whether the spelling correction won, and scope. It holds
  **no question text and no user id**. A question that stored nothing (a 503
  `assistant.quick_unavailable`) logs nothing.
- **Administrators see what was missed.** `GET /api/monitoring/assistant/insights?days=1..30`
  (`Monitoring.Read`) returns daily rates, the most-asked targets and up to 50 missed questions: those
  that ended in suggestions or the AI, or were rated 👎. The list is ordered rated 👎 first, then newest,
  so volume of AI and suggestion misses never pushes a 👎 out of the 50.
- **The question text is read live from the transcript and scrubbed** with `OperationalText.Scrub`,
  never copied. The list names no user and no conversation.
- **Ratings and outcomes cascade with the conversation**, so the owner's delete and the 30-day
  retention remove them, and the question disappears from the misses list with them.

## Consequences

Easier: the catalogue can be improved from what users actually asked, and a wrong instant answer has
a signal other than a complaint.

Costs:

- A new, narrow access path to members' words: scrubbed, read-only, administrators only.
- The insights page covers at most 30 days, the life of the transcript.
- A missed question cannot link to its conversation, because administrators cannot open another
  user's conversation. Scrubbing removes email and IP addresses but cannot remove every personal detail
  a member types into free text.
- The turn number becomes part of the API (events and saved messages).
- Choosing "Ask the AI" after suggestions re-asks the question, so one question logs a Suggested and an Ai
  outcome: it appears twice in the misses and in the daily counts.
- A quick reply that reports a source "couldn't be loaded" is still an Instant answer, so an outage can
  raise the answered-at-once rate.
- A miss carries its exact time. An administrator who can also read request logs or the sign-in audit
  could match it to a user, so "names no user" means the page itself names none.

Rules out: storing question text in the outcome or rating tables, and tying a miss to a user.

## Alternatives considered

- **Store the question text in the outcome.** It would outlive the transcript and the owner's
  delete; rejected.
- **Hash the questions.** Unreadable, so useless for improving the catalogue.
- **Show misses to their author only.** No one could improve the catalogue from them.
- **No ratings.** Precision misses (a confident wrong answer) would leave no signal at all.
