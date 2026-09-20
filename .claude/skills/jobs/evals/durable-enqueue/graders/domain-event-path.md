---
type: llm
weight: 1
---

The response must direct the user to the correct mechanism: raise a **domain event** from the
aggregate and enqueue the job from that event's handler.

It should convey why this works — the domain-events interceptor writes the outbox row in the
same `SaveChangesAsync` as the aggregate, so the job exists if and only if the command
committed.

Credit a response that names `OrderPlacedConfirmationHandler` as the existing reference, but do
not require it.

FAIL if the response proposes a different fix instead: a try/catch around the enqueue, a manual
retry, a transaction the handler opens itself, or Wolverine's own EF Core outbox
(`IDbContextOutbox`) — that last one was measured and rejected in this repo, because publishing
through it enrolls the DbContext and the plain `SaveChangesAsync` then cannot commit.
