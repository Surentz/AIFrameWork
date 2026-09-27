---
name: auth
description: Use when touching sign-in, sessions, passwords, the security stamp, the Admin role or Admin__Usernames, the startup admin reconciler, or the sign_in_events audit - what rotates a stamp, why nothing on the auth path is cached, and how administrators are granted and removed.
---

# Authentication, sessions and administrators

## Session invalidation

Every authenticated request costs one uncached read of the user's security stamp, in
`Program.cs`'s `OnValidatePrincipal`. That read is deliberately **not** cached: `HybridCache`
here is L1-only and cannot be evicted across pods, so caching it would let a revoked session
survive on another replica for the length of the TTL.

Rotating `User.SecurityStamp` invalidates every cookie already issued for that user. Three
things rotate it: a password change, the failed sign-in that locks an account, and
`POST /api/auth/sign-out-everywhere`.

Two things that will cost you time:

- **A cookie carrying no stamp claim is rejected.** That is what retires sessions issued before
  ADR 0011, and it means any change to `SessionClaims.SecurityStamp`'s value signs everybody out.
- **Changing a password re-issues the caller's own cookie.** `ChangePassword` returns a
  `SessionView` rather than a bool for exactly this reason; without the re-issue in
  `AuthController`, changing your own password signs you out.

Nothing on the auth path is cached, which is what stops a stale stamp being served from the
query cache. Do not make `GetUser` `ICacheable`.

See ADR 0011.

## The administrator role

`User.Role` is `Member` or `Admin`. Three things require `Admin`: the monitoring page
(`/api/monitoring/*`), order fulfilment (`/api/fulfilment/orders`, which is also the only place an
order is shipped), and writing to the catalogue (`POST`/`PUT /api/products`, per action — reading
it is every member's). Everything else is still "any authenticated user", because ADR 0007 puts
ownership in the query and every other endpoint returns only the caller's own data.

**Controllers name a capability, never a role.** `AuthorizationPolicies` holds `Orders.Fulfil`,
`Catalogue.Manage`, `Monitoring.Read`, `Monitoring.Operate` and `Users.Manage`; Program.cs maps
every one to the `Admin` role in a single loop over `AuthorizationPolicies.All`. A new privileged
endpoint takes the policy for what it lets someone do — add one if none fits, and list it in
`All`, or `AuthorizationPolicyTests` fails. Never `[Authorize(Roles = "Admin")]`: the point is that
a later permission model changes the loop and not the controllers. See ADRs 0024 and 0025.

**The role is read from the database on every authenticated request, never carried in the
cookie.** `SessionValidator` already pays for one projected, uncached, primary-key read per
request for ADR 0011's security stamp, so `Role` rides along on the same row and the same index
seek. `Program.cs`'s `OnValidatePrincipal` attaches it to the principal with `ReplacePrincipal`
and does not renew the cookie.

Four things that will cost you time:

- **A role change does NOT rotate the security stamp**, and must not. Nothing issued carries the
  role, so there is nothing stale to invalidate — and rotating would sign every administrator out
  on every API restart, courtesy of the startup reconciler. ADR 0011's own summary anticipated the
  opposite; ADR 0020 supersedes it, and the rotation list stays at three.
- **Configuration is a floor, not a mirror: `Admin__Usernames` only ever PROMOTES.** Startup
  promotes everyone listed and demotes nobody, so a grant made in the application survives a
  restart. **`RegisterUser` reads the same list**, so a configured operator who registers after
  the API started holds the role immediately rather than waiting for one; both paths ask one
  `IAdministratorDirectory` and both only promote, so they cannot disagree.
  **Removing an administrator therefore takes TWO steps** — demote them in the application *and*
  remove the name from `Admin__Usernames`. Either alone is insufficient, and the sharp one is
  demoting while the name is still configured: the next start promotes them straight back, and it
  reads as "the demotion didn't save". The reconciler logs one event per promotion naming the
  user, which is how you diagnose that from the log store. Keeping your own name listed is the
  deliberate break-glass path back in. ADR 0022 supersedes ADR 0020 here.
- **`Admin__ReconcileOnStart=false` is required by anything that boots the app without a
  database**, exactly like `Wolverine__Durable=false`. That is the OpenAPI contract command (the `regenerate` skill)
  and CI's `contract` job; `HealthTests` sets it too. `codegen write` does not need it — a JasperFx
  command never starts hosted services. Any NEW startup path that dials Postgres needs the same
  treatment, and `HealthTests` is the canary that catches it.
- **The reconciler lives in `src/Infrastructure/Security`, not beside the policy.** Surviving an
  absent database means catching both `DbException` and EF's `RetryLimitExceededException` (the
  execution strategy reports its own exhaustion rather than the inner fault), and `src/Api` carries
  no EF reference at all. Api composes it through `AddAdministratorRoles()`.

A blank or over-long entry in `Admin__Usernames` fails startup with an
`OptionsValidationException`; an empty list is legal and means nobody.

See ADR 0020.

## The sign-in audit

Every attempt to authenticate writes a `sign_in_events` row: the outcome, the username as typed,
the caller's address and user-agent, and a `TraceId`. `User.LastSeenAt` is stamped alongside it,
which is what "online now" on the monitoring page means.

**The audit records the difference the endpoint itself refuses to reveal.** `SignInHandler`
answers every failure with one uniform error — unknown username, wrong password and locked-out
are indistinguishable to the caller, deliberately (ADR 0006), and both the unknown-user and
locked-out branches hash a password they then discard so the timings match too. The audit
separates them because an operator investigating an attack needs that; the response does not,
because an attacker must not have it. `SignInAuditTests` asserts the two responses are identical
field for field, excluding only the per-request `traceId`.

Four things that will cost you time:

- **`sign_in_events` is personal data.** It holds an IP address and a user-agent against a
  username. `PruneSignInEvents` runs daily and keeps thirty days
  (`Monitoring__SignInEventRetentionDays`); that sweep is a requirement of the feature, not
  housekeeping, and lengthening the window is a decision rather than a default.
- **`LastSeenAt` is written on the hottest path in the application**, beside ADR 0011's uncached
  stamp read. The throttle is the UPDATE's own `WHERE` clause — one write per user per minute
  regardless of request volume, with no read to race against. Never make it read-modify-write, and
  never let it fail a request: `Program.cs` swallows `DbException` and EF's
  `RetryLimitExceededException` there on purpose.
- **`X-Forwarded-For` only means anything behind the ingress.** `ForwardedHeaders__Enabled` is set
  in the Kubernetes overlay; without it every address recorded in the cluster is the ingress pod's
  rather than the caller's — audit data that looks right until someone tries to use it.
  `ClientContext` reads the resolved `RemoteIpAddress`, so whatever that middleware decided is
  what gets stored.
- **`IClientContext` is registered per host, like `ICurrentUser`.** The API binds it to
  `HttpContext`; the worker registers `NoClientContext`. It cannot simply be absent there — the
  generic host validates every registered descriptor when it builds its container, so a missing
  implementation fails `codegen write` at container-build time, nowhere near a sign-in path that
  host does not have.

See ADR 0021.

