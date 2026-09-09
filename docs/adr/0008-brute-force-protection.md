# 0008. Brute-force protection: request rate limiting and a self-expiring account lockout

**Date:** 2026-09-08
**Status:** Accepted

## Context

ADR 0006 shipped username-and-password sign-in and named what it deliberately left out:

> **No brute-force protection.** Lockout was considered and declined for this slice, so passwords
> can be tried as fast as the server answers. The command shape leaves room to add it without a
> redesign. This should be revisited before the API faces the internet.

Nothing had been added since. `POST /api/auth/login` and `POST /api/auth/register` accepted
unlimited attempts at whatever rate the server could answer, and no state anywhere recorded that
an account had been guessed at.

The same ADR also spent real effort making sign-in refuse to answer "does this account exist?" —
one uniform `Error` for every failure mode, and a dummy password hash on an unknown username so
response *timing* does not answer it either. Anything added here has to hold that line, or it
undoes work that was done on purpose.

## Decision

**Two layers, because they defend against different things.** A request rate limiter on the auth
endpoints stops one client hammering. A per-account failed-attempt counter stops a distributed
attack, spread across many addresses, from grinding one account while each address individually
stays under the per-client limit. Neither substitutes for the other: the limiter cannot see that a
thousand addresses are all guessing at `alice`, and the counter cannot see that one address is
enumerating a thousand usernames.

**The lockout is five consecutive failures, fifteen minutes, fixed window, self-expiring.**
`MaxFailedSignInAttempts` (5) and `LockoutDuration` (fifteen minutes) are constants on `User` in
`src/Domain/Users/User.cs` — a business rule about what counts as an attack, not an operational
knob, so it lives in Domain rather than configuration. The counter clears on any successful
sign-in (`RegisterSuccessfulSignIn`). Fixed rather than sliding is the decision that matters more
than the numbers: `RegisterFailedSignIn(now)` checks first whether a previous lockout has expired,
and if so resets the count to 1 before incrementing, rather than leaving it standing at the
threshold. Without that reset, the very next failure after the window lapsed would reach the
threshold again and re-lock — and every failure after that would too. That is a sliding window
arriving through the back door, and it would let an attacker who knows one username hold that
account locked indefinitely at one guess per window, turning the protection into a
denial-of-service delivered on the attacker's own behalf. Serving out a lockout earns a fresh set
of attempts. A permanent lockout was not considered: there is no password-reset flow in this
codebase, so a permanently locked account would have no way out at all.

The fixed window is the weaker of those two claims, and worth stating honestly: it frees an
account only if the guessing stops. An attacker who keeps spending five guesses per window —
twenty requests an hour, far under the per-IP limit and cheap to sustain — holds the account
locked continuously without ever tripping the rate limiter. Fixed rather than sliding narrows
that from "locked forever on one guess per window" to "locked for as long as the attacker keeps
paying five", which is a real improvement but not an escape. The escape is a password reset, and
there is none here: an account under sustained attack has no self-service way back in until one
exists. That is inherent to account lockout without a reset flow, not to this implementation of
it, and it is the second reason — alongside the mistyped-password case below — that password
reset is the next slice.

**The lockout is silent: the identical 401 a wrong password produces.** `SignInHandler`
(`src/Application/Users/SignIn.cs`) checks `user.IsLockedOut(clock.UtcNow)` after the unknown-user
branch and before password verification, and on a lockout returns the same `Failed` error instance
— `ErrorKind.Unauthorized`, code `auth.failed`, "That username and password do not match." — that
every other failure path returns. A distinguishable "too many attempts" response here would be a
clean enumeration oracle: six wrong guesses against `alice` would return one status if `alice`
exists and another if she does not, handing an attacker a username scanner and undoing the
dummy-hash work ADR 0006 already did. The honest "slow down" message comes from the rate limiter
instead, as a 429, which counts clients rather than accounts and so distinguishes nothing about
which accounts exist. No new `ErrorKind` was needed: the lockout reuses `Unauthorized`, and the
429 is produced by ASP.NET Core's rate-limiting middleware, never passing through `Result` at all.

