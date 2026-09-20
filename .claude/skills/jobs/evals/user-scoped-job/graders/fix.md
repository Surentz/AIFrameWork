---
type: llm
weight: 1
---

The response must give the correct fix: the job type should implement `IUserScopedJob` and carry
the owner's id as a property on the message, so the worker populates `ICurrentUser` from it
before the handler runs.

Credit a response that names `JobUserMiddleware` as the thing that does the populating, but do
not require it.

FAIL if the proposed fix is to read the user some other way — passing an `ICurrentUser` into the
handler manually, registering `ICurrentUser` inside `AddJobs`, querying the repository directly
to bypass the check, or adding an "all users" mode to the query.
