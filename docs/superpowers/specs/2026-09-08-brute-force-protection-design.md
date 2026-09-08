# Brute-force protection: request rate limiting and a self-expiring account lockout

**Date:** 2026-09-08
**Status:** Approved, not yet implemented

## Context

ADR 0006 shipped username-and-password sign-in and named what it deliberately left out:

> **No brute-force protection.** Lockout was considered and declined for this slice, so passwords
> can be tried as fast as the server answers. The command shape leaves room to add it without a
> redesign. This should be revisited before the API faces the internet.

Nothing has been added since. `POST /api/auth/login` and `POST /api/auth/register` accept
unlimited attempts at whatever rate the server can answer, and no state anywhere records that an
account has been guessed at.

The same ADR also went to real lengths to stop sign-in responses answering "does this account
exist?" — one uniform error for every failure mode, and a dummy hash for unknown usernames so
response *timing* does not answer it either. Any protection added now has to hold that line, or it
undoes work that was done on purpose.

## Decisions

### Two layers, defending different things

**A request rate limiter** on the auth endpoints stops one client hammering. **A per-account
failed-attempt counter** stops a distributed attack, spread across many addresses, from grinding
one account while staying under the per-client limit. Neither substitutes for the other: the
limiter cannot see that a thousand addresses are all guessing at `alice`, and the counter cannot
see that one address is enumerating a thousand usernames.

### The lockout is self-expiring and its window is fixed

Five consecutive failures lock the account for fifteen minutes. The counter clears on any
successful sign-in. Attempts made *during* a lockout are refused without extending it.

Fixed rather than sliding is the decision that matters, more than the numbers. Under a sliding
window an attacker who knows a username can keep its owner locked out indefinitely simply by
continuing to guess — the protection becomes a denial-of-service delivered on the attacker's
behalf. A fixed window always frees itself.

Permanent lockout was not considered. There is no password-reset flow in this codebase, so a
locked account would have no self-service way out.

### The lockout is silent: the same 401 as a wrong password

A locked-out caller receives the identical response body a wrong password produces —
`ErrorKind.Unauthorized`, code `auth.failed`, message "That username and password do not match."

Answering "too many attempts" here would be a clean enumeration oracle: six wrong guesses against
`alice` would return one status if `alice` exists and another if she does not, handing an attacker
a username scanner and undoing `SignInHandler`'s dummy hash. The honest "slow down" message comes
from the rate limiter instead, which counts per client rather than per account and therefore
distinguishes nothing about which accounts exist.

**No new `ErrorKind` member is needed.** The lockout reuses `Unauthorized`; the 429 is produced by
middleware and never passes through `Result`.

### The rate limiter partitions by IP address

An earlier draft of this design said "partitioned by username". That is not buildable as stated:
ASP.NET's rate limiter runs before model binding and partitions on `HttpContext`, so reaching a
username in the JSON body would mean buffering the request body and rewinding it for the binder.
Partitioning on `RemoteIpAddress` is what the framework supports directly, and it still leaks
nothing — it counts clients, not accounts.

The limiter is applied to **login and register only**. Not `/me`: the SPA calls it on every page
load and on every route change behind the guard, and a ten-per-minute cap there would break
ordinary use. Not `/logout`, which is anonymous by design and harmless.

### The counter needs its own write path

This is the constraint that shapes the implementation, and it is not obvious.

`Behaviors.CommitAsync` reads:

```csharp
if (!result.IsSuccess)
{
    return;
}
```

A failed sign-in returns a failed `Result`. A handler that mutated the loaded user to increment
the counter would therefore **never persist it** — and the bug is invisible in a unit test that
substitutes the repository and asserts the mutation happened. In production the counter would read
zero forever and the lockout would never fire.

So the outcome is written through a dedicated repository method implemented with EF Core's
`ExecuteUpdateAsync`, which issues its own `UPDATE` immediately, outside the tracked change set and
independent of whether the unit of work commits. `SignIn` ends up depending on the commit behavior
not at all.

`GetByNormalizedUsernameAsync` is already `AsNoTracking` (unlike `GetAsync`, which is tracked
because `ChangePassword` mutates what it loads), so mutating the user the handler loaded enqueues
nothing and there is no double-write hazard between the tracked and untracked paths.

