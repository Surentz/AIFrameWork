---
type: llm
weight: 1
---

The response must identify the actual cause: there is no `HttpContext` in the worker, so nothing
can infer the caller, and the job is running with no current user. `GetOrders` resolves the
caller through `ICurrentUser` and returns nothing (or fails unauthorized) without one, because
ownership lives in the query itself — there is no "read everything" mode to fall back to.

FAIL if the response blames something else: a caching problem, a transaction or timing issue, a
missing `await`, the query itself being wrong, or the data genuinely being absent.