The locked-out branch still calls `hasher.Hash(command.Password)` and discards the result, exactly
as the unknown-user branch does. Returning early without hashing would let a locked account answer
measurably faster than a wrong-password one — the timing difference is exactly the account-
existence oracle the dummy hash exists to close. And the lockout check runs, deliberately, before
any counter update: an attempt made during an active lockout is refused without touching
`FailedSignInAttempts` or `LockedOutUntil`, which is what keeps the window fixed rather than
extending on every retry. `RegisterFailedSignIn` returns early on `IsLockedOut(now)` as well, so
that invariant is held by the type owning the two fields and not only by its one caller.

**The rate limiter partitions by IP address, not by username, and guards only login and
register.** `Program.cs` registers a fixed-window policy named `AuthRateLimiting.PolicyName`
(`src/Api/Auth/AuthRateLimiting.cs`), partitioned on `HttpContext.Connection.RemoteIpAddress`
(falling back to a shared `"unknown"` bucket when it is null, so an unidentifiable caller is
over-restricted rather than handed an unlimited partition). ASP.NET's rate limiter runs before
model binding, so a username sitting in the JSON body is not reachable without buffering and
rewinding the request — partitioning by client address is what the framework supports directly,
and it still leaks nothing, since it counts clients rather than accounts. `PermitLimit` and
`WindowSeconds` bind from `RateLimiting:Auth` configuration, defaulting to 10 requests per 60
seconds; being an operational tuning knob rather than a business rule is also why they live in
configuration, in contrast to the lockout's Domain constants — and being configuration is what
lets the test host raise the limit out of the way. `[EnableRateLimiting(AuthRateLimiting
.PolicyName)]` sits on `Login` and `Register` in `AuthController`, and both actions gained
`[ProducesResponseType(StatusCodes.Status429TooManyRequests)]`. `/me`, `/logout`, and
`change-password` are unguarded: the SPA calls `/me` on every page load and route change, where a
ten-per-minute cap would break ordinary use, and `/logout` is anonymous and harmless already.
`app.UseRateLimiter()` runs after `UseAuthentication`/`UseAuthorization` and before
`MapControllers()`, with `OnRejected` writing a `Retry-After` header whenever the limiter's lease
carries that metadata — rounded up, since truncating the remainder of a window emits
`Retry-After: 0` and a client that obeys it comes straight back into another 429.

`OnRejected` also writes the response body. A rejected request is short-circuited before the MVC
pipeline, so nothing else would give it one: `AddProblemDetails()` fills in a body for a thrown
exception, not for a status code set by middleware, and there is no `UseStatusCodePages` here.
The body is built by hand to the same shape `ResultExtensions.Problem` produces — `title`,
`detail`, `status`, and the `traceId` extension, served as `application/problem+json` — so a 429
is not a differently-shaped error for a client to special-case, and so the committed contract's
`429 → ProblemDetails` is true of the running application and not only of the document.

**The counter needs a write path independent of the unit of work — the change that shapes the
rest of the implementation.** `Behaviors.CommitAsync` (`src/Infrastructure/Messaging/
Behaviors.cs`) reads:

```csharp
if (!result.IsSuccess)
{
    return;
}
```

A failed sign-in returns a failed `Result`. A handler that just mutated the loaded `User` to bump
the counter would never have that mutation persisted — invisibly, because a unit test that
substitutes the repository and asserts the mutation happened would still pass; only production,
reading a counter that stays zero forever, would show the lockout never firing. So `IUserRepository`
gained two methods, both implemented in `UserRepository` with EF Core's `ExecuteUpdateAsync`
against `context.Users`. `ExecuteUpdateAsync` issues its own `UPDATE` immediately, outside the
tracked change set, so neither depends on `CommitAsync` running at all. This is safe against a
double-write because `GetByNormalizedUsernameAsync`, which `SignInHandler` calls to load the user,
is already `AsNoTracking` (unlike `GetAsync`, tracked because `ChangePassword` mutates what it
loads), so mutating the in-memory `User` the handler holds enqueues nothing in the change tracker.

