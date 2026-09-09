# Brute-Force Protection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop password guessing, with a per-IP rate limiter on the credential endpoints and a self-expiring per-account lockout that stays silent.

**Architecture:** `User` gains a consecutive-failure count and a lockout expiry, with the policy as Domain constants. `SignInHandler` consults and updates them, writing through a dedicated `ExecuteUpdateAsync` repository method because the unit-of-work behavior does not commit failed commands — and the failure path is the one that must persist. A fixed-window ASP.NET rate limiter partitioned by remote address guards `login` and `register`.

**Tech Stack:** .NET 10 (net10.0), ASP.NET Core rate limiting (`Microsoft.AspNetCore.RateLimiting`, in the shared framework — no package reference needed), EF Core + Npgsql, xUnit + FluentAssertions + NSubstitute, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-08-brute-force-protection-design.md`

## Global Constraints

- **Warnings are errors** — compiler, analyzers and build (`Directory.Build.props`). Never suppress without a narrow `#pragma warning disable`/`restore` pair carrying a justification comment above it.
- **Nullable is enabled.** **Never `catch (Exception)`** — CA1031 is an error.
- **`required` keyword in Domain, never `[Required]`.** Domain may not reference EF Core, ASP.NET, DI, `System.Data` or DataAnnotations — a hook blocks the edit.
- **Handlers never call `SaveChangesAsync`.** This plan does not change that: the new repository method uses `ExecuteUpdateAsync`, which is not `SaveChanges`.
- **Never hand-edit a *committed* EF migration.** `.claude/hooks/protect-migrations.ps1` keys on `git ls-tree HEAD`, so a migration you have generated and not yet committed is yours to adjust.
- **One Testcontainer per collection, never per class.** New database-backed classes join `PostgresCollection` or `ApiFactoryCollection`. A class that needs no database uses its own `IClassFixture<WebApplicationFactory<Program>>`, as `HealthTests` does.
- **The uniform sign-in failure is exactly** `ErrorKind.Unauthorized`, code `auth.failed`, message `"That username and password do not match."` — unchanged, and every new failure path returns that same `Error`.
- **Policy values are Domain constants:** `User.MaxFailedSignInAttempts = 5`, `User.LockoutDuration = TimeSpan.FromMinutes(15)`. The rate limiter's numbers are configuration; the lockout's are not.
- Dev database connection string for `dotnet ef`: `Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres`
- Run `dotnet test <project>` **one project at a time** — two paths in one invocation fails with MSB1008.
- Branch: `feat/brute-force-protection`, already created; the spec is already committed on it.

---

## File Structure

**Created:**
- `src/Api/Auth/AuthRateLimiting.cs` — the policy name constant, so `Program.cs` and `AuthController` cannot disagree about it.
- `src/Infrastructure/Persistence/Migrations/<timestamp>_AddSignInLockout.cs` — generated.
- `tests/Api.IntegrationTests/Auth/AuthRateLimitTests.cs` — the limiter's own behaviour, container-free.
- `docs/adr/0008-brute-force-protection.md`

**Modified:**
- `src/Domain/Users/User.cs` — lockout state, policy constants, three methods.
- `src/Application/Users/IUserRepository.cs` — `RecordSignInOutcomeAsync`.
- `src/Application/Users/SignIn.cs` — the handler consults and updates the counter.
- `src/Infrastructure/Persistence/UserRepository.cs` — `ExecuteUpdateAsync` implementation.
- `src/Infrastructure/Persistence/Configurations/UserConfiguration.cs` — map the two columns.
- `src/Api/Program.cs` — register and use the limiter.
- `src/Api/Auth/AuthController.cs` — `[EnableRateLimiting]` + `[ProducesResponseType(429)]` on `Login` and `Register`.
- `tests/Api.IntegrationTests/ApiFactory.cs` — raise the limit out of the way.
- `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts` — regenerated.
- Test files in all four test projects.

**Deliberately unchanged:** `src/Application/Abstractions/Result.cs` (no new `ErrorKind`), every Api DTO, all frontend source.

---

### Task 1: The lockout lives on the user

Domain state, its EF mapping and the migration together — adding mapped properties without the columns breaks every test that reads a user.

**Files:**
- Modify: `src/Domain/Users/User.cs`
- Modify: `src/Infrastructure/Persistence/Configurations/UserConfiguration.cs`
- Create: `src/Infrastructure/Persistence/Migrations/<timestamp>_AddSignInLockout.cs` (generated)
- Test: `tests/Domain.Tests/Users/UserTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `User.MaxFailedSignInAttempts` (`const int` = 5); `User.LockoutDuration` (`static readonly TimeSpan` = 15 minutes); `User.FailedSignInAttempts` (`int`, private setter); `User.LockedOutUntil` (`DateTimeOffset?`, private setter); `bool User.IsLockedOut(DateTimeOffset now)`; `void User.RegisterFailedSignIn(DateTimeOffset now)`; `void User.RegisterSuccessfulSignIn()`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Domain.Tests/Users/UserTests.cs`, inside the existing class. It already has a `RegisteredAt`-style constant and an `AUser()`-shaped helper — read the file and match its existing naming rather than inventing new helpers.

