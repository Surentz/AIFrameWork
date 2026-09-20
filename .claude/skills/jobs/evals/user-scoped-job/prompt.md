---
max_turns: 12
allowed_tools: [Read, Glob, Grep, Skill]
---

I wrote a background job that rebuilds a per-user report. It dispatches `GetOrders` through
`IQueryDispatcher` to page the user's orders.

The job runs — I can see it complete in the worker log with no errors — but the report always
comes out empty, every time. The same query works fine from the controller. What's going on?