**Alternatives rejected.** Letting a command opt into committing on failure would weaken "one
command, one transaction, only on success" for every command in the application, and its failure
mode is partial state persisted from any failed command anywhere — a large blast radius bought for
one counter. Returning `Result<SignInOutcome>` with success-carrying-a-failure would make the unit
of work commit naturally, but it inverts what `Result` means and forces the controller to branch on
domain state to choose a status code, which `src/Api/CLAUDE.md` forbids outright.

### Policy lives in Domain

`MaxFailedSignInAttempts` and `LockoutDuration` are constants on `User`, not configuration. They
are a business rule about what counts as an attack, in the layer that owns business rules. The
rate limiter's numeric limits *are* configuration, because they are an operational tuning knob and
because the test host must be able to raise them (see Testing).

## The change, layer by layer

### Domain

`User` gains:

```csharp
/// <summary>Consecutive failures that trigger a lockout. Business rule, not configuration.</summary>
public const int MaxFailedSignInAttempts = 5;

public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

public int FailedSignInAttempts { get; private set; }

public DateTimeOffset? LockedOutUntil { get; private set; }

public bool IsLockedOut(DateTimeOffset now) => LockedOutUntil is { } until && until > now;

public void RegisterFailedSignIn(DateTimeOffset now);
public void RegisterSuccessfulSignIn();
```

`RegisterSuccessfulSignIn` sets the count to zero and `LockedOutUntil` to null.

`RegisterFailedSignIn(now)` does one thing first that is easy to miss: **if a lockout was set and
has since expired, the count restarts from that failure** — count becomes 1 and `LockedOutUntil`
is cleared. Only then does it increment and, on reaching `MaxFailedSignInAttempts`, set
`LockedOutUntil = now + LockoutDuration`.

Without that reset the count would still stand at 5 when the window lapsed, so the very next
failure would reach the threshold again and re-lock for another fifteen minutes, and every failure
after it would too. That is a sliding window arriving through the back door, and it would defeat
the fixed-window decision above — an attacker could hold an account locked indefinitely at one
guess per fifteen minutes. Serving out a lockout earns a fresh set of attempts.

Both methods are handed the clock's reading rather than reading a clock, the same way `Order.Place`
is handed `placedAt`.

`User.Register` initialises the count to zero and the expiry to null.

### Application

`IUserRepository` gains one method:

```csharp
/// <summary>
/// Writes the sign-in counter immediately, independently of the unit of work: a failed sign-in
/// returns a failed Result, and the unit-of-work behavior does not commit those.
/// </summary>
public Task RecordSignInOutcomeAsync(
    Guid userId, int attempts, DateTimeOffset? lockedOutUntil, CancellationToken cancellationToken);
```

`SignInHandler` gains `IClock` and runs in this order:

1. Look up by normalized username. Null → hash the supplied password and discard it, return the
   uniform failure. (Unchanged.)
2. `user.IsLockedOut(clock.UtcNow)` → hash the supplied password and discard it, return the same
   uniform failure. Return *before* touching the counter — that is what makes the window fixed.
3. Verify the password.
   - Success → `RegisterSuccessfulSignIn()`; persist via the port only when the counter was
     non-zero or a lockout was set, so an ordinary sign-in costs no extra `UPDATE`; return the
     session.
   - Failure → `RegisterFailedSignIn(clock.UtcNow)`; persist via the port; return the uniform
     failure.

**The dummy hash in step 2 is load-bearing, not copied ceremony.** Returning early on a lockout
without hashing would let a locked account answer measurably faster than a wrong-password one, and
the resulting timing difference is exactly the account-existence oracle the step-1 dummy hash
exists to close.

### Infrastructure

`UserRepository.RecordSignInOutcomeAsync` uses `ExecuteUpdateAsync` against `context.Users`
filtered by id, setting both columns. `UserConfiguration` maps `FailedSignInAttempts` as required
and `LockedOutUntil` as nullable.

Migration `AddSignInLockout` adds `FailedSignInAttempts integer NOT NULL DEFAULT 0` and
`LockedOutUntil timestamp with time zone NULL`.

**Keeping EF's scaffolded `defaultValue` is correct here**, in deliberate contrast to
`AddOrderOwner`, where it was stripped. An existing user genuinely has zero failed attempts, so
zero is a true value rather than a placeholder standing in for unknown data.

### Api