```csharp
    private static readonly DateTimeOffset SignInAt = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_StartsWithACleanSignInRecord()
    {
        var user = User.Register(
            Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);

        user.FailedSignInAttempts.Should().Be(0);
        user.LockedOutUntil.Should().BeNull();
        user.IsLockedOut(SignInAt).Should().BeFalse();
    }

    [Fact]
    public void RegisterFailedSignIn_BelowTheThreshold_CountsButDoesNotLock()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);

        for (var i = 0; i < User.MaxFailedSignInAttempts - 1; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        user.FailedSignInAttempts.Should().Be(User.MaxFailedSignInAttempts - 1);
        user.LockedOutUntil.Should().BeNull();
        user.IsLockedOut(SignInAt).Should().BeFalse();
    }

    [Fact]
    public void RegisterFailedSignIn_OnTheThresholdFailure_LocksForTheLockoutDuration()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);

        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        user.LockedOutUntil.Should().Be(SignInAt + User.LockoutDuration);
    }

    [Fact]
    public void IsLockedOut_IsTrueBeforeTheExpiryAndFalseAtIt()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);
        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        var expiry = SignInAt + User.LockoutDuration;

        user.IsLockedOut(expiry.AddTicks(-1)).Should().BeTrue();
        user.IsLockedOut(expiry).Should().BeFalse("the window is closed at the instant it expires");
        user.IsLockedOut(expiry.AddMinutes(1)).Should().BeFalse();
    }

    [Fact]
    public void RegisterSuccessfulSignIn_ClearsTheCounterAndTheLockout()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);
        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        user.RegisterSuccessfulSignIn();

        user.FailedSignInAttempts.Should().Be(0);
        user.LockedOutUntil.Should().BeNull();
    }

    /// <summary>
    /// The test that guards the fixed-window decision. Without the reset, the count would still
    /// stand at the threshold when the window lapsed, so the next single failure would re-lock —
    /// and every failure after it would too, which is a sliding window arriving through the back
    /// door and lets an attacker hold an account locked indefinitely at one guess per window.
    /// </summary>
    [Fact]
    public void RegisterFailedSignIn_AfterAnExpiredLockout_StartsCountingFromOne()
    {
        var user = User.Register(Guid.NewGuid(), "Ada", "hash", "Ada Lovelace", SignInAt);
        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            user.RegisterFailedSignIn(SignInAt);
        }

        var afterExpiry = SignInAt + User.LockoutDuration + TimeSpan.FromMinutes(1);
        user.RegisterFailedSignIn(afterExpiry);

        user.FailedSignInAttempts.Should().Be(1, "serving out a lockout earns a fresh set of attempts");
        user.LockedOutUntil.Should().BeNull();
        user.IsLockedOut(afterExpiry).Should().BeFalse();
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Domain.Tests --filter "FullyQualifiedName~UserTests"`
Expected: FAIL to build — `CS0117: 'User' does not contain a definition for 'MaxFailedSignInAttempts'`.

- [ ] **Step 3: Add the state and the policy to the aggregate**

In `src/Domain/Users/User.cs`, add the constants beside the existing `MaxUsernameLength`/`MaxDisplayNameLength`:

```csharp
    /// <summary>
    /// Consecutive failed sign-ins that trigger a lockout. A business rule about what counts as
    /// an attack, so it lives here rather than in configuration.
    /// </summary>
    public const int MaxFailedSignInAttempts = 5;

    /// <summary>How long a lockout lasts. Fixed, never sliding — see RegisterFailedSignIn.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
```

Add the properties after `RegisteredAt`:

```csharp
    /// <summary>Consecutive failures since the last successful sign-in.</summary>
    public int FailedSignInAttempts { get; private set; }

    /// <summary>When the current lockout expires, or null when the account is not locked.</summary>
    public DateTimeOffset? LockedOutUntil { get; private set; }
```

No change to the private constructor: EF matches its parameters by name and sets the two new properties through their private setters.

Add the three methods after `ChangePassword`:

```csharp
    public bool IsLockedOut(DateTimeOffset now) => LockedOutUntil is { } until && until > now;

    /// <summary>
    /// Records a failed attempt, locking the account on the threshold failure. Handed the time
    /// rather than reading a clock, the same reason <see cref="Orders.Order.Place"/> is handed
    /// a timestamp.
    /// </summary>
    public void RegisterFailedSignIn(DateTimeOffset now)
    {
        // Serving out a lockout earns a fresh set of attempts. Without this reset the count
        // would still stand at MaxFailedSignInAttempts when the window lapsed, so the very next
        // failure would reach the threshold again and re-lock — and every failure after it would
        // too. That is a sliding window arriving through the back door: it would let an attacker
        // hold an account locked indefinitely at one guess per window.
        if (LockedOutUntil is not null && !IsLockedOut(now))
        {
            FailedSignInAttempts = 0;
            LockedOutUntil = null;
        }

        FailedSignInAttempts++;

        if (FailedSignInAttempts >= MaxFailedSignInAttempts)
        {
            LockedOutUntil = now + LockoutDuration;
        }
    }

    public void RegisterSuccessfulSignIn()
    {
        FailedSignInAttempts = 0;
        LockedOutUntil = null;
    }
```

