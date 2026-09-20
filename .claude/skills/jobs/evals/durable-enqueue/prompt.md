---
max_turns: 12
allowed_tools: [Read, Glob, Grep, Skill]
---

When an order is placed I need to send a confirmation email in the background. It absolutely
must not be lost — if we take the order, the customer gets the email.

I was going to inject `IJobScheduler` into `PlaceOrderHandler` and call `EnqueueAsync` right
after the order is added. Is that right?