**The failure write is conditional, because concurrent guesses on one account are the normal case
during an attack, not a rare interleaving.** `TryRecordFailedSignInAsync(userId, expectedAttempts,
attempts, lockedOutUntil, cancellationToken)` carries `FailedSignInAttempts == expectedAttempts`
in its `Where` alongside the id, and returns whether the `UPDATE` matched a row. The handler reads
the counter, then spends tens of milliseconds in PBKDF2 deciding what to write, so overlapping
requests routinely read the same value: an unconditional write would let N simultaneous guesses
all read `k` and all write `k+1`, advancing the counter by one per hash duration regardless of N
and firing the lockout after roughly `5 × N` guesses rather than 5. The account counter is the
only defence against an attacker spread across many addresses, and such an attacker is concurrent
by construction, so this is the case it has to survive. On a refused write `SignInHandler`
re-reads the user once and recomputes from the winner's value — one retry, not a loop, since a
second lost race means the counter is moving anyway, and giving up silently is safe because the
caller gets the same uniform failure either way. The threshold comparison and the increment stay
in `User.RegisterFailedSignIn`, deliberately: pushing them into the `SetProperty` expression would
put the lockout policy in Infrastructure, and it belongs in Domain with the constants.

`ClearSignInFailuresAsync(userId, cancellationToken)` is the success counterpart and is
unconditional on purpose — clearing is idempotent, and a successful sign-in should win over any
concurrent failed one. `SignInHandler` calls it only when there is something to clear
(`user.FailedSignInAttempts > 0 || user.LockedOutUntil is not null`), so an ordinary sign-in costs
no extra `UPDATE`.

## Consequences

The limiter is in-memory and per-instance: a second instance behind a load balancer doubles the
effective limit, since each instance counts independently. Behind a reverse proxy,
`RemoteIpAddress` is the proxy's own address unless `ForwardedHeaders` is configured, which would
collapse every client into one partition — whatever eventually deploys this has to wire that up,
and nothing in this repo does yet. A reverse proxy is only the extreme case of a more general
one: the budget is per address, not per person, so everyone behind a single NAT — an office, a
university, a mobile carrier's gateway — shares those ten requests a minute. Ten legitimate
sign-ins a minute from one such network is not far-fetched, and the tenth one gets a 429 it did
nothing to earn. That the per-account lockout is the layer doing the real work here is what makes
a limit that blunt acceptable for now.

A legitimate user who mistypes their password five times is locked out for fifteen minutes with no
explanation in the response and no way to shorten it. That silence is the accepted cost of not
handing out an enumeration oracle, and it is the strongest argument for password reset being the
next slice after this one — until it exists, the only way out of a self-inflicted lockout is
waiting.

`ApiFactory` (`tests/Api.IntegrationTests/ApiFactory.cs`) sets
`RateLimiting:Auth:PermitLimit` to 1,000,000 so the shared integration suite — which registers a
fresh user per test, all from one address, over `ApiFactoryCollection`'s one shared host — does not
exhaust a realistic limit incidentally. The limiter's own behavior is proven separately, in
`AuthRateLimitTests` (`tests/Api.IntegrationTests/Auth/AuthRateLimitTests.cs`), which stands up its
own `WebApplicationFactory<Program>` outside `ApiFactoryCollection` with a deliberately tiny limit
of 3 — the same pattern `HealthTests` already uses to stay off the shared container — and asserts
the 429, the `Retry-After` header (present, greater than zero, no longer than the window), and the
`ProblemDetails` body all in one test, since a shared fixed window across two tests in one class
would otherwise race.

The Playwright API server sets `RateLimiting__Auth__PermitLimit` to the same 1,000,000, in
`frontend/playwright.config.ts`'s `webServer.env`, for the same reason with a sharper edge: the
e2e API runs on defaults, all browser traffic reaches it through the Vite preview proxy, so the
limiter sees one address and one partition for the entire suite. `auth.spec.ts` spends five of
the ten default permits and `orders.spec.ts` adds two per test in `beforeEach` — the next spec
that signs up would walk into the wall, and the failure would surface as `sign-up.ts`'s
`waitForURL('**/orders')` timing out: indistinguishable from a flake, and not reproducible when
re-running the one spec.