- [ ] **Step 4: Run the Domain tests**

Run: `dotnet test tests/Domain.Tests --filter "FullyQualifiedName~UserTests"`
Expected: PASS.

- [ ] **Step 5: Map the columns**

In `src/Infrastructure/Persistence/Configurations/UserConfiguration.cs`, after the `RegisteredAt` line:

```csharp
        builder.Property(u => u.FailedSignInAttempts).IsRequired();

        // Nullable by design: null means "not locked", which is a different state from "locked
        // until a time in the past" and avoids a sentinel date.
        builder.Property(u => u.LockedOutUntil);
```

- [ ] **Step 6: Generate the migration**

Make sure the dev database is up (`docker compose up -d --wait`), then:

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef migrations add AddSignInLockout --project src/Infrastructure --startup-project src/Infrastructure
```

- [ ] **Step 7: Check the generated migration, and keep its default**

Open the generated file. It should add `FailedSignInAttempts` as `integer NOT NULL` with `defaultValue: 0` and `LockedOutUntil` as nullable `timestamp with time zone`.

**Keep the `defaultValue: 0`.** This is the deliberate opposite of `AddOrderOwner`, where the scaffolded default was stripped: an existing user genuinely has zero failed attempts, so zero is a true value rather than a placeholder standing in for data nobody has. Make no other edit.

- [ ] **Step 8: Apply it and run the database suites**

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
dotnet test tests/Infrastructure.Tests
dotnet test tests/Api.IntegrationTests
```

Expected: PASS. Both projects read users, so this is what proves the mapping and the migration agree with the model.

- [ ] **Step 9: Commit**

```bash
git add src/Domain/Users/User.cs src/Infrastructure/Persistence tests/Domain.Tests
git commit -m "feat(auth): a user records failed sign-ins and a lockout expiry"
```

---

### Task 2: Persisting the counter without the unit of work

**Files:**
- Modify: `src/Application/Users/IUserRepository.cs`
- Modify: `src/Infrastructure/Persistence/UserRepository.cs`
- Test: `tests/Infrastructure.Tests/Persistence/UserRepositoryTests.cs`

**Interfaces:**
- Consumes: `User.FailedSignInAttempts`, `User.LockedOutUntil` from Task 1.
- Produces: `Task IUserRepository.RecordSignInOutcomeAsync(Guid userId, int attempts, DateTimeOffset? lockedOutUntil, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the failing test**

Append to `tests/Infrastructure.Tests/Persistence/UserRepositoryTests.cs`, matching the file's existing fixture style (it is `[Collection(nameof(PostgresCollection))]` and uses `fixture.CreateContext()`):

```csharp
    /// <summary>
    /// The property that makes the whole lockout work: this writes WITHOUT a SaveChanges call.
    /// SignIn's failure path returns a failed Result, and Behaviors.CommitAsync does not commit
    /// those — a tracked mutation there would be discarded silently, and the counter would read
    /// zero forever.
    /// </summary>
    [Fact]
    public async Task RecordSignInOutcomeAsync_PersistsWithoutSaveChanges()
    {
        var id = Guid.NewGuid();
        var lockedUntil = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        await using (var seed = fixture.CreateContext())
        {
            await new UserRepository(seed).AddAsync(
                User.Register(id, $"u{id:N}"[..32], "hash", "Ada Lovelace", RegisteredAt),
                CancellationToken.None);
            await new UnitOfWork(seed).SaveChangesAsync(CancellationToken.None);
        }

        await using (var write = fixture.CreateContext())
        {
            await new UserRepository(write).RecordSignInOutcomeAsync(
                id, attempts: 5, lockedOutUntil: lockedUntil, CancellationToken.None);
            // Deliberately no SaveChangesAsync here.
        }

        await using var verify = fixture.CreateContext();
        var found = await new UserRepository(verify).GetAsync(id, CancellationToken.None);

        found.Should().NotBeNull();
        found.FailedSignInAttempts.Should().Be(5);
        found.LockedOutUntil.Should().Be(lockedUntil);
    }

    [Fact]
    public async Task RecordSignInOutcomeAsync_CanClearALockout()
    {
        var id = Guid.NewGuid();
        await using (var seed = fixture.CreateContext())
        {
            await new UserRepository(seed).AddAsync(
                User.Register(id, $"u{id:N}"[..32], "hash", "Ada Lovelace", RegisteredAt),
                CancellationToken.None);
            await new UnitOfWork(seed).SaveChangesAsync(CancellationToken.None);
        }

        await using (var write = fixture.CreateContext())
        {
            var repository = new UserRepository(write);
            await repository.RecordSignInOutcomeAsync(
                id, 5, new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero), CancellationToken.None);
            await repository.RecordSignInOutcomeAsync(id, 0, null, CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var found = await new UserRepository(verify).GetAsync(id, CancellationToken.None);

        found.Should().NotBeNull();
        found.FailedSignInAttempts.Should().Be(0);
        found.LockedOutUntil.Should().BeNull();
    }
```

If the file has no `RegisteredAt` constant, add `private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);` to the class.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~UserRepositoryTests"`
Expected: FAIL to build — `CS1061: 'UserRepository' does not contain a definition for 'RecordSignInOutcomeAsync'`.

