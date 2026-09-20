# 0011. Session invalidation on a rotating security stamp

**Date:** 2026-09-11
**Status:** Accepted

> **Recorded retrospectively on 2026-09-20.** The implementation landed on 2026-09-11 across
> `fa74a98` and the commits around it; `3c31998` added the root `CLAUDE.md` section that cites
> this ADR, but the ADR itself was never written. The number was reserved rather than reused —
> ADR 0012 explicitly took the next number and left 0011 for "whoever eventually writes the
> session-invalidation ADR it already implies exists", and the same gap was recorded again as F8
> in the e2e design and a third time in the centralized-logging plan. By then 17 references to
> ADR 0011 existed, nine of them in shipped source and test code. This file is that ADR, written
> from those call sites and the code they annotate rather than from a contemporaneous design
> note. Nothing here is a new decision; it is the reasoning already carried in
> `User.SecurityStamp`, `ISessionValidator`, `SessionValidator`, `ChangePassword`, and
> `Program.cs`'s `OnValidatePrincipal`, collected where an ADR reference expects to find it.

## Context

ADR 0006 shipped sign-in as a cookie session: authenticate once, and the cookie is the authority
for every later request until it expires. ADR 0008 then added a per-account lockout on top of
that, and in doing so created a gap it did not close.

**A cookie, once minted, answered to nothing.** The cookie handler validates the ticket's
signature and its expiry, and nothing else. Between issue and expiry there was no point at which
the application got to say "that session should no longer work". Three situations made that
untenable:

1. **A password change did not end other sessions.** Changing a password is the action people
   take *because* they believe someone else has their credentials. Leaving every previously
   issued cookie working is the one outcome that makes the action pointless — the attacker's
   session is exactly the one that survives.
2. **A lockout did not reach a session already inside the account.** ADR 0008's lockout stops
   further *guessing*; it does nothing to an attacker who already guessed correctly and is
   holding a cookie. The lockout would fire, the account would refuse new sign-ins, and the
   intruder would carry on uninterrupted. This is the gap the integration tests still call
   "ADR 0011 gap 2".
3. **There was no way to sign out everywhere.** A user on a lost or shared device had no way to
   revoke it, because there was nothing to revoke: no server-side record of a session existed.

The shape of the problem is that a cookie session is stateless by design, and all three of these
need the server to be able to change its mind after the fact.

## Decision

**Every user carries an opaque `SecurityStamp`; the cookie carries the stamp it was issued
under; every authenticated request compares the two.** A mismatch rejects the session. Rotating
the stored stamp therefore invalidates every cookie already issued for that user, atomically and
without enumerating them.

`User.SecurityStamp` (`src/Domain/Users/User.cs`) is the stored value, with `RotateSecurityStamp()`
as the single primitive. The claim type is `SessionClaims.SecurityStamp` — `"aiframework:stamp"`,
named once in `src/Api/Auth/SessionClaims.cs` because it is written in `AuthController` and read
in `Program.cs`, and a typo across those two would fail in the worse direction.

**Three things rotate the stamp, and the list is deliberately short:**

| Rotated by | Why |
|---|---|
| `User.ChangePassword` | Ends every session issued under the old password |
| `User.RegisterFailedSignIn`, **only on the failure that locks** | Cuts off an attacker already inside the account — gap 2 above |
| `SignOutEverywhere` | The explicit user-facing revocation |

**Rotating only on the locking failure is the decision that matters most in that table.**
Rotating on every wrong guess would mean that anyone who knows a username can sign that user out
at will, at one request per attempt — a denial of service handed to an attacker for free, and a
strictly worse outcome than the gap it would be closing. Rotation happens once, on the fifth
consecutive failure, at the same moment the lockout is applied.

**The check runs in `OnValidatePrincipal`, before the endpoint sees the request.** Putting it in
the cookie handler's own validation event rather than in middleware or a filter is what makes it
unconditional: there is no endpoint, attribute, or code path that can forget it. The event
resolves `ISessionValidator` from `context.HttpContext.RequestServices` per request rather than
capturing it in the lambda, because the validator is scoped and holds the scoped `DbContext` —
capturing it would leak one `DbContext` across every request in the process.

**A stale session is rejected, never thrown.** `RejectAsync` drops the principal and clears the
cookie, so the request continues unauthenticated and lands on the same `OnRedirectToLogin` a
request with no cookie at all would — a 401. A client cannot distinguish "your session was
revoked" from "you were never signed in", which is the point: the former is an expected state,
not a fault, and not something to explain to whoever is holding the cookie.

**A cookie with no stamp claim is rejected, not tolerated.** This is the only migration story
there is. Cookies minted before this change carry no `aiframework:stamp` claim, and treating a
missing claim as "no opinion" would leave precisely the sessions this feature exists to retire
working until they expired. Failing closed retires them on their holder's next request. The same
fail-closed branch is written twice on purpose — in `Program.cs` before the validator is resolved
at all, and again at the top of `SessionValidator.IsStampCurrentAsync` — so the invariant belongs
to the type that answers the question and not only to its one caller.

The corollary is worth stating plainly, because it is a loaded gun: **changing the *value* of
`SessionClaims.SecurityStamp` signs every user out.** Every live cookie carries the old claim
name, which the new code reads as absent, which fails closed. It is a one-line edit with a
whole-population blast radius.

