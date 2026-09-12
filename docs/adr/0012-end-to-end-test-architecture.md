# 0012. End-to-end test architecture

**Date:** 2026-09-12
**Status:** Accepted

## Context

The Playwright suite this replaces was six tests in two files, against a stack Playwright
started itself, with no fixtures, no abstraction over locators, and `Date.now()`-based
uniqueness safe only because the suite was small. It worked, and every workaround in it was
documented, but it was sized for six tests written by one person, not for the two things this
repository actually needs from it: many more features, and more people writing tests for them.
Without a documented shape, "more people" means more inconsistent tests, each reinventing its
own answer to questions — how do I get a signed-in session, how do I avoid stepping on another
test's data, does this test even run against the cluster — that should have one answer each.

Two findings, on inspection, turned out to constrain the design more than anything else:

**The rate limiter's partition collapses behind the ingress.** ADR 0008's limiter partitions on
`HttpContext.Connection.RemoteIpAddress`, and nothing in `Program.cs` trusted a forwarded
header. Behind `ingress-nginx`, every request arrives from the ingress pod's own address, so the
entire suite — every worker, every test — shared one partition capped at the cluster's
10-per-60-seconds budget. A registration per test would cap a cluster run at roughly ten tests a
minute, and the failure would not announce itself as a rate limit: it would surface as a
`waitForURL` timeout on an unrelated navigation, indistinguishable from a flake.

**`/health` through the ingress lies.** Verified directly rather than argued: with
`deployment/api` scaled to 0 and its pods fully deleted, `GET /health` through the ingress
returned **HTTP 200** — nginx's `try_files $uri $uri/ /index.html` matches no file and falls
back to serving the SPA, so a readiness probe on that path reports the stack healthy with zero
API pods running. `GET /api/auth/me` through the same ingress returned **HTTP 503** at the same
moment, correctly. `deploy/e2e-k8s.ps1`, gating on `/api/auth/me`, refused to run against the
scaled-down cluster; a version gating on `/health` would not have.

The full design, including the layout, the fixture and screen contracts, and the target
resolution mechanics, is recorded at
`docs/superpowers/specs/2026-09-12-e2e-test-architecture-design.md`. This ADR condenses the
decisions that change an existing assumption or commit the framework to a shape, and amends the
one existing ADR whose premise this work runs into.

## Decision

**Registration happens once per worker, over HTTP, and reused via `storageState` — not once per
test.** This is the direct answer to the rate-limiter finding above: auth calls scale with
worker count, not test count, so a run of any size makes a small, fixed number of calls against
the shared budget. `workerUser` (worker-scoped) is what `signedInPage` signs in as; a test that
needs a session nobody else has touched takes `freshUser` (test-scoped) instead. The cost of
reuse is that `workerUser`'s data accumulates across a worker's tests, which is what makes
"assert contains, never equals or is empty" on any list a correctness rule rather than a style
preference — sharpened further by the cluster's own Postgres being a StatefulSet with a
persistent volume, so a worker's data survives not just the run but the redeploy.

**The suite targets `local`, `kind`, or an arbitrary URL, and the target resolution rules out any
database-level seeding or cleanup.** `support/target.ts` resolves `E2E_TARGET` to a base URL and
a small set of per-target behaviours (does Playwright manage the stack, does the connection
ignore a self-signed certificate). Arranging or tearing down data by reaching into Postgres
directly would work against the compose stack and silently not exist as an option against kind,
reached only through the ingress — a design that works on one target and not the other is not a
shared suite, it is two suites that happen to share source files. Every test therefore arranges
its own data the same way regardless of target: over the application's own HTTP API, via the
`api` fixture (`await api.placeOrder(...)`, `await api.placeOrders(...)`). Only the auth
endpoints are rate limited, so bulk arrangement — placing twenty-five orders for a pagination
test, say — is free on every target.

**Fixtures plus screen modules, not page-object classes.** `screens/*.ts` are plain exported
functions — locators, and interactions that act and, where unambiguous, wait — with no class and
no shared base to extend. A page-object class earns its keep when a suite needs polymorphism
over pages (a `BasePage` that different page types specialise) or needs to bundle state across
calls; this suite needs neither. What it needs is one place per route where a locator is
defined once, and Playwright's own fixture system already provides scoping, setup, and teardown
for anything stateful — reimplementing that inside a constructor would be working against the
tool rather than with it. The one rule a class model does not enforce any better than a plain
function does is kept as a convention instead: **assertions never live in a screen.** They stay
in the spec, where the reader can see, in one place, what the test claims — a screen's job is to
find and act, never to judge.