- [ ] **Step 3: Add the port method**

In `src/Application/Users/IUserRepository.cs`:

```csharp
    /// <summary>
    /// Writes the sign-in counter immediately, independently of the unit of work.
    /// <para>
    /// This exists because <c>Behaviors.CommitAsync</c> commits only a successful Result, and
    /// sign-in's failure path is the one that must persist. A tracked mutation there would be
    /// discarded without an error, leaving the counter at zero forever and the lockout dead.
    /// </para>
    /// </summary>
    public Task RecordSignInOutcomeAsync(
        Guid userId, int attempts, DateTimeOffset? lockedOutUntil, CancellationToken cancellationToken);
```

- [ ] **Step 4: Implement it**

In `src/Infrastructure/Persistence/UserRepository.cs`:

```csharp
    /// <summary>
    /// ExecuteUpdateAsync, not a tracked mutation: it issues its own UPDATE straight away, so it
    /// does not depend on the unit of work committing. See the port's documentation.
    /// </summary>
    public Task RecordSignInOutcomeAsync(
        Guid userId, int attempts, DateTimeOffset? lockedOutUntil, CancellationToken cancellationToken) =>
        context.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.FailedSignInAttempts, attempts)
                    .SetProperty(u => u.LockedOutUntil, lockedOutUntil),
                cancellationToken);
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~UserRepositoryTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Application/Users/IUserRepository.cs src/Infrastructure/Persistence/UserRepository.cs tests/Infrastructure.Tests
git commit -m "feat(auth): persist the sign-in counter outside the unit of work"
```

---

### Task 3: Sign-in consults and updates the lockout

**Files:**
- Modify: `src/Application/Users/SignIn.cs`
- Test: `tests/Application.Tests/Users/SignInHandlerTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 and 2, plus `IClock` (`AiFramework.Application.Abstractions`, one member `DateTimeOffset UtcNow`).
- Produces: `SignInHandler(IUserRepository users, IPasswordHasher hasher, IClock clock)`.

- [ ] **Step 1: Write the failing tests**

In `tests/Application.Tests/Users/SignInHandlerTests.cs`, add the clock substitute and a locked-out helper to the existing class, and update **every** existing `new SignInHandler(_users, _hasher)` to `new SignInHandler(_users, _hasher, _clock)`:

```csharp
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly IClock _clock = Substitute.For<IClock>();

    public SignInHandlerTests() => _clock.UtcNow.Returns(Now);

    /// <summary>An Ada who has just used up her last attempt, so she is locked at <see cref="Now"/>.</summary>
    private static User ALockedOutAda()
    {
        var ada = AnAda();
        for (var i = 0; i < User.MaxFailedSignInAttempts; i++)
        {
            ada.RegisterFailedSignIn(Now);
        }

        return ada;
    }
