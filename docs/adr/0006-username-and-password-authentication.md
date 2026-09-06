# 0006. Username and password authentication, on a session cookie

**Date:** 2026-09-06
**Status:** Accepted

## Context

`docs/superpowers/specs/2026-09-02-react-ui-design.md` put "Authentication and authorisation"
under **Out of scope**, and a later slice shipped a login page anyway — a form calling
`POST /api/auth/login`, an endpoint that returned 404. `frontend/src/features/auth/types.ts` said
so in its own header: its interfaces were hand-written precisely because there was nothing in
`openapi/AiFramework.Api.json` to alias.

So the question was not whether to add authentication but which shape, against a codebase with
some strong existing opinions: Domain may reference neither EF Core nor DataAnnotations, enforced
twice over (`.claude/hooks/dependency-rule.ps1` at edit time and
`Domain.Tests/ArchitectureTests` on assembly references); commands, not queries, get the
validation and unit-of-work behaviors (ADR 0003); and `.claude/hooks/no-secrets.ps1` blocks a
bare `Key` in any `appsettings*.json`, with a `Jwt:Key` fixture to prove it.

Only username and password. No external providers, no 2FA.

## Decision

**The session is an HttpOnly cookie**, via ASP.NET Core cookie authentication.

The SPA is served same-origin — `vite.config.ts` proxies `/api` in dev, preview and e2e, which is
also why this repo has never needed CORS — so the browser attaches the cookie unaided and no
JavaScript ever holds the session. An XSS bug has nothing to steal, which is not true of a token
in `localStorage`. It also needs no signing key, and the no-secrets hook above means a JWT key
could not have lived in configuration anyway.

The cookie is `HttpOnly`, `SameSite=Lax`, sliding, seven days, and `Secure` in Production but
`SameAsRequest` in Development — the dev API is plain HTTP on 5234, and a flat `Always` risks the
cookie being dropped locally. `SameSite=Lax` is the CSRF defence: it withholds the cookie on
cross-site POST, and every endpoint binds JSON, which a cross-site HTML form cannot send. No
antiforgery token is issued on top of that.

The cookie handler's default 302-to-a-login-page is overridden to 401/403. Left alone, an
unauthenticated `fetch` would see a 404 of HTML and could not tell "signed out" from "broken".

**The `User` aggregate is hand-rolled, not ASP.NET Core Identity.** `IdentityUser` is an EF-shaped
type; Domain cannot reference EF Core, so Identity's model could not have lived there and the
aggregate would have ended up in Infrastructure with a real exception to the dependency matrix.
`User` instead has the shape `Order` already has — private constructor, private setters, a static
factory named for the business act, a bare `Guid` id passed in by the caller.

Only `PasswordHasher<T>` is borrowed, from `Microsoft.Extensions.Identity.Core`. That package is
a plain library: no `UserManager`, no `SignInManager`, no EF stores, no eight tables. PBKDF2 with
a versioned format byte (so the work factor can be raised later without invalidating existing
hashes) and a constant-time comparison are exactly the parts not worth hand-rolling.

**Domain is handed an already-hashed password**, the same way `Order.Place` is handed a timestamp
rather than reading a clock: hashing needs a dependency and Domain has none. `IPasswordHasher` is
an Application port; the implementation is Infrastructure's.

**All four use cases are commands**, sign-in included. Only commands get the validation and
unit-of-work behaviors, so grouping them gives all four one shape — and leaves a lockout counter
somewhere to be written later without moving the seam.

**`ErrorKind` gains `Unauthorized`, mapped to 401.** It had `Validation`, `NotFound` and
`Conflict`, and `ResultExtensions.Problem` falls through to 500 for anything else — so without a
new member a rejected sign-in was a 500.

**Case-insensitive usernames via a normalized column.** `UsernameNormalized` (uppercase
invariant) carries the unique index, so "Ada" and "ada" are one account without needing the
Postgres `citext` extension. `User.Normalize` is the single place that folding happens.

**Registration is public**, and signs the new account straight in.

## Consequences

Every orders integration test was calling anonymously, and `[Authorize]` on `OrdersController`
broke all of them at once. They now go through `ApiFactory.CreateAuthenticatedClientAsync`, which
registers a fresh user per client — fresh rather than shared because these tests all run against
one database via `ApiFactoryCollection`, where a shared account would let one test's password
change invalidate another's session. The e2e specs register through the UI for the same reason:
`global-teardown.ts` drops the database every run and nothing seeds it, so this follows the
`SKU-E2E-${Date.now()}` idiom already there rather than adding `storageState` machinery.

A failed sign-in returns one error whatever went wrong, and hashes the supplied password even
when no user matched, so neither the response body nor its timing answers "does this account
exist?". `AuthEndpointTests` asserts the two bodies are byte-identical apart from `traceId`.

The frontend moved from email to username throughout — the login page had been built against
`email`, which no longer exists in the contract. `features/auth/types.ts` is now aliases over the
generated schema, as its own comment anticipated, so a backend rename breaks the build.

**Three things this deliberately does not do:**

- **No brute-force protection.** Lockout was considered and declined for this slice, so passwords
  can be tried as fast as the server answers. The command shape leaves room to add it without a
  redesign. This should be revisited before the API faces the internet.
- **Registration is open.** Anyone who can reach the API can create an account. Chosen, not
  overlooked — but not something to expose publicly unchanged.
- **Data protection keys are wherever ASP.NET puts them by default**, which is the local
  filesystem. In a container that means every restart invalidates every session, and two
  instances cannot read each other's cookies. Fine for development; a real deployment needs a
  persisted, shared key ring. There is no deployment configuration in this repo yet, so that
  decision has nowhere to live today.

The same-origin requirement is now load-bearing rather than incidental. Production must serve the
SPA and the API from one origin, or the cookie stops flowing and this design needs revisiting
(`credentials: 'include'` plus CORS with credentials, at minimum).

## Alternatives considered

**JWT bearer token.** Stateless, survives a cross-origin deployment, and the conventional API
answer. Rejected on storage: `localStorage` is readable by any XSS, in-memory means re-login on
every refresh, and a usable session length needs refresh-token machinery — a lot of moving parts
to end up less safe than a cookie the browser manages. It would also have needed a signing key
the no-secrets hook blocks from configuration.

**Opaque session id in a cookie, with the session row in Postgres.** Same transport, plus instant
server-side revocation and a listable set of active sessions. Rejected for now as a table, a
lookup on every request and a cleanup job bought for a revocation story nothing yet asks for.
The claims-cookie can be swapped for this later without the frontend noticing.

**Full ASP.NET Core Identity.** Brings lockout, 2FA, token providers and password policy for
free. Rejected on the dependency rule, per Decision — and because most of what it brings is
either out of scope or (lockout) explicitly deferred. Worth revisiting if external providers or
2FA are ever actually wanted, but that is a migration, not a default.

**Storing a plaintext-verifiable password in Domain, or hashing inside the aggregate.** Rejected:
Domain has no DI, and giving it a crypto dependency to satisfy one aggregate inverts the layering
the repo is built on.