**No shared seed dataset; arrange through the API, and no cleanup of test users on the
cluster.** A SQL fixture or seed script was considered and rejected for the same reason
database-level arrangement was above: it cannot reach kind through the ingress, so it would
either not exist there or require a second, database-connected code path just for that target.
It would also be a second source of truth for what a valid user or order looks like, drifting
from the domain model over time, and shared seeded rows under parallel workers is a well-known
route to flakes. A narrower version of the same idea — **seeding one known e2e account into the
cluster at deploy time**, so a remote run makes zero auth calls instead of one per worker — was
also considered and rejected: two workers means two registrations against a budget of ten, so
the problem the seeded account would solve does not exist at the scale this suite runs at. It is
recorded here as a deliberate non-decision rather than an oversight, in case suite growth ever
makes worker count large enough to revisit it.

Test users are never removed from the cluster's database. The mechanism that would remove them —
an admin "delete user" endpoint — was considered and rejected outright: **a destructive endpoint
that exists only so tests can clean up after themselves is a production hole built for test
convenience**, and this framework's non-goals explicitly rule out any admin or test-only endpoint
in the API. The accepted cost is that the cluster's Postgres accumulates e2e users and orders for
as long as it lives; the documented recycle is `deploy/teardown.ps1` followed by
`deploy/start-cluster.ps1`, which discards the volume along with the cluster.

**`ForwardedHeadersOptions` is registered, but gated behind `ForwardedHeaders__Enabled` and off
by default — this amends ADR 0008.** ADR 0008 partitioned the auth rate limiter on
`RemoteIpAddress` and named the gap explicitly in its own Consequences: "behind a reverse proxy,
`RemoteIpAddress` is the proxy's own address unless `ForwardedHeaders` is configured, which would
collapse every client into one partition — whatever eventually deploys this has to wire that up,
and nothing in this repo does yet." Kubernetes is that deployment, and this ADR is where the
wiring lands. The naive fix opens a wider hole than it closes: trusting `X-Forwarded-For` at all
requires clearing ASP.NET Core's proxy allow-list (empty by default, trusting only loopback), and
once that trust is cleared, a caller who can reach the API directly — bypassing the ingress
entirely — can set the header to whatever it likes and mint a fresh rate-limit partition on every
request, a complete bypass of ADR 0008's volume defence rather than a fix for it. The decision is
therefore conditional, in `Program.cs`:

```csharp
if (builder.Configuration.GetValue("ForwardedHeaders:Enabled", defaultValue: false))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}
```

Only `XForwardedFor` is forwarded — nothing in this application reads `Request.IsHttps`, since
`CookieSecurePolicy.Always` is unconditional in Production, so forwarding the protocol would
change behaviour for no benefit. `ForwardLimit` of 1 assumes exactly one hop — the ingress — so a
client cannot prepend its own address and choose which one the limiter sees. `KnownIPNetworks`
is the current name of the property that clears the trusted-network allow-list; the SDK this
repository targets marks the old `KnownNetworks` name obsolete under ASPDEPR005, an error here
because warnings are errors, and a reviewer confirmed by reflection that `KnownNetworks` and
`KnownIPNetworks` return the same object instance — clearing one clears both, so the rename is
cosmetic, not a behaviour change. `app.UseForwardedHeaders()` is registered unconditionally,
before `app.UseRateLimiter()`; with the flag off, `ForwardedHeadersOptions` keeps its defaults,
which forward nothing, so the middleware is a no-op rather than absent. Only
`k8s/overlays/local/config.yaml` sets `ForwardedHeaders__Enabled: 'true'` — `dotnet run`, the
e2e compose stack, and the integration test host all keep the flag off and the old,
already-broken-behind-a-proxy behaviour, which is at least not an active bypass.

`ForwardedHeadersTests` (`tests/Api.IntegrationTests/Auth`) proves both directions with the flag
off and on, each against a deliberately tiny permit limit so two spoofed addresses either share
a partition or do not. The flag-off test is the one that matters most: it is the regression guard
against the gate ever being "simplified" away. Both tests also settle a question the design
raised rather than answered up front — whether `ForwardedHeadersMiddleware` rewrites cleanly from
`WebApplicationFactory`'s `TestServer`, which leaves `Connection.RemoteIpAddress` null. It does:
both tests pass with no `IStartupFilter` or other fallback needed to give TestServer a non-null
starting address.

