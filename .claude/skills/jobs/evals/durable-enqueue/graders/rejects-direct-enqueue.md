---
type: llm
weight: 1
---

The proposed approach is wrong for the stated requirement, and the response must say so.

`IJobScheduler.EnqueueAsync` publishes immediately and is NOT transactional with the caller's
work. A command handler that enqueues and then fails its transaction has still run the job — the
order rolls back and the confirmation email goes out anyway.

PASS if the response identifies that calling `EnqueueAsync` from the command handler does not
give the "must not be lost" guarantee the user asked for.

FAIL if it endorses the user's plan, or only suggests cosmetic changes to it.