```

Then add these tests:

```csharp
    [Fact]
    public async Task HandleAsync_WhenLockedOut_FailsWithoutCheckingThePassword()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ALockedOutAda());
        var handler = new SignInHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(new SignIn("Ada", "correct horse"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        // The assertion that proves the lockout short-circuited, rather than merely coinciding
        // with a wrong password: a correct password must not get in either.
        _hasher.DidNotReceive().Verify(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleAsync_WhenLockedOut_StillHashesThePassword()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ALockedOutAda());
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "guess"), CancellationToken.None);

        // Without this a locked account answers measurably faster than a wrong password, and the
        // timing difference is the account-existence oracle the unknown-username hash closes.
        _hasher.Received(1).Hash("guess");
    }

    [Fact]
    public async Task HandleAsync_WhenLockedOut_DoesNotExtendTheLockout()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ALockedOutAda());
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "guess"), CancellationToken.None);

        // A fixed window: attempts made during a lockout must not touch the counter at all,
        // or an attacker could hold the account locked forever by continuing to guess.
        await _users.DidNotReceiveWithAnyArgs().RecordSignInOutcomeAsync(
            default, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_WhenLockedOut_FailsWithTheSameErrorAsAWrongPassword()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ALockedOutAda());
        var handler = new SignInHandler(_users, _hasher, _clock);
        var lockedOut = await handler.HandleAsync(new SignIn("Ada", "guess"), CancellationToken.None);

        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(AnAda());
        _hasher.Verify("stored-hash", "guess").Returns(false);
        var wrongPassword = await handler.HandleAsync(new SignIn("Ada", "guess"), CancellationToken.None);

        lockedOut.Error.Should().Be(wrongPassword.Error);
    }

    [Fact]
    public async Task HandleAsync_WithTheWrongPassword_RecordsTheFailure()
    {
        var ada = AnAda();
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "wrong").Returns(false);
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "wrong"), CancellationToken.None);

        await _users.Received(1).RecordSignInOutcomeAsync(
            ada.Id, 1, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_OnTheThresholdFailure_RecordsALockout()
    {
        var ada = AnAda();
        for (var i = 0; i < User.MaxFailedSignInAttempts - 1; i++)
        {
            ada.RegisterFailedSignIn(Now);
        }

        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "wrong").Returns(false);
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "wrong"), CancellationToken.None);

        await _users.Received(1).RecordSignInOutcomeAsync(
            ada.Id,
            User.MaxFailedSignInAttempts,
            Now + User.LockoutDuration,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithTheRightPasswordAfterFailures_ClearsTheCounter()
    {
        var ada = AnAda();
        ada.RegisterFailedSignIn(Now);
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(ada);
        _hasher.Verify("stored-hash", "correct horse").Returns(true);
        var handler = new SignInHandler(_users, _hasher, _clock);

        var result = await handler.HandleAsync(new SignIn("Ada", "correct horse"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _users.Received(1).RecordSignInOutcomeAsync(ada.Id, 0, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithTheRightPasswordAndACleanRecord_WritesNothing()
    {
        _users.GetByNormalizedUsernameAsync("ADA", Arg.Any<CancellationToken>()).Returns(AnAda());
        _hasher.Verify("stored-hash", "correct horse").Returns(true);
        var handler = new SignInHandler(_users, _hasher, _clock);

        await handler.HandleAsync(new SignIn("Ada", "correct horse"), CancellationToken.None);

        // An ordinary sign-in is the common case and must not cost a pointless UPDATE.
        await _users.DidNotReceiveWithAnyArgs().RecordSignInOutcomeAsync(
            default, default, default, default);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~SignInHandlerTests"`
Expected: FAIL to build — `CS1729: 'SignInHandler' does not contain a constructor that takes 3 arguments`.

- [ ] **Step 3: Rewrite the handler**

Replace `SignInHandler` in `src/Application/Users/SignIn.cs`. The `SignIn` record and `SignInValidator` above it are unchanged.

```csharp
public sealed class SignInHandler(IUserRepository users, IPasswordHasher hasher, IClock clock)
    : ICommandHandler<SignIn, SessionView>
{
    /// <summary>
    /// One error for every failure mode — unknown username, wrong password, locked out. A
    /// distinct "too many attempts" would turn this endpoint into a way to enumerate accounts,
    /// which is the same reason the unknown-username branch hashes and discards below.
    /// </summary>
    private static readonly Error Failed = new(
        ErrorKind.Unauthorized, "auth.failed", "That username and password do not match.");

    public async Task<Result<SessionView>> HandleAsync(
        SignIn command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var user = await users
            .GetByNormalizedUsernameAsync(User.Normalize(command.Username), cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            // Hash the supplied password and throw the result away. Hashing costs the same order
            // as verifying, so an unknown username takes about as long as a known one with the
            // wrong password - without this, response time answers "does this account exist?".
            _ = hasher.Hash(command.Password);

            return Result.Failure<SessionView>(Failed);
        }

        var now = clock.UtcNow;

        if (user.IsLockedOut(now))
        {
            // Hash and discard for the same timing reason as above: returning early here without
            // hashing would make a locked account answer measurably faster than a wrong password.
            // And deliberately no counter update — the window is fixed, so attempts made during
            // a lockout must not extend it.
            _ = hasher.Hash(command.Password);

            return Result.Failure<SessionView>(Failed);
        }

        if (!hasher.Verify(user.PasswordHash, command.Password))
        {
            user.RegisterFailedSignIn(now);
            await users
                .RecordSignInOutcomeAsync(
                    user.Id, user.FailedSignInAttempts, user.LockedOutUntil, cancellationToken)
                .ConfigureAwait(false);

            return Result.Failure<SessionView>(Failed);
        }

        // Only when there is something to clear: an ordinary sign-in is the common case and must
        // not cost an UPDATE that writes the values already there.
        if (user.FailedSignInAttempts > 0 || user.LockedOutUntil is not null)
        {
            user.RegisterSuccessfulSignIn();
            await users
                .RecordSignInOutcomeAsync(
                    user.Id, user.FailedSignInAttempts, user.LockedOutUntil, cancellationToken)
                .ConfigureAwait(false);
        }

        return Result.Success(new SessionView(user.Id, user.Username, user.DisplayName));
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~SignInHandlerTests"`
Expected: PASS, existing tests included.

- [ ] **Step 5: Run the whole backend**

```bash
dotnet build
dotnet test tests/Domain.Tests
dotnet test tests/Application.Tests
dotnet test tests/Infrastructure.Tests
dotnet test tests/Api.IntegrationTests
```

Expected: PASS, 0 warnings. `RegistrationCompletenessTests` in `Infrastructure.Tests` reflects over the Application assembly and will fail if the handler's new dependency broke its registration.

- [ ] **Step 6: Commit**

```bash
git add src/Application/Users/SignIn.cs tests/Application.Tests
git commit -m "feat(auth): five failures lock an account, silently"
```

---

### Task 4: The rate limiter

**Files:**
- Create: `src/Api/Auth/AuthRateLimiting.cs`
- Modify: `src/Api/Program.cs`
- Modify: `src/Api/Auth/AuthController.cs`
- Modify: `tests/Api.IntegrationTests/ApiFactory.cs`
- Create: `tests/Api.IntegrationTests/Auth/AuthRateLimitTests.cs`
- Regenerate: `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `AiFramework.Api.Auth.AuthRateLimiting.PolicyName` (`const string` = `"auth"`); configuration keys `RateLimiting:Auth:PermitLimit` (int, default 10) and `RateLimiting:Auth:WindowSeconds` (int, default 60).

- [ ] **Step 1: Write the failing test**

Create `tests/Api.IntegrationTests/Auth/AuthRateLimitTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Auth;

/// <summary>
/// The limiter's own behaviour, on its own host with a deliberately tiny limit. Outside
/// ApiFactoryCollection on purpose: the shared factory raises the limit out of the way so that
/// registering a user per test does not exhaust it, which is exactly what this class must not do.
/// </summary>
public sealed class AuthRateLimitTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const int PermitLimit = 3;

    private readonly WebApplicationFactory<Program> _factory;

    public AuthRateLimitTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            // No container: every request this class sends is rejected by the validator (empty
            // username) or by the limiter, so none of them reaches the database. Same placeholder
            // trick HealthTests uses, and the same reason Wolverine durability is off.
            builder.UseSetting(
                "ConnectionStrings:Default",
                "Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder");
            builder.UseSetting("Wolverine:Durable", "false");
            builder.UseSetting(
                "RateLimiting:Auth:PermitLimit", PermitLimit.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("RateLimiting:Auth:WindowSeconds", "60");
        });
    }

    [Fact]
    public async Task PostLogin_BeyondThePermitLimit_Returns429()
    {
        using var client = _factory.CreateClient();
        var empty = new { Username = "", Password = "", RememberMe = false };

        for (var i = 0; i < PermitLimit; i++)
        {
            var allowed = await client.PostAsJsonAsync("/api/auth/login", empty);
            allowed.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "an empty username fails validation before touching the database, but it is still a permitted request");
        }

        var rejected = await client.PostAsJsonAsync("/api/auth/login", empty);

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task PostLogin_WhenRejected_SaysWhenToComeBack()
    {
        using var client = _factory.CreateClient();
        var empty = new { Username = "", Password = "", RememberMe = false };

        for (var i = 0; i < PermitLimit + 1; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", empty);
        }

        var rejected = await client.PostAsJsonAsync("/api/auth/login", empty);

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull(
            "a 429 with no Retry-After leaves a well-behaved client guessing");
    }
}
```

Add `using System.Globalization;` to the file's usings for `CultureInfo`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~AuthRateLimitTests"`
Expected: FAIL — the fourth request returns 400, not 429, because no limiter is registered.

- [ ] **Step 3: Add the policy name**

Create `src/Api/Auth/AuthRateLimiting.cs`:

```csharp
namespace AiFramework.Api.Auth;

/// <summary>
/// The rate-limiter policy guarding the credential endpoints. A named constant so Program.cs
/// and the controller cannot drift apart over a string literal.
/// </summary>
public static class AuthRateLimiting
{
    public const string PolicyName = "auth";
}
```

- [ ] **Step 4: Register the limiter**

In `src/Api/Program.cs`, add these usings:

```csharp
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
```

`AiFramework.Api.Auth` is already imported for `CurrentUser`. Add this registration immediately after the `AddScoped<ICurrentUser, CurrentUser>()` line:

```csharp
// Volume defence on the credential endpoints, alongside the per-account lockout in
// SignInHandler. Partitioned by remote address, not by username: the limiter runs before model
// binding, so a username in the JSON body is not reachable without buffering and rewinding the
// request body. Counting clients rather than accounts also means this 429 distinguishes nothing
// about which accounts exist - which is why the lockout itself stays silent. ADR 0008.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    var permitLimit = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", defaultValue: 10);
    var windowSeconds = builder.Configuration.GetValue("RateLimiting:Auth:WindowSeconds", defaultValue: 60);

    options.AddPolicy(AuthRateLimiting.PolicyName, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Null for a request with no remote address (in-memory test hosts, some proxies).
            // One shared "unknown" bucket is the safe direction: it over-restricts rather than
            // handing every such caller its own unlimited partition.
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
            }));

    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };
});
```

Then add the middleware after `app.UseAuthorization();`:

```csharp
app.UseRateLimiter();
```

- [ ] **Step 5: Apply the policy to the credential endpoints**

In `src/Api/Auth/AuthController.cs`, add `using Microsoft.AspNetCore.RateLimiting;`, then add to **`Register`** and **`Login`** only — not `Logout`, not `Me`, not `ChangeOwnPassword`:

```csharp
    [EnableRateLimiting(AuthRateLimiting.PolicyName)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
```

`Me` is deliberately excluded: the SPA calls it on every page load and on every guarded route change, and a ten-per-minute cap there would break ordinary use.

- [ ] **Step 6: Raise the limit in the shared test host**

In `tests/Api.IntegrationTests/ApiFactory.cs`, inside `ConfigureWebHost`, beside the existing `UseSetting` call:

```csharp
        // CreateAuthenticatedClientAsync registers a fresh user for nearly every test in this
        // project, all from one address. The production limit would exhaust itself partway
        // through the run and fail tests that have nothing to do with rate limiting, so this
        // host sets it out of the way. The limiter's own behaviour is covered by
        // AuthRateLimitTests, which stands up its own host with a tiny limit.
        builder.UseSetting("RateLimiting:Auth:PermitLimit", "1000000");
```

- [ ] **Step 7: Run the tests**

```bash
dotnet test tests/Api.IntegrationTests
```

Expected: PASS, including `AuthRateLimitTests` and every pre-existing test.

- [ ] **Step 8: Regenerate the contract**

Adding `[ProducesResponseType(429)]` changes the OpenAPI document, so this time the regeneration is expected to produce a diff:

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
git status --porcelain openapi/ frontend/src/api/schema.d.ts
```

Expected: both files modified, showing 429 responses on the login and register operations and nothing else. Read the diff and confirm no other operation changed.

- [ ] **Step 9: Commit**

```bash
git add src/Api tests/Api.IntegrationTests openapi frontend/src/api/schema.d.ts
git commit -m "feat(auth): rate-limit the credential endpoints"
```

---

### Task 5: Prove the lockout over HTTP

**Files:**
- Test: `tests/Api.IntegrationTests/Auth/AuthEndpointTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-4. The class already has `AUsername()`, `ARegistration(username)`, `APassword` and `StripTraceIdAsync(response)`; reuse them rather than writing new helpers.
- Produces: nothing.

- [ ] **Step 1: Write the tests**

Add to `tests/Api.IntegrationTests/Auth/AuthEndpointTests.cs`, and add `using AiFramework.Domain.Users;` for `User.MaxFailedSignInAttempts`:

```csharp
    /// <summary>
    /// The proof the whole slice exists for: after the threshold, even the right password is
    /// refused. A test that only checked wrong passwords keep failing would pass without a
    /// lockout at all.
    /// </summary>
    [Fact]
    public async Task PostLogin_AfterTheThresholdOfWrongPasswords_RefusesEvenTheCorrectOne()
    {
        var username = AUsername();
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", ARegistration(username)))
            .EnsureSuccessStatusCode();

        for (var attempt = 0; attempt < User.MaxFailedSignInAttempts; attempt++)
        {
            var failed = await client.PostAsJsonAsync(
                "/api/auth/login",
                new { Username = username, Password = "not the password", RememberMe = false });
            failed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var withTheCorrectPassword = await client.PostAsJsonAsync(
            "/api/auth/login", new { Username = username, Password = APassword, RememberMe = false });

        withTheCorrectPassword.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "the lockout outranks a correct password, or it would not be a lockout");
    }

    [Fact]
    public async Task PostLogin_WhenLockedOut_IsIndistinguishableFromAWrongPassword()
    {
        var lockedOutUser = AUsername();
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", ARegistration(lockedOutUser)))
            .EnsureSuccessStatusCode();

        for (var attempt = 0; attempt < User.MaxFailedSignInAttempts; attempt++)
        {
            await client.PostAsJsonAsync(
                "/api/auth/login",
                new { Username = lockedOutUser, Password = "not the password", RememberMe = false });
        }

        var lockedOut = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = lockedOutUser, Password = "not the password", RememberMe = false });

        var otherUser = AUsername();
        (await client.PostAsJsonAsync("/api/auth/register", ARegistration(otherUser)))
            .EnsureSuccessStatusCode();
        var wrongPassword = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Username = otherUser, Password = "not the password", RememberMe = false });

        // A distinguishable lockout response would say "this account exists and I am guarding
        // it", undoing the uniform error and the dummy hash SignInHandler goes to trouble for.
        lockedOut.StatusCode.Should().Be(wrongPassword.StatusCode);
        (await StripTraceIdAsync(lockedOut))
            .Should().Be(await StripTraceIdAsync(wrongPassword), "traceId aside, the bodies must match");
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~AuthEndpointTests"`
Expected: PASS. Tasks 1-3 already implement the behaviour; these prove it end to end. If either fails, the fix belongs in Task 3's layer, not here.

- [ ] **Step 3: Full verification, both configurations**

```bash
dotnet build
dotnet build -c Release
dotnet test tests/Domain.Tests
dotnet test tests/Application.Tests
dotnet test tests/Infrastructure.Tests
dotnet test tests/Api.IntegrationTests
dotnet test tests/Api.IntegrationTests -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/hooks/tests/run-hook-tests.ps1
```

Expected: 0 warnings, all green, hook suite `Failed: 0`. Release matters on its own — this repo has shipped a Release-only startup break that Debug could not see (ADR 0005).

- [ ] **Step 4: Frontend and e2e**

```bash
npm run lint --prefix frontend && npm run build --prefix frontend && npm test --prefix frontend
```

Then, with no `dotnet run` dev API listening on 5234 (check with `powershell.exe -NoProfile -Command "Get-Process -Name AiFramework.Api -ErrorAction SilentlyContinue"`):

```bash
npm run e2e --prefix frontend
```

Expected: PASS. The e2e specs sign in once per run, far below the limit, and `schema.d.ts` gained only a response code. If e2e fails on a rate limit, the limit is too low for the suite and that is a real finding — report it rather than raising the limit to make it pass.

- [ ] **Step 5: Commit**

```bash
git add tests/Api.IntegrationTests/Auth/AuthEndpointTests.cs
git commit -m "test(auth): a locked account refuses the correct password, silently"
```

---

### Task 6: Record the decision

**Files:**
- Create: `docs/adr/0008-brute-force-protection.md`

**Interfaces:**
- Consumes: the shipped implementation.
- Produces: nothing.

- [ ] **Step 1: Write the ADR**

Follow the structure of `docs/adr/0007-orders-belong-to-the-user-who-placed-them.md` and `0006-username-and-password-authentication.md` — Context, Decision, Consequences, Alternatives considered — in prose, not bullets. Verify every factual claim against the code before writing it. It must cover:

- **Context:** ADR 0006 shipped sign-in and explicitly deferred brute-force protection, saying to revisit before the API faces the internet. It also spent real effort making responses refuse to answer "does this account exist?" — a uniform error and a dummy hash — which anything added here has to preserve.
- **Decision:** two layers, because a per-client limiter cannot see a thousand addresses guessing at one account and a per-account counter cannot see one address enumerating a thousand usernames. Five consecutive failures, fifteen minutes, fixed window, self-expiring, counter cleared on success. Serving out a lockout earns a fresh set of attempts — without that reset the window slides in through the back door. The lockout is silent, returning the same 401 as a wrong password, because a distinguishable response is an enumeration oracle; the honest 429 comes from the limiter, which counts clients and so distinguishes nothing. The locked-out branch still hashes, because otherwise timing answers what the body refuses to. Policy values are Domain constants; the limiter's numbers are configuration.
- **The write path, prominently:** `Behaviors.CommitAsync` does not commit a failed `Result`, so the counter cannot ride the unit of work on the failure path — the one that matters most. It is written with `ExecuteUpdateAsync` instead, making `SignIn` the first command in this codebase that persists outside the unit of work.
- **Consequences:** the limiter is in-memory and per-instance, so a second instance doubles the effective limit; behind a proxy `RemoteIpAddress` is the proxy unless `ForwardedHeaders` is configured; a user who forgets their password five times is locked out for fifteen minutes with no explanation and no reset flow, which is the accepted cost of silence and the strongest argument for password reset being the next slice; `ApiFactory` has to raise the limit so the suite does not exhaust it, and the limiter's own tests stand up a separate host.
- **Alternatives considered:** letting a command opt into committing on failure (weakens "one command, one transaction, only on success" for every command, to persist one counter); `Result<SignInOutcome>` carrying failure as success (inverts `Result`, and forces the controller to branch on domain state, which `src/Api/CLAUDE.md` forbids); partitioning the limiter by username (not reachable before model binding without buffering the body); a sliding window (lets an attacker hold an account locked indefinitely); escalating lockout durations (rejected as too punishing while there is no password reset).

Check whether `docs/adr/` keeps an index; if it does, add 0008.

- [ ] **Step 2: Commit**

```bash
git add docs/adr/0008-brute-force-protection.md
git commit -m "docs: ADR 0008, brute-force protection"
```

---

## Done when

- Five consecutive wrong passwords lock an account for fifteen minutes, and the correct password is refused during that window — proven over HTTP with a real session.
- A locked-out response is byte-identical to a wrong-password response apart from `traceId`.
- The credential endpoints answer 429 with `Retry-After` beyond the configured limit, proven on a host with a tiny limit.
- An attempt during a lockout does not extend it, and a failure after one expires starts a fresh count.
- `dotnet build` and every test project are green in Debug and Release with 0 warnings, and the hook suite reports `Failed: 0`.
- `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` are regenerated and committed, changed only by the new 429 responses.
- ADR 0008 is committed.