`Program.cs` registers a fixed-window limiter policy partitioned on
`HttpContext.Connection.RemoteIpAddress`, with `RejectionStatusCode = 429` and an `OnRejected`
callback that writes `Retry-After` when the limiter reports a retry hint. `app.UseRateLimiter()`
goes after `UseAuthentication`/`UseAuthorization` and before `MapControllers`.

`PermitLimit` and `Window` bind from configuration under `RateLimiting:Auth`, defaulting to 10
requests per minute.

`[EnableRateLimiting("auth")]` is applied to `Login` and `Register` only, with
`[ProducesResponseType(StatusCodes.Status429TooManyRequests)]` on both.

The contract therefore moves: `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts`
must be regenerated and committed. Frontend handling of the 429 is **out of scope** — the login
page's existing error rendering will display the problem detail as it does any other failure.

## Testing

**Domain** — `RegisterFailedSignIn` increments; the fifth call sets `LockedOutUntil` to exactly
`now + LockoutDuration`; the fourth does not; `IsLockedOut` is true one tick before expiry and
false at and after it; `RegisterSuccessfulSignIn` clears both. And the case that guards the
fixed-window decision: a failure arriving after an expired lockout resets the count to 1 and
clears the expiry, so it takes another five failures — not one — to lock again.

**Application** — with `IClock` and `IUserRepository` substituted:

- A locked-out user returns `Unauthorized` **and `IPasswordHasher.Verify` is never called** — the
  assertion that proves the lockout short-circuits rather than merely coinciding with a bad
  password.
- A locked-out user still calls `Hash` (the timing equaliser).
- The fifth consecutive failure persists a non-null `lockedOutUntil` through
  `RecordSignInOutcomeAsync`; the fourth persists null.
- An attempt during a lockout does **not** call `RecordSignInOutcomeAsync` at all — the fixed
  window, asserted directly.
- Success persists zeroes when the counter was non-zero, and skips the call when it was already
  zero.
- The failure `Error` is identical across the unknown-user, wrong-password and locked-out paths.

**Infrastructure** — against real Postgres: `RecordSignInOutcomeAsync` persists both columns and is
visible to a fresh context, proving `ExecuteUpdateAsync` writes without a `SaveChanges`.

**Api integration** — the end-to-end proof: register a user, fail five times, then sign in with the
**correct** password and still receive 401. Plus a byte-identical-body assertion between a
locked-out response and a wrong-password response, mirroring the existing comparison in
`AuthEndpointTests`.

**The rate limiter needs its own host.** `ApiFactory.CreateAuthenticatedClientAsync` registers a
fresh user for nearly every integration test, all from one address; a ten-per-minute cap on
`register` would break the suite outright. So `ApiFactory` sets `RateLimiting:Auth:PermitLimit`
high enough to be irrelevant, and the limiter's own behavior is tested by a separate class with its
own `WebApplicationFactory` configured with a deliberately tiny limit — the pattern `HealthTests`
already uses to stay outside `ApiFactoryCollection`.

## Consequences

- **The limiter is in-memory and per-instance.** Two instances double the effective limit. Fine
  today; a shared limiter is a deployment-time concern, and there is no deployment configuration in
  this repo.
- **Behind a proxy, `RemoteIpAddress` is the proxy**, collapsing every client into one partition.
  Whatever deploys this must configure `ForwardedHeaders` or the limiter becomes a global cap.
- **A legitimate user who forgets their password five times is locked out for fifteen minutes with
  no explanation and no reset flow.** This is the accepted cost of the silent lockout. It is also
  the strongest argument for password reset being the next slice after this one.
- **`SignIn` now writes on the failure path**, which makes it the first command in the codebase to
  persist anything outside the unit of work. The XML doc on `RecordSignInOutcomeAsync` is where
  that surprise is explained.

## Out of scope

- **Password reset and account recovery** — the natural next slice, and what makes lockout humane.
- **Frontend handling of 429** — the existing error display covers it adequately.
- **CAPTCHA, proof-of-work, or device fingerprinting.**
- **Rate limiting anything but login and register.**
- **Distributed/shared rate-limiter state**, which has nowhere to live until there is a deployment.

## Follow-on

ADR 0008, recording the two layers, the fixed self-expiring window, the silent lockout and its
enumeration-oracle reasoning, and why the counter needs a write path independent of the unit of
work.