**Kubernetes is an additional gate, not the everyday loop, and it has no CI job yet.** The local
compose run stays the fast, isolated, disposable loop; `./deploy/e2e-k8s.ps1` runs the same specs
(minus `@local-only`) against the deployed cluster, which is the only place that exercises
durable Wolverine, real cache eviction, and two API replicas behind cookie affinity — ADR 0010's
claims, argued in a document with no test behind them until this suite existed. It assumes the
cluster is already deployed and fails loudly, naming `deploy/start-cluster.ps1`, rather than
silently triggering three image builds and a rollout on every invocation. No CI job runs it:
standing up a kind cluster, building three images, and waiting on the ingress would add ten or
more minutes to every push, for a gate this repository does not yet have evidence needs to run
that often. That is a decision to revisit if the local kind path shows signs of rotting from
disuse, not a permanent exemption.

**Two specs are tagged `@local-only`, and a cluster run therefore executes 8 of the suite's 12
tests, not 12.** `registration.spec.ts` spends three auth-endpoint calls across its two tests (a
UI registration, an `api.register`, and a duplicate-username attempt); `change-password.spec.ts`
spends four across its two — the first test only registers a fresh user, the second registers
one and then signs in twice, once with the old password and once with the new. Seven of the
suite's worth of auth calls sit in those two files — untagged, a cluster run would spend most of
its 10-per-60-seconds budget on them before the rest of the suite got a permit. `run.ts` passes
`--grep-invert @local-only` for any target but `local`, so `npm run e2e` still runs all twelve
locally, where the test host's rate limit is raised out of the way, and a cluster run predictably
executes the other eight.

## Consequences

`frontend/e2e/CLAUDE.md` is the primary artifact of this decision, more than the code it
documents. It loads automatically for anyone working in that directory and carries the two rules
that are correctness rather than style — session-invalidating actions take an isolated user, and
list assertions say "contains" — plus the fixture table, the `@local-only` checklist, and the
per-worker registration arithmetic. `frontend/CLAUDE.md` and the root `CLAUDE.md` point at it
rather than duplicating it.

`docs/adr/0011` remains unwritten. The root `CLAUDE.md` cites it twice for session invalidation,
and this ADR takes the next number, **0012**, rather than the gap — 0011 stays reserved for
whoever eventually writes the session-invalidation ADR it already implies exists.

### Accepted trade-offs

**The kind run's coverage is smaller than the local run's, by design, and that gap could grow.**
Eight of twelve tests today is a ratio specific to the current spec inventory; a new
`@local-only` test narrows the cluster run further; a suite that grows mostly in tagged specs
would eventually leave kind covering a shrinking fraction of the whole. Nothing enforces a floor.
The mitigation is the arithmetic being written down in `frontend/e2e/CLAUDE.md`, not a hard
budget line one test could silently blow through — if that changes, it changes with a visible
count.

**Test users and orders accumulate on the cluster forever**, for as long as it lives, with no
cleanup path short of destroying the cluster. This is the direct cost of rejecting an admin
delete endpoint, and it is accepted because the alternative — building the production hole
anyway, scoped "only for test convenience" — does not stay scoped. `deploy/teardown.ps1` is the
only recycle, and it is documented as such.

**The `ForwardedHeaders__Enabled` flag is a footgun by construction, not despite it.** Once set,
the API trusts a header any client can set, wherever the client can reach it. The correctness of
this design rests entirely on the flag being true only where the ingress is provably the sole
path in — which is true of this kind cluster and would need re-verifying, not assuming, for any
other deployment target this repository grows toward.

**No CI job runs the Kubernetes suite.** Its correctness rests on someone running
`./deploy/e2e-k8s.ps1` by hand, which working software does not enforce. The local suite and the
unit/integration suites are what CI actually gates on; the kind run is a manual, additional
check, and this ADR records that as a known gap rather than a discovered one.

## Alternatives considered

The rejected shapes for the two decisions with the most alternatives on the table are argued
where those decisions are made, above: a shared seed dataset and a seeded cluster account, for
data setup; page-object classes, for the abstraction layer; an admin delete endpoint, for
cleanup. Two more were on the table for the framework as a whole:

**Per-test registration**, the shape the suite started with. Rejected because it is the direct
cause of F1 above: it caps a cluster run at the rate limiter's raw budget in tests per minute,
regardless of how fast the tests themselves would otherwise run.

**Opt-in tagging (`@smoke` or similar) instead of opt-out (`@local-only`).** Rejected because an
opt-in set decays silently — a new test simply is not added to it, and nothing fails, so the
"tests that run everywhere" set quietly shrinks to whatever was tagged early on. Opt-out inverts
the failure mode: a test that needs something only the managed stack has and is not tagged fails
immediately and loudly against kind, naming the cause, rather than passing everywhere and being
wrong about the assumption.

See also ADR 0008, whose partition-collapse gap this ADR closes with a gate rather than an
unconditional fix, and ADR 0010, whose durable-Wolverine, real-cache, and two-replica claims this
suite's kind run is the first thing to actually exercise.