**The read is uncached, permanently and on purpose.** `SessionValidator`
(`src/Infrastructure/Persistence/SessionValidator.cs`) issues one `AsNoTracking`, projected,
primary-key read per authenticated request. `HybridCache` in this repo is L1-only and cannot be
evicted across pods (ADR 0010), while whoever rotates a stamp — an administrator, or the same
user on another device — is, under the ingress cookie affinity, almost certainly on a *different*
pod from the session being invalidated. Caching this read would reintroduce exactly the staleness
the stamp exists to remove, for the length of the TTL, on the one code path where staleness means
a revoked session still works. This is why the root `CLAUDE.md` says `GetUser` must not become
`ICacheable`, and why ADR 0015 names this read alongside ADR 0008's lockout state as something
that must happen on every request regardless of what else is cached.

The projection and `AsNoTracking` are not incidental either: this runs on every request, and
materialising a tracked `User` nobody asked for would put it in the change tracker, to be scanned
by `SaveChanges` and by `DomainEventsInterceptor` on any later write in the same scope.

**Changing your own password re-issues your own cookie.** `ChangePassword` returns a
`SessionView` rather than a `bool` for exactly this reason, and `AuthController.ChangeOwnPassword`
re-issues the cookie from the rotated stamp it carries. Without that, the rotation would take
effect against the caller too and changing your own password would sign you out — which is not
what rotation is for; it is meant to end *other* sessions. The re-issue reads the existing
cookie's `IsPersistent` first and carries it over, because re-issuing with a hard-coded `false`
would silently downgrade a "remember me" session to a browser-session cookie as a side effect of
a password change.

**The lockout's rotation is passed through the repository, not the tracked entity.** ADR 0008
established that the failed-sign-in write has to bypass the unit of work, because
`Behaviors.CommitAsync` returns early on a failed `Result` and a failed sign-in is exactly that.
So the rotated stamp travels as `TryRecordFailedSignInAsync`'s `rotatedSecurityStamp` parameter —
non-null only on the failure that locks — and is written by the same `ExecuteUpdateAsync` as the
counter and the expiry. It cannot ride on the tracked entity, because on this path nothing
tracked is ever saved.

## Consequences

**Every authenticated request costs one uncached database read.** A primary-key lookup on a
pooled connection, returning a single string, but it is unconditional and it scales with request
volume rather than with sign-in volume. That is the price of immediate invalidation, and it was
paid deliberately. The exit, if it ever measurably matters, is interval-based revalidation in the
manner of ASP.NET Core Identity's `SecurityStampValidator` — which trades immediacy for
throughput, and would reopen a bounded version of the gap this ADR closes. It is recorded in
`SessionValidator`'s own summary so that whoever profiles this finds the intended escape rather
than reaching for the cache.

**Sign-out-everywhere also clears the calling browser's cookie**, via `SignOutAsync`. The
rotation alone would already fail that cookie's next validation, so this is tidiness rather than
the security boundary — it avoids one guaranteed 401 round trip before the SPA notices.

**Any e2e or integration test that changes a password or signs out everywhere invalidates the
session it is running under.** This is why `frontend/e2e/CLAUDE.md` carries "session-invalidating
actions take an isolated user" as one of its two rules that are correctness rather than style,
and why ADR 0012's D4 gives those specs a `freshUser`/`isolatedPage` fixture instead of the
worker's shared `storageState`. A spec that forgets it does not fail where the mistake is; it
fails in whatever runs next against the same worker.

The same effect shapes `AuthEndpointTests`: the client probing for the lockout oracle must never
have registered, because a prober that *was* signed in as the account it is locking would see its
own session invalidated by the very lockout it is causing, adding `Set-Cookie` and cache-control
headers that are about the prober rather than about whether the account is distinguishable.

**Gap 2 is closed; the lockout now reaches live sessions.**
`SessionInvalidationTests.LockingTheAccount_EndsItsLiveSessions` asserts it from the outside,
with a cookie-less attacker, which is the only arrangement that proves the reach rather than the
mechanism.

**Nothing on the auth path may become cacheable.** This ADR, ADR 0008's lockout state, and ADR
0009's caller-scoped cache meet at the same rule from three directions. It is stated in the root
`CLAUDE.md` under both "Session invalidation" and "Caching", and it is a permanent constraint
rather than a current state.

## Alternatives considered

**Server-side session storage** — a sessions table, one row per live session, deleted on
revocation. Rejected: it is a strictly larger mechanism for the same outcome. It needs a write on
every sign-in, a cleanup path for expired rows, and a read on every request that the stamp
approach also needs — so it costs more and buys only per-session granularity, which nothing here
asks for. Every rotation trigger in the table above is per-*user* by nature.

**Short-lived cookies with silent refresh.** Rejected: it bounds the staleness window rather than
removing it, and the bound is the cookie lifetime. A revoked session keeps working until the next
refresh, which is precisely the property the password-change case cannot tolerate. It also does
not remove the per-request read, since the refresh has to check *something*.

**Caching the stamp read with a short TTL.** Rejected, and the reasoning is above rather than
here because it is the alternative most likely to be re-proposed: L1-only `HybridCache` cannot be
evicted across pods, and the rotating party is almost always on a different pod from the session
being revoked, so the TTL becomes a window in which a revoked session still works. It is recorded
in three places for that reason.

**Rotating the stamp on every failed sign-in**, rather than only on the locking one. Rejected: it
hands anyone who knows a username the ability to sign that user out at one request per attempt.

**Accepting pre-stamp cookies until they expire**, to avoid signing everyone out on deploy.
Rejected: those are exactly the sessions with no invalidation story at all, and tolerating them
would mean the feature does not take effect until the last old cookie lapses. One sign-out at
deploy time is a smaller cost than a window in which the guarantee is untrue.