**This branch introduces a small timing asymmetry, and it is worth naming rather than leaving for
someone to rediscover.** The wrong-password path now additionally awaits an indexed `UPDATE` that
neither the unknown-username path nor the locked-out path performs. Both of those still pay a
full dummy hash, so the dominant cost is matched; what is not matched is one round trip to
Postgres on a primary-key predicate. A sufficiently patient attacker measuring response times
could therefore separate "this username exists and I guessed wrong" from "this username does not
exist" — the same order of residual signal as the locked-out/wrong-password difference the design
already accepts, and far smaller than the difference the dummy hash exists to remove. Closing it
would mean issuing a matching pointless write on the miss paths, which is a worse trade: real
database work on every unauthenticated request, to hide a signal an attacker can only read
through many samples. Named here so the next person weighing "should the miss path write too?"
finds the reasoning instead of re-deriving it.

`SignIn` is now the first command in this codebase to persist anything outside the unit of work.
The XML docs on `IUserRepository.TryRecordFailedSignInAsync` and `ClearSignInFailuresAsync` carry
that explanation at the point future readers will find it: that `Behaviors.CommitAsync` commits
only a successful `Result`, and sign-in's failure path is exactly the one that has to persist
regardless. They also record the corollary — bypassing `SaveChangesAsync` bypasses
`DomainEventsInterceptor`, so a domain event raised on this path would never reach the outbox.
`User` raises none today, so nothing is broken; the note exists because adding one to
`RegisterFailedSignIn` would compile, pass its unit tests, and silently go nowhere.

The contract moved accordingly: `[ProducesResponseType(429)]` on `Login` and `Register`, and
`openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` were regenerated and committed,
changed only by the new 429 responses. Frontend handling of the 429 was left alone — the login
page's existing error rendering displays a problem-detail failure the same way it displays any
other, and because `OnRejected` writes a real `ProblemDetails` body (above), the 429 arrives as
one: `frontend/src/api/client.ts` reads its `detail` rather than falling back to its generic
"Request failed with status 429." So nothing needed to change there for this slice.

## Alternatives considered

**Letting a command opt into committing on failure**, e.g. a flag `Behaviors.CommitAsync` checks
before its early return. Rejected: it would weaken "one command, one transaction, only on success"
for every command in the application to serve one counter, and its failure mode — partial state
persisted from any failed command that opts in, anywhere, forever — is a large blast radius for a
narrow need.

**`Result<SignInOutcome>` carrying failure as a successful result**, so the unit of work commits
naturally on the ordinary path. Rejected on two counts: it inverts what `Result` is for — a failed
sign-in is not success — and it would force `AuthController` to branch on domain state to choose a
status code, which `src/Api/CLAUDE.md`'s "no `if` chains over domain state" rule forbids outright.

**Partitioning the rate limiter by username instead of by IP address.** An earlier draft of the
design proposed exactly this, and it turned out not to be buildable as stated: ASP.NET Core's rate
limiter runs before model binding and partitions on `HttpContext`, so reaching a username sitting
in the JSON request body would mean buffering the body and rewinding it for the model binder to
read again. Partitioning on `RemoteIpAddress` is what the framework supports directly without that
plumbing, and it still counts clients rather than accounts, so it leaks nothing the username
partition would have avoided leaking either.

**A sliding window for the lockout**, resetting the fifteen-minute clock on every failure rather
than only on the fifth. Rejected because it lets an attacker who knows a single username hold that
account locked out indefinitely, simply by continuing to guess once per window — the protection
becomes a denial-of-service the attacker delivers on the account owner's behalf, which is worse
than the brute-force risk it was meant to close.

**Escalating lockout durations** (fifteen minutes, then an hour, then a day, on repeated
offenses). Rejected as too punishing while there is no password-reset flow in this codebase: a
user who is locked out because they forgot their password, not because they are under attack, has
no way to shorten a growing lockout and no self-service way out of even the first one.
