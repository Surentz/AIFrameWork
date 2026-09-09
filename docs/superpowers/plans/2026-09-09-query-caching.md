# Query Caching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add opt-in, caller-scoped read-through caching to the query dispatch pipeline, with synchronous invalidation on write, demonstrated on the two order reads.

**Architecture:** Two marker interfaces in `Application` declare intent (`ICacheable`, `IInvalidatesCache`). `Infrastructure` owns the store and the mechanism: `Behaviors.CachedAsync` wraps every query handler and `Behaviors.EvictAsync` runs after a successful command commit, both mirroring the existing `ValidateAsync`/`CommitAsync` shape in `MessagingRegistration`. Cache keys are composed by the behavior from the query type plus `ICurrentUser.Id` — never by the query — because a key missing the user scope serves one user's orders to another.

**Tech Stack:** .NET 10 (`net10.0`), `Microsoft.Extensions.Caching.Hybrid` (L1 only), xUnit + FluentAssertions + NSubstitute, EF Core 10 / Npgsql, Testcontainers, Playwright.

**Spec:** `docs/superpowers/specs/2026-09-09-query-caching-design.md` — read it first. This plan argues from it; where they disagree, the spec wins.

## Global Constraints

- **Target framework `net10.0`.** .NET SDK 10.0.400.
- **Warnings are errors** — compiler, analyzers, and build, via `Directory.Build.props`. A warning is a build failure. Do not suppress without a `#pragma warning disable`/`restore` pair carrying a justification comment right above it explaining why the rule's intent does not apply.
- **Nullable is enabled.** A missing null check does not compile.
- **Never `catch (Exception)`.** CA1031 is an error everywhere except the outbox's file-scoped `.editorconfig` exemption. `throw;`, never `throw ex;`.
- **`required` keyword in `Domain`, never `[Required]`.** Not exercised by this plan; do not introduce DataAnnotations anywhere outside `Api` DTOs.
- **The dependency rule is hook-enforced** by `.claude/hooks/dependency-rule.ps1`, which blocks the edit rather than warning. `Application` may reference only `Domain`. **No caching package may be referenced from `Application`** — `ICacheable` is a plain interface.
- **No `Thread.Sleep` in tests.** `tests/CLAUDE.md:75` — "No `Thread.Sleep`. Inject an `IClock`." No test in this plan waits for a TTL to expire; none can, because `HybridCache` expires on its own internal clock.
- **Tests:** xUnit, FluentAssertions (`Should()`), NSubstitute (`Substitute.For<T>()`). Test names read as sentences: `Method_Condition_Outcome`.
- **No secrets in `appsettings*.json`.**
- **Verify with:** `dotnet build --nologo --verbosity quiet` then `dotnet test --nologo --verbosity quiet`.

---

### Task 1: Cache foundation — package, options, and the two pure helpers

Nothing in the request pipeline changes in this task. It lands the package, the configuration object, and the two small pure units that later tasks depend on, so a reviewer can check the arithmetic and the key format in isolation before any behavior uses them.

**Files:**
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj`
- Create: `src/Infrastructure/Caching/CacheOptions.cs`
- Create: `src/Infrastructure/Caching/CacheDuration.cs`
- Create: `src/Infrastructure/Caching/CachingRegistration.cs`
- Create: `src/Infrastructure/Messaging/CacheScope.cs`
- Modify: `src/Infrastructure/InfrastructureRegistration.cs` (add `AddCaching()` to `AddInfrastructure`)
- Test: `tests/Infrastructure.Tests/Caching/CacheDurationTests.cs`
- Test: `tests/Infrastructure.Tests/Caching/CacheScopeTests.cs`
- Test: `tests/Infrastructure.Tests/Caching/CachingRegistrationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public sealed class CacheOptions { public bool Enabled { get; set; } = true; public TimeSpan MaximumDuration { get; set; } = TimeSpan.FromMinutes(1); }` in namespace `AiFramework.Infrastructure.Caching`
  - `internal static TimeSpan CacheDuration.Clamp(TimeSpan requested, TimeSpan maximum)` in namespace `AiFramework.Infrastructure.Caching`
  - `internal static string CacheScope.Key(string queryName, Guid userId, string part)` and `internal static string CacheScope.Tag(string queryName, Guid userId)` in namespace `AiFramework.Infrastructure.Messaging`
  - `public static IServiceCollection AddCaching(this IServiceCollection services)` in namespace `AiFramework.Infrastructure.Caching`, class `CachingRegistration`

- [ ] **Step 1: Add the package and record its version**

`Microsoft.Extensions.Caching.Hybrid` is not in the local NuGet cache, so this step needs network access and pins whatever version resolves. Run:

```bash
dotnet add package Microsoft.Extensions.Caching.Hybrid --project src/Infrastructure
```

Then open `src/Infrastructure/AiFramework.Infrastructure.csproj` and confirm the added
`<PackageReference>` carries an explicit `Version` attribute. **Pin `10.9.0`.**

This package does **not** track the shared framework's patch line. Its published versions are
`9.3.0 … 9.10.0`, then `10.0.0 … 10.9.0` — an independent release counter where the minor digit
advances per release, so there is no `10.0.11` to match the other `Microsoft.Extensions.*`
references. `10.0.0` is merely its first .NET 10 release; `10.9.0` is the current one.

The API surface this plan depends on was verified against `10.9.0` by compile probe:
`AddHybridCache(o => o.MaximumPayloadBytes = …)`, the six-argument
`GetOrCreateAsync(key, state, factory, options, tags:, cancellationToken:)` taking a tuple state
and a `static async` factory that throws, `HybridCacheEntryOptions { Expiration = … }`, and
`RemoveByTagAsync(tag, ct)` returning `ValueTask` — all present, compiling clean. **If
`dotnet add package` resolves anything other than `10.9.0`, set the version to `10.9.0`
explicitly.** If `10.9.0` itself fails to build here, fall back to `10.0.0` and report it.

Move the new reference into alphabetical position among the existing `Microsoft.Extensions.*`
entries, and add this comment above it:

```xml
<!--
  HybridCache, for the query-caching behavior in Messaging/Behaviors.cs. Chosen over a bare
  IMemoryCache for three things that would otherwise be hand-rolled: stampede protection (ten
  concurrent misses collapse to one load), RemoveByTagAsync for per-caller invalidation, and an
  L2 tier that can be added later without touching a call site. L1 only today. See ADR 0009.
-->
<PackageReference Include="Microsoft.Extensions.Caching.Hybrid" Version="10.9.0" />
```

Adjust the `Version` to whatever `dotnet add package` actually pinned.

Also add an explicit reference for the options-binding package. It is already in this project's
resolved graph transitively at `10.0.11`, but this repository pins what it depends on rather than
relying on a transitive version — see the `Microsoft.EntityFrameworkCore.Relational` comment in
the same file for the failure that habit exists to prevent:

```xml
<!-- IOptions binding for CacheOptions (Caching/CachingRegistration.cs). Explicit rather
     than transitive, for the same reason Relational is pinned above. -->
<PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.11" />
```

- [ ] **Step 2: Measure the publish-size delta and record both numbers**

ADR 0009 (Task 5) records this, and the repository's convention is a measured number rather than
an assumption. Run both, in this order:

```bash
git stash push src/Infrastructure/AiFramework.Infrastructure.csproj
dotnet publish src/Api -c Release -o /tmp/pub-before --nologo
du -sm /tmp/pub-before
git stash pop
dotnet publish src/Api -c Release -o /tmp/pub-after --nologo
du -sm /tmp/pub-after
```

Write both numbers into this task's commit message body (Step 12) in the form
`Release publish: NNMB -> NNMB`. Task 5 reads them back with `git log`.

If `dotnet publish` fails for a reason unrelated to this change, say so and record
`Release publish: not measured (<reason>)` instead — do not invent a number.

- [ ] **Step 3: Write the failing tests for `CacheDuration.Clamp`**

Create `tests/Infrastructure.Tests/Caching/CacheDurationTests.cs`:

```csharp
using AiFramework.Infrastructure.Caching;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Caching;

/// <summary>
/// The clamp is a pure function precisely so it can be tested without waiting. HybridCache
/// expires entries on its own internal clock, which this repository's IClock cannot reach, and
/// tests/CLAUDE.md forbids Thread.Sleep — so the arithmetic is tested here and the library's
/// honouring of Expiration is taken as its own contract.
/// </summary>
public sealed class CacheDurationTests
{
    [Fact]
    public void Clamp_WhenRequestedIsBelowTheMaximum_ReturnsTheRequested()
    {
        var clamped = CacheDuration.Clamp(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

        clamped.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Clamp_WhenRequestedExceedsTheMaximum_ReturnsTheMaximum()
    {
        var clamped = CacheDuration.Clamp(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

        clamped.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Clamp_WhenRequestedEqualsTheMaximum_ReturnsTheMaximum()
    {
        var clamped = CacheDuration.Clamp(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        clamped.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Clamp_WithAZeroRequest_ReturnsZeroRatherThanTheMaximum()
    {
        var clamped = CacheDuration.Clamp(TimeSpan.Zero, TimeSpan.FromMinutes(1));

        clamped.Should().Be(
            TimeSpan.Zero,
            "MaximumDuration is a ceiling, not a default — it must never raise a duration");
    }
}
```

- [ ] **Step 4: Run them and verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet --filter FullyQualifiedName~CacheDurationTests`

Expected: FAIL to compile — `CacheDuration` does not exist. A compile failure is the correct
"red" here; do not proceed until you have seen it.

- [ ] **Step 5: Write `CacheDuration`**

Create `src/Infrastructure/Caching/CacheDuration.cs`:

```csharp
namespace AiFramework.Infrastructure.Caching;

internal static class CacheDuration
{
    /// <summary>
    /// Caps a query's requested duration at the configured ceiling. Extracted from the caching
    /// behavior so the only arithmetic in the TTL is directly testable: HybridCache expires on
    /// its own internal clock, so no test can observe an entry actually lapsing.
    /// </summary>
    internal static TimeSpan Clamp(TimeSpan requested, TimeSpan maximum) =>
        requested < maximum ? requested : maximum;
}
```

- [ ] **Step 6: Run them and verify they pass**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet --filter FullyQualifiedName~CacheDurationTests`

Expected: PASS, 4 tests.

- [ ] **Step 7: Write the failing tests for `CacheScope`**

Create `tests/Infrastructure.Tests/Caching/CacheScopeTests.cs`:

```csharp
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Caching;

/// <summary>
/// Key and Tag must agree, permanently. The read side writes an entry under Key and tags it with
/// Tag; the command side evicts by Tag. If the two ever stop sharing a prefix, eviction misses
/// every entry, silently, and every other test in the suite stays green.
/// </summary>
public sealed class CacheScopeTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Key_StartsWithTheTagForTheSameQueryAndUser()
    {
        var key = CacheScope.Key("GetOrders", Alice, "20:");
        var tag = CacheScope.Tag("GetOrders", Alice);

        key.Should().StartWith(
            tag, "RemoveByTagAsync can only reach an entry whose tag prefixes its key");
    }

    [Fact]
    public void Key_ForTwoDifferentUsers_Differs()
    {
        var alice = CacheScope.Key("GetOrders", Alice, "20:");
        var bob = CacheScope.Key("GetOrders", Bob, "20:");

        alice.Should().NotBe(
            bob, "a shared key across users is a data leak, not a staleness bug");
    }

    [Fact]
    public void Key_ForTwoDifferentQueryTypes_Differs()
    {
        var orders = CacheScope.Key("GetOrders", Alice, "1");
        var order = CacheScope.Key("GetOrder", Alice, "1");

        orders.Should().NotBe(order);
    }

    [Fact]
    public void Key_ForTwoDifferentArguments_Differs()
    {
        var first = CacheScope.Key("GetOrders", Alice, "20:");
        var second = CacheScope.Key("GetOrders", Alice, "50:");

        first.Should().NotBe(second);
    }

    [Fact]
    public void Tag_ForTwoDifferentUsers_Differs()
    {
        var alice = CacheScope.Tag("GetOrders", Alice);
        var bob = CacheScope.Tag("GetOrders", Bob);

        alice.Should().NotBe(
            bob, "evicting one caller's entries must not evict another's");
    }
}
```

- [ ] **Step 8: Run them and verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet --filter FullyQualifiedName~CacheScopeTests`

Expected: FAIL to compile — `CacheScope` does not exist.

- [ ] **Step 9: Write `CacheScope`**

Create `src/Infrastructure/Messaging/CacheScope.cs`:

```csharp
namespace AiFramework.Infrastructure.Messaging;

/// <summary>
/// The single source of cache key and tag composition. Both sides of the pipeline come through
/// here — the caching behavior writes an entry under <see cref="Key"/> and tags it with
/// <see cref="Tag"/>, and the eviction behavior removes by <see cref="Tag"/> — because the tag
/// being the key's own prefix is the whole mechanism. Composing either string anywhere else
/// invites a drift that no test would see: eviction would simply stop matching, with no error.
/// </summary>
internal static class CacheScope
{
    /// <summary>"GetOrders:3f2a...:20:" — the query type, the caller, then the query's own part.</summary>
    internal static string Key(string queryName, Guid userId, string part) =>
        $"{Tag(queryName, userId)}:{part}";

    /// <summary>"GetOrders:3f2a..." — everything one caller has cached for one query type.</summary>
    internal static string Tag(string queryName, Guid userId) =>
        $"{queryName}:{userId}";
}
```

- [ ] **Step 10: Run them and verify they pass**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet --filter FullyQualifiedName~CacheScopeTests`

Expected: PASS, 5 tests.

- [ ] **Step 11: Write the failing tests for `CacheOptions` and `AddCaching`**

Create `tests/Infrastructure.Tests/Caching/CachingRegistrationTests.cs`:

```csharp
using AiFramework.Infrastructure;
using AiFramework.Infrastructure.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Caching;

public sealed class CachingRegistrationTests
{
    // AddLogging is not decoration: HybridCache's default implementation takes an ILogger, so a
    // bare ServiceCollection cannot construct it. Every test host in this plan that resolves
    // HybridCache adds logging for that reason.
    private static ServiceProvider Build(Action<CacheOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddCaching_ByDefault_EnablesTheCache()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        options.Enabled.Should().BeTrue("the cache is on unless a host turns it off");
    }

    [Fact]
    public void AddCaching_ByDefault_CapsDurationAtOneMinute()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        options.MaximumDuration.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void AddCaching_ResolvesHybridCache()
    {
        using var provider = Build();

        var cache = provider.GetService<HybridCache>();

        cache.Should().NotBeNull(
            "CachedAsync resolves HybridCache with GetRequiredService; an absent registration " +
            "would surface as a failed request rather than a failed startup");
    }

    [Fact]
    public void AddCaching_WithANonPositiveMaximumDuration_FailsValidation()
    {
        using var provider = Build(o => o.MaximumDuration = TimeSpan.Zero);

        var act = () => provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*MaximumDuration*");
    }

    [Fact]
    public void AddCaching_WithAMaximumDurationAboveAnHour_FailsValidation()
    {
        using var provider = Build(o => o.MaximumDuration = TimeSpan.FromHours(2));

        var act = () => provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*MaximumDuration*");
    }

    [Fact]
    public void AddInfrastructure_WiresCachingIn()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Never connects: UseNpgsql does not touch the network at registration. Same placeholder
        // string RegistrationCompletenessTests uses, for the same reason.
        services.AddInfrastructure(
            "Host=localhost;Port=1;Database=unreachable;Username=none;Password=none");

        using var provider = services.BuildServiceProvider();

        provider.GetService<HybridCache>().Should().NotBeNull(
            "Api reaches Infrastructure only through AddInfrastructure, so caching has to be " +
            "wired in there rather than left for a host to remember");
    }
}
```

- [ ] **Step 12: Run them and verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet --filter FullyQualifiedName~CachingRegistrationTests`

Expected: FAIL to compile — `CacheOptions` and `AddCaching` do not exist.

- [ ] **Step 13: Write `CacheOptions` and `CachingRegistration`**

Create `src/Infrastructure/Caching/CacheOptions.cs`:

```csharp
namespace AiFramework.Infrastructure.Caching;

/// <summary>
/// The two things an operator needs to reach without a deploy. Per-query durations are NOT here:
/// they are literals on the query itself, because the query is what knows how stale its own
/// result may acceptably be, and a record in Application has nothing to inject configuration
/// through. Bound from the "Cache" configuration section in Program.cs.
/// </summary>
public sealed class CacheOptions
{
    /// <summary>The kill switch. False makes every cached query a straight handler call.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A ceiling every ICacheable.Duration is clamped to, so a careless literal in Application
    /// cannot retain a page for an hour. A cap, not a default — it never raises a duration.
    /// </summary>
    public TimeSpan MaximumDuration { get; set; } = TimeSpan.FromMinutes(1);
}
```

Create `src/Infrastructure/Caching/CachingRegistration.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Caching;

public static class CachingRegistration
{
    /// <summary>
    /// The cache store and its options. Called from AddInfrastructure, beside AddOutbox, so Api
    /// still reaches Infrastructure through exactly one entry point.
    /// </summary>
    /// <remarks>
    /// Deliberately does not bind configuration itself. Binding here would make
    /// IOptions&lt;CacheOptions&gt; depend on an IConfiguration being registered, which a bare
    /// ServiceCollection in a unit test does not have — and the caching behavior resolves those
    /// options on every query. Program.cs binds the section instead, the same way it already
    /// reads Wolverine:Durable and RateLimiting:Auth and passes them in explicitly.
    /// </remarks>
    public static IServiceCollection AddCaching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Validated for the same reason OutboxOptions is: a value that is merely wrong rather
        // than malformed would otherwise do something quietly useless instead of failing.
        services.AddOptions<CacheOptions>()
            .Validate(
                o => o.MaximumDuration > TimeSpan.Zero,
                "CacheOptions.MaximumDuration must be positive.")
            .Validate(
                o => o.MaximumDuration <= TimeSpan.FromHours(1),
                "CacheOptions.MaximumDuration above an hour is almost certainly a mistake.");

        // MaximumPayloadBytes bounds a single entry. The key space itself is unbounded in
        // principle — any cursor string is a distinct key — and bounded in practice by the
        // duration cap above. See ADR 0009's accepted risks.
        services.AddHybridCache(o => o.MaximumPayloadBytes = 1024 * 1024);

        return services;
    }
}
```

- [ ] **Step 14: Call `AddCaching()` from `AddInfrastructure`**

In `src/Infrastructure/InfrastructureRegistration.cs`, add `using AiFramework.Infrastructure.Caching;`
to the using block, then insert the call immediately before `services.AddOutbox();`:

```csharp
        services.AddCaching();

        services.AddOutbox();
```

- [ ] **Step 15: Run the full backend suite**

Run: `dotnet build --nologo --verbosity quiet` then `dotnet test --nologo --verbosity quiet`

Expected: build clean (warnings are errors), all tests pass. Nothing in the pipeline has changed
yet, so no existing test should move.

- [ ] **Step 16: Commit**

```bash
git add src/Infrastructure/Caching src/Infrastructure/Messaging/CacheScope.cs \
        src/Infrastructure/InfrastructureRegistration.cs \
        src/Infrastructure/AiFramework.Infrastructure.csproj \
        tests/Infrastructure.Tests/Caching
git commit -m "feat(cache): add HybridCache, CacheOptions, and the key and clamp helpers

CacheScope is the single source of key and tag composition: the tag is the
key's own prefix, which is what makes RemoveByTagAsync able to reach an entry
at all. Composing either string anywhere else would drift silently.

CacheDuration.Clamp is extracted as a pure function because HybridCache expires
on its own internal clock, so no test can watch a TTL lapse without the
Thread.Sleep tests/CLAUDE.md forbids.

Nothing in the request pipeline uses any of this yet.

Release publish: <BEFORE>MB -> <AFTER>MB

Co-Authored-By: Claude <noreply@anthropic.com>"
```

Replace `<BEFORE>` and `<AFTER>` with the numbers measured in Step 2.

---

### Task 2: Read-through caching in the query pipeline

**Files:**
- Create: `src/Application/Abstractions/Caching.cs`
- Modify: `src/Infrastructure/Messaging/Behaviors.cs`
- Modify: `src/Infrastructure/Messaging/MessagingRegistration.cs` (the `AddQuery` local function)
- Modify: `tests/Infrastructure.Tests/Messaging/QueryDispatcherTests.cs`
- Test: `tests/Infrastructure.Tests/Messaging/QueryCachingBehaviorTests.cs`

**Interfaces:**
- Consumes: `CacheScope.Key`, `CacheScope.Tag`, `CacheDuration.Clamp`, `CacheOptions`, `AddCaching()` from Task 1.
- Produces:
  - `public interface ICacheable { public string CacheKey { get; } public TimeSpan Duration { get; } }` in namespace `AiFramework.Application.Abstractions`
  - `internal static Task<Result<TResponse>> Behaviors.Handle<TQuery, TResponse>(IServiceProvider sp, TQuery query, CancellationToken ct) where TQuery : IQuery<TResponse>`
  - `internal static Task<Result<TResponse>> Behaviors.CachedAsync<TQuery, TResponse>(IServiceProvider sp, TQuery query, CancellationToken ct) where TQuery : IQuery<TResponse>`

- [ ] **Step 1: Write the `ICacheable` contract**

This is a contract with no behavior of its own, so it has no test of its own — Step 3's tests are
what fail without it. Create `src/Application/Abstractions/Caching.cs`:

```csharp
namespace AiFramework.Application.Abstractions;

/// <summary>
/// A query whose successful result may be cached. Opt-in: a query without this interface is
/// never cached, which is why nothing on the auth path has it.
/// </summary>
/// <remarks>
/// <see cref="CacheKey"/> is ONLY the part that varies with this query's own arguments. The
/// caching behavior prepends the query type name and the calling user's id, so a query cannot
/// omit the user scope — a key shared across users would serve one caller another's data, which
/// is a leak rather than a stale read. Do not put a user id in CacheKey; it is already there.
/// </remarks>
public interface ICacheable
{
    public string CacheKey { get; }

    /// <summary>
    /// How long a successful result may be served from cache. Clamped to
    /// CacheOptions.MaximumDuration, so this can only ever be shortened by configuration.
    /// </summary>
    public TimeSpan Duration { get; }
}
```

- [ ] **Step 2: Confirm the dependency-rule hook allowed it**

The hook at `.claude/hooks/dependency-rule.ps1` blocks the edit rather than warning. The file
above references nothing outside `Application`, so it must have been written. Confirm:

```bash
cat src/Application/Abstractions/Caching.cs
```

Expected: the file exists with the content from Step 1. If the write was blocked, you have added
a using directive that reaches outward — remove it. `ICacheable` must not reference any caching
package.

- [ ] **Step 3: Write the failing tests for the caching behavior**

Create `tests/Infrastructure.Tests/Messaging/QueryCachingBehaviorTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Caching;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.Messaging;

/// <summary>A cacheable query, and a handler that counts how often it actually ran.</summary>
public sealed record Lookup(int Value) : IQuery<string>, ICacheable
{
    public string CacheKey => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public TimeSpan Duration => TimeSpan.FromSeconds(30);
}

public sealed class CountingLookupHandler : IQueryHandler<Lookup, string>
{
    // Static because the handler is registered scoped: each dispatch resolves a new instance,
    // so an instance field could not tell one call from two.
    public static int Calls;

    public Task<Result<string>> HandleAsync(Lookup query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Interlocked.Increment(ref Calls);
        return Task.FromResult(Result.Success($"value-{query.Value}"));
    }
}

public sealed class FailingLookupHandler : IQueryHandler<Lookup, string>
{
    public static int Calls;

    public Task<Result<string>> HandleAsync(Lookup query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Interlocked.Increment(ref Calls);
        return Task.FromResult(Result.Failure<string>(
            new Error(ErrorKind.NotFound, "lookup.not_found", "no such value")));
    }
}

/// <summary>An uncacheable query, to prove opt-in really is opt-in.</summary>
public sealed record Plain(int Value) : IQuery<string>;

public sealed class PlainHandler : IQueryHandler<Plain, string>
{
    public static int Calls;

    public Task<Result<string>> HandleAsync(Plain query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Interlocked.Increment(ref Calls);
        return Task.FromResult(Result.Success("plain"));
    }
}

public sealed class QueryCachingBehaviorTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // A real HybridCache, not a substitute: the thing under test is whether the key and tag
    // composition actually hits and misses, which a mock would simply agree with.
    private static ServiceProvider Build<THandler>(ICurrentUser currentUser, bool enabled = true)
        where THandler : class, IQueryHandler<Lookup, string>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        services.Configure<CacheOptions>(o => o.Enabled = enabled);
        services.AddQuery<Lookup, string, THandler>();
        services.AddQuery<Plain, string, PlainHandler>();
        services.AddSingleton<QueryRegistry>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
        services.AddSingleton(currentUser);

        return services.BuildServiceProvider();
    }

    private static ICurrentUser UserOf(Guid? id)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(id);
        return currentUser;
    }

    [Fact]
    public async Task SendAsync_TwiceForTheSameUserAndArguments_RunsTheHandlerOnce()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(Alice));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var first = await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);
        var second = await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);

        first.Value.Should().Be("value-1");
        second.Value.Should().Be("value-1");
        CountingLookupHandler.Calls.Should().Be(1, "the second dispatch must be served from cache");
    }

    [Fact]
    public async Task SendAsync_ForASecondUser_DoesNotServeTheFirstUsersValue()
    {
        CountingLookupHandler.Calls = 0;
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(Alice, Bob);
        await using var provider = Build<CountingLookupHandler>(currentUser);
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);
        await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(
            2,
            "the same CacheKey for a different caller is a different entry; sharing it would " +
            "serve one user another user's data");
    }

    [Fact]
    public async Task SendAsync_WithDifferentArguments_RunsTheHandlerForEach()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(Alice));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);
        await dispatcher.SendAsync(new Lookup(2), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task SendAsync_ForACacheableQueryWithNoCurrentUser_Throws()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(null));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var act = async () => await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Lookup*")
            .WithMessage("*current user*");
    }

    [Fact]
    public async Task SendAsync_WhenTheHandlerFails_ReturnsTheFailureAndCachesNothing()
    {
        FailingLookupHandler.Calls = 0;
        await using var provider = Build<FailingLookupHandler>(UserOf(Alice));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var first = await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);
        var second = await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);

        first.IsSuccess.Should().BeFalse();
        first.Error.Kind.Should().Be(ErrorKind.NotFound);
        first.Error.Code.Should().Be("lookup.not_found");
        second.IsSuccess.Should().BeFalse();
        FailingLookupHandler.Calls.Should().Be(
            2, "a 404 must not be retained for the query's full duration");
    }

    [Fact]
    public async Task SendAsync_WhenCachingIsDisabled_RunsTheHandlerEveryTime()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(Alice), enabled: false);
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);
        await dispatcher.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(
            2, "the integration and e2e suites depend on this switch actually switching");
    }

    [Fact]
    public async Task SendAsync_ForAQueryWithoutICacheable_RunsTheHandlerEveryTime()
    {
        PlainHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(Alice));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        await dispatcher.SendAsync(new Plain(1), CancellationToken.None);
        await dispatcher.SendAsync(new Plain(1), CancellationToken.None);

        PlainHandler.Calls.Should().Be(2, "caching is opt-in, not the default");
    }

    [Fact]
    public async Task SendAsync_ForAQueryWithoutICacheableAndNoCurrentUser_DoesNotThrow()
    {
        PlainHandler.Calls = 0;
        await using var provider = Build<CountingLookupHandler>(UserOf(null));
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var result = await dispatcher.SendAsync(new Plain(1), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "the current-user requirement belongs to caching, not to every query — the outbox " +
            "pumps resolve scopes with no HTTP context at all");
    }
}
```

- [ ] **Step 4: Run them and verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet --filter FullyQualifiedName~QueryCachingBehaviorTests`

Expected: FAIL to compile — `ICacheable` exists but nothing consumes it, so `Lookup` compiles
while the behavior does not cache. Precisely: the tests compile and
`SendAsync_TwiceForTheSameUserAndArguments_RunsTheHandlerOnce` fails with
`Expected Calls to be 1, but found 2`. Confirm you see that assertion failure, not a compile
error — if it does not compile, the cause is a missing `using` in the test file.

- [ ] **Step 5: Add `Handle`, `CachedAsync`, and the sentinel to `Behaviors`**

In `src/Infrastructure/Messaging/Behaviors.cs`, add these using directives:

```csharp
using AiFramework.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
```

Then add these members to the existing `internal static class Behaviors`:

```csharp
    /// <summary>
    /// Resolves a query's handler and runs it. Extracted so the cached and uncached paths share
    /// one definition of "run the handler" — AddQuery no longer resolves it directly.
    /// </summary>
    internal static async Task<Result<TResponse>> Handle<TQuery, TResponse>(
        IServiceProvider sp, TQuery query, CancellationToken ct)
        where TQuery : IQuery<TResponse>
    {
        var handler = sp.GetRequiredService<IQueryHandler<TQuery, TResponse>>();
        return await handler.HandleAsync(query, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves a query from cache when it opts in with <see cref="ICacheable"/>, and otherwise
    /// runs the handler directly. Caches the success VALUE, not the Result: Result&lt;T&gt;.Error
    /// throws when read on a success, so serializing a successful Result fails, and its internal
    /// constructor makes deserializing one impossible.
    /// </summary>
    internal static async Task<Result<TResponse>> CachedAsync<TQuery, TResponse>(
        IServiceProvider sp, TQuery query, CancellationToken ct)
        where TQuery : IQuery<TResponse>
    {
        // Not absence-tolerant, deliberately, unlike the validator above: an unregistered cache
        // would leave every query silently uncached, the same class of quiet wrongness
        // CommitAsync refuses to allow for a missing IUnitOfWork. AddInfrastructure wires
        // AddCaching in, and CachingRegistrationTests asserts it.
        var options = sp.GetRequiredService<IOptions<CacheOptions>>().Value;

        if (!options.Enabled || query is not ICacheable cacheable)
        {
            return await Handle<TQuery, TResponse>(sp, query, ct).ConfigureAwait(false);
        }

        // A cached query with no caller would share one entry across every user. That is a data
        // leak, so it fails loudly instead: a cacheable query is reachable only from an
        // [Authorize]d endpoint, and its absence means the wiring is wrong.
        var userId = sp.GetRequiredService<ICurrentUser>().Id
            ?? throw new InvalidOperationException(
                $"'{typeof(TQuery).Name}' is ICacheable but there is no current user to scope " +
                "its key to. Cached queries must be reachable only from an authorized endpoint.");

        var cache = sp.GetRequiredService<HybridCache>();
        var name = typeof(TQuery).Name;

        try
        {
            var value = await cache.GetOrCreateAsync(
                CacheScope.Key(name, userId, cacheable.CacheKey),
                (sp, query),
                static async (state, token) =>
                {
                    var result = await Handle<TQuery, TResponse>(state.sp, state.query, token)
                        .ConfigureAwait(false);

                    // The sentinel is what keeps failures out of the cache: HybridCache stores
                    // nothing when its factory throws. The alternative — caching a wrapper and
                    // removing it afterwards — leaves a window in which a concurrent caller
                    // reads the cached failure.
                    return result.IsSuccess
                        ? result.Value
                        : throw new QueryFailed(result.Error);
                },
                new HybridCacheEntryOptions
                {
                    Expiration = CacheDuration.Clamp(cacheable.Duration, options.MaximumDuration),
                },
                tags: [CacheScope.Tag(name, userId)],
                cancellationToken: ct).ConfigureAwait(false);

            return Result.Success(value);
        }
        catch (QueryFailed failed)
        {
            return Result.Failure<TResponse>(failed.Error);
        }
    }

    // CA1032 wants the standard exception constructor set; S3871 wants exception types public.
    // Both are asking to widen a type whose entire purpose is to stay inside one method: it is
    // thrown by the cache factory a dozen lines above and caught by name immediately after,
    // never crosses this class's boundary, and no caller can construct or catch it. Note this is
    // a specific catch, not catch (Exception) — CA1031 is satisfied on its own terms.
#pragma warning disable CA1032, S3871
    private sealed class QueryFailed(Error error) : Exception
    {
        public Error Error { get; } = error;
    }
#pragma warning restore CA1032, S3871
```

- [ ] **Step 6: Route `AddQuery` through the behavior**

In `src/Infrastructure/Messaging/MessagingRegistration.cs`, replace the body of `AddQuery`'s
local function. It currently reads:

```csharp
        static async Task<object?> InvokeAsync(
            IServiceProvider sp, object query, CancellationToken ct)
        {
            var handler = sp.GetRequiredService<IQueryHandler<TQuery, TResponse>>();
            return await handler.HandleAsync((TQuery)query, ct).ConfigureAwait(false);
        }
```

Replace with:

```csharp
        static async Task<object?> InvokeAsync(
            IServiceProvider sp, object query, CancellationToken ct) =>
            await Behaviors.CachedAsync<TQuery, TResponse>(sp, (TQuery)query, ct)
                .ConfigureAwait(false);
```

Also update this method's doc comment, which currently claims no behaviors run on the query
path. Replace its `<summary>` with:

```csharp
    /// <summary>
    /// Registers a query, its handler, and a dispatch delegate that closes over TQuery and
    /// TResponse at compile time. Same reflection-free mechanism as <see cref="AddCommand"/>,
    /// via <see cref="QueryDispatcher"/>. Unlike the command path this runs no validation —
    /// query handlers validate their own inputs — but it does run the caching behavior, which
    /// is a no-op for a query that has not opted in with ICacheable.
    /// </summary>
```

The local function stays `static`, so the no-closure property the file documents is preserved.

- [ ] **Step 7: Fix `QueryDispatcherTests`, which builds bare service collections**

`CachedAsync` resolves `IOptions<CacheOptions>` with `GetRequiredService`, and the three tests in
`tests/Infrastructure.Tests/Messaging/QueryDispatcherTests.cs` build a `ServiceCollection` with
neither options nor logging registered. Two of them will now throw.

Add these using directives to that file:

```csharp
using AiFramework.Infrastructure.Caching;
```

Then add this private helper to the `QueryDispatcherTests` class, above the first `[Fact]`:

```csharp
    // AddCaching and AddLogging are what CachedAsync needs to resolve: it reads
    // IOptions<CacheOptions> on every query, and HybridCache's default implementation takes an
    // ILogger. Echo is not ICacheable, so nothing here is actually cached — these registrations
    // exist so the pipeline can run at all.
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        services.AddSingleton<QueryRegistry>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
        return services;
    }
```

Then rewrite the three tests to use it, replacing their four-line setup blocks:

```csharp
    [Fact]
    public async Task SendAsync_WithARegisteredQuery_InvokesItsHandler()
    {
        var services = Services();
        services.AddQuery<Echo, int, EchoHandler>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var result = await dispatcher.SendAsync(new Echo(21), CancellationToken.None);

        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task SendAsync_WithAnUnregisteredQuery_Throws()
    {
        var services = Services();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IQueryDispatcher>();

        var act = async () => await dispatcher.SendAsync(new Echo(1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Echo*");
    }

    [Fact]
    public void Construction_WithAQueryRegisteredTwice_ThrowsNamingTheQuery()
    {
        var services = Services();
        services.AddQuery<Echo, int, EchoHandler>();
        services.AddQuery<Echo, int, EchoHandler>();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IQueryDispatcher>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Echo*");
    }
```

- [ ] **Step 8: Run the caching behavior tests**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet --filter FullyQualifiedName~QueryCachingBehaviorTests`

Expected: PASS, 8 tests.

- [ ] **Step 9: Run the full backend suite**

Run: `dotnet build --nologo --verbosity quiet` then `dotnet test --nologo --verbosity quiet`

Expected: build clean, all tests pass. `QueryDispatcherTests` in particular must be green —
if it is not, Step 7 is incomplete.

- [ ] **Step 10: Commit**

```bash
git add src/Application/Abstractions/Caching.cs \
        src/Infrastructure/Messaging/Behaviors.cs \
        src/Infrastructure/Messaging/MessagingRegistration.cs \
        tests/Infrastructure.Tests/Messaging
git commit -m "feat(cache): serve opted-in queries from a caller-scoped cache

The query path gains the pipeline the command path already had. A query opts in
with ICacheable and supplies only the part of the key that varies with its own
arguments; the behavior prepends the query type and ICurrentUser.Id, so a query
cannot omit the user scope and leak another caller's data. A cacheable query
dispatched with no caller throws rather than sharing one entry.

Caches TResponse, not Result<T>: Result<T>.Error throws when read on a success,
so a successful Result cannot be serialized, and its internal constructor makes
deserialization impossible.

Failures are kept out by a private sentinel exception — HybridCache stores
nothing when its factory throws, which avoids the window a cache-then-remove
approach leaves open.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: Synchronous eviction in the command pipeline

**Files:**
- Modify: `src/Application/Abstractions/Caching.cs` (add `IInvalidatesCache`)
- Modify: `src/Infrastructure/Messaging/Behaviors.cs` (add `EvictAsync`)
- Modify: `src/Infrastructure/Messaging/MessagingRegistration.cs` (the `AddCommand` local function)
- Test: `tests/Infrastructure.Tests/Messaging/CacheEvictionBehaviorTests.cs`

**Interfaces:**
- Consumes: `ICacheable`, `Behaviors.CachedAsync`, `CacheScope.Tag`, `CacheOptions` from Tasks 1–2.
- **Also consumes two test types from Task 2's file** — `public sealed record Lookup(int Value) : IQuery<string>, ICacheable` and `public sealed class CountingLookupHandler : IQueryHandler<Lookup, string>` with its `public static int Calls`, both in `tests/Infrastructure.Tests/Messaging/QueryCachingBehaviorTests.cs`, namespace `AiFramework.Infrastructure.Tests.Messaging`. Task 3's test file is in the same namespace and uses them directly. **Do not redefine them** — a second declaration of either is a compile error, and the point of reusing them is that eviction is proven against the same read path the caching tests use.
- Produces:
  - `public interface IInvalidatesCache { public IReadOnlyList<string> Tags { get; } }` in namespace `AiFramework.Application.Abstractions`
  - `internal static Task Behaviors.EvictAsync<TCommand, TResponse>(IServiceProvider sp, TCommand command, Result<TResponse> result, CancellationToken ct)`

- [ ] **Step 1: Add the `IInvalidatesCache` contract**

Append to `src/Application/Abstractions/Caching.cs`:

```csharp
/// <summary>
/// A command that invalidates cached queries belonging to the caller who issued it. Eviction
/// runs synchronously, after the command's transaction commits and before the response returns.
/// </summary>
/// <remarks>
/// Tags name query TYPES — use <c>nameof(GetOrders)</c>, not a hand-written string. The calling
/// user's id is prepended by the behavior, exactly as it is for keys, so one caller's write never
/// evicts another's entries.
/// <para>
/// Synchronous rather than riding the domain-event path, and read-your-own-writes is the reason:
/// the frontend refetches milliseconds after a 201, and must not be served a page that omits
/// what it just created. The outbox is polled, so an event-driven eviction would land too late.
/// </para>
/// </remarks>
public interface IInvalidatesCache
{
    public IReadOnlyList<string> Tags { get; }
}
```

- [ ] **Step 2: Write the failing tests for eviction**

Create `tests/Infrastructure.Tests/Messaging/CacheEvictionBehaviorTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Caching;
using AiFramework.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.Messaging;

public sealed record Touch(int Value) : ICommand<bool>, IInvalidatesCache
{
    public IReadOnlyList<string> Tags => [nameof(Lookup)];
}

public sealed class TouchHandler : ICommandHandler<Touch, bool>
{
    public Task<Result<bool>> HandleAsync(Touch command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(Result.Success(true));
    }
}

public sealed class FailingTouchHandler : ICommandHandler<Touch, bool>
{
    public Task<Result<bool>> HandleAsync(Touch command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(Result.Failure<bool>(
            new Error(ErrorKind.Conflict, "touch.conflict", "nope")));
    }
}

/// <summary>A command that invalidates nothing, to prove eviction is opt-in too.</summary>
public sealed record Quiet(int Value) : ICommand<bool>;

public sealed class QuietHandler : ICommandHandler<Quiet, bool>
{
    public Task<Result<bool>> HandleAsync(Quiet command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult(Result.Success(true));
    }
}

/// <summary>
/// Eviction is tested against the real cache and through the real read path: these tests write an
/// entry by dispatching Lookup, then dispatch Touch, then dispatch Lookup again and count
/// handler calls. Asserting on a mocked RemoveByTagAsync would only prove the behavior calls a
/// method — not that the tag it passes matches the key the query wrote, which is the one thing
/// that can silently drift.
/// </summary>
public sealed class CacheEvictionBehaviorTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ServiceProvider Build<TCommandHandler>(
        ICurrentUser currentUser, bool enabled = true)
        where TCommandHandler : class, ICommandHandler<Touch, bool>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        services.Configure<CacheOptions>(o => o.Enabled = enabled);
        services.AddQuery<Lookup, string, CountingLookupHandler>();
        services.AddCommand<Touch, bool, TCommandHandler>();
        services.AddCommand<Quiet, bool, QuietHandler>();
        services.AddSingleton<QueryRegistry>();
        services.AddSingleton<CommandRegistry>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddSingleton(currentUser);
        services.AddSingleton(Substitute.For<IUnitOfWork>());

        return services.BuildServiceProvider();
    }

    private static ICurrentUser UserOf(Guid? id)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(id);
        return currentUser;
    }

    [Fact]
    public async Task SendAsync_AfterASuccessfulInvalidatingCommand_TheNextReadIsAMiss()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<TouchHandler>(UserOf(Alice));
        var queries = provider.GetRequiredService<IQueryDispatcher>();
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        await queries.SendAsync(new Lookup(1), CancellationToken.None);
        await commands.SendAsync(new Touch(1), CancellationToken.None);
        await queries.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(
            2,
            "the tag the command evicts by has to match the key the query wrote; if CacheScope's " +
            "two halves ever drift, this is the only test that notices");
    }

    [Fact]
    public async Task SendAsync_WhenTheCommandFails_DoesNotEvict()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<FailingTouchHandler>(UserOf(Alice));
        var queries = provider.GetRequiredService<IQueryDispatcher>();
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        await queries.SendAsync(new Lookup(1), CancellationToken.None);
        await commands.SendAsync(new Touch(1), CancellationToken.None);
        await queries.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(
            1, "nothing changed, so there is nothing to invalidate");
    }

    [Fact]
    public async Task SendAsync_ForACommandWithoutIInvalidatesCache_DoesNotEvict()
    {
        CountingLookupHandler.Calls = 0;
        await using var provider = Build<TouchHandler>(UserOf(Alice));
        var queries = provider.GetRequiredService<IQueryDispatcher>();
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        await queries.SendAsync(new Lookup(1), CancellationToken.None);
        await commands.SendAsync(new Quiet(1), CancellationToken.None);
        await queries.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(1, "eviction is opt-in, like caching");
    }

    [Fact]
    public async Task SendAsync_WhenCachingIsDisabled_DoesNotThrowOnAnInvalidatingCommand()
    {
        await using var provider = Build<TouchHandler>(UserOf(Alice), enabled: false);
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        var result = await commands.SendAsync(new Touch(1), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "the kill switch has to leave writes working, or the integration suites cannot run");
    }

    [Fact]
    public async Task SendAsync_ForAnInvalidatingCommandWithNoCurrentUser_Throws()
    {
        await using var provider = Build<TouchHandler>(UserOf(null));
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        var act = async () => await commands.SendAsync(new Touch(1), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Touch*")
            .WithMessage("*current user*");
    }

    [Fact]
    public async Task SendAsync_OneCallersWrite_DoesNotEvictAnotherCallersEntries()
    {
        CountingLookupHandler.Calls = 0;
        var currentUser = Substitute.For<ICurrentUser>();

        // Alice reads, Bob writes, Alice reads again.
        currentUser.Id.Returns(Alice, Bob, Alice);
        await using var provider = Build<TouchHandler>(currentUser);
        var queries = provider.GetRequiredService<IQueryDispatcher>();
        var commands = provider.GetRequiredService<ICommandDispatcher>();

        await queries.SendAsync(new Lookup(1), CancellationToken.None);
        await commands.SendAsync(new Touch(1), CancellationToken.None);
        await queries.SendAsync(new Lookup(1), CancellationToken.None);

        CountingLookupHandler.Calls.Should().Be(
            1, "Bob's write must not invalidate Alice's cached page");
    }
}
```

- [ ] **Step 3: Run them and verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet --filter FullyQualifiedName~CacheEvictionBehaviorTests`

Expected: FAIL. `SendAsync_AfterASuccessfulInvalidatingCommand_TheNextReadIsAMiss` fails with
`Expected Calls to be 2, but found 1` — nothing evicts yet — and
`SendAsync_ForAnInvalidatingCommandWithNoCurrentUser_Throws` fails because no exception is
thrown. The other four pass vacuously; that is expected.

- [ ] **Step 4: Add `EvictAsync` to `Behaviors`**

Add to `internal static class Behaviors` in `src/Infrastructure/Messaging/Behaviors.cs`:

```csharp
    /// <summary>
    /// Removes the caller's cached entries for the query types a command declares, after the
    /// command has committed. Runs in-request, before the response returns, so a client that
    /// refetches immediately after a 201 reads its own write.
    /// </summary>
    /// <remarks>
    /// A failure here is deliberately not caught. With an L1-only HybridCache
    /// RemoveByTagAsync has no realistic failure mode, and catching one would mean
    /// catch (Exception) on a path with no IExceptionHandler parameter, which this repository
    /// bans outside the outbox's exemption. The consequence — a committed write surfacing a 500
    /// — becomes the wrong trade if an L2 tier is ever added. ADR 0009 records that.
    /// </remarks>
    internal static async Task EvictAsync<TCommand, TResponse>(
        IServiceProvider sp, TCommand command, Result<TResponse> result, CancellationToken ct)
    {
        if (!result.IsSuccess || command is not IInvalidatesCache invalidates)
        {
            return;
        }

        if (!sp.GetRequiredService<IOptions<CacheOptions>>().Value.Enabled)
        {
            return;
        }

        var userId = sp.GetRequiredService<ICurrentUser>().Id
            ?? throw new InvalidOperationException(
                $"'{typeof(TCommand).Name}' invalidates cache tags but there is no current user " +
                "to scope them to.");

        var cache = sp.GetRequiredService<HybridCache>();

        foreach (var tag in invalidates.Tags)
        {
            await cache.RemoveByTagAsync(CacheScope.Tag(tag, userId), ct).ConfigureAwait(false);
        }
    }
```

- [ ] **Step 5: Call it from `AddCommand`**

In `src/Infrastructure/Messaging/MessagingRegistration.cs`, inside `AddCommand`'s `InvokeAsync`,
add one line after the existing commit:

```csharp
            await Behaviors.CommitAsync(sp, result, ct).ConfigureAwait(false);
            await Behaviors.EvictAsync(sp, typed, result, ct).ConfigureAwait(false);

            return result;
```

Order matters and is not incidental: eviction must follow the commit, or a concurrent read could
repopulate the cache from the pre-commit state.

- [ ] **Step 6: Run the eviction tests**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet --filter FullyQualifiedName~CacheEvictionBehaviorTests`

Expected: PASS, 6 tests.

- [ ] **Step 7: Run the full backend suite**

Run: `dotnet build --nologo --verbosity quiet` then `dotnet test --nologo --verbosity quiet`

Expected: build clean, all tests pass. `CommandDispatcherTests` and `BehaviorTests` build bare
collections too — but `Ping` and `Save` are not `IInvalidatesCache`, so `EvictAsync` returns
before resolving options. If either fails, report it rather than adding registrations blindly.

- [ ] **Step 8: Commit**

```bash
git add src/Application/Abstractions/Caching.cs \
        src/Infrastructure/Messaging/Behaviors.cs \
        src/Infrastructure/Messaging/MessagingRegistration.cs \
        tests/Infrastructure.Tests/Messaging/CacheEvictionBehaviorTests.cs
git commit -m "feat(cache): evict the caller's entries when a command commits

A command declares the query types it invalidates; the behavior prepends the
caller's id, so one user's write never touches another's entries. Runs after
CommitAsync and before the response, which is what preserves read-your-own-
writes for a client that refetches straight after a 201.

The eviction tests go through the real read path rather than asserting on a
mocked RemoveByTagAsync: the failure worth catching is the tag drifting from
the key it is meant to match, and a mock would agree with either.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: Opt the order reads in, and wire the configuration

This is where caching becomes real in production. It ends with an over-HTTP proof of the
read-your-own-writes guarantee.

**Files:**
- Modify: `src/Application/Orders/GetOrders.cs`
- Modify: `src/Application/Orders/GetOrder.cs`
- Modify: `src/Application/Orders/PlaceOrder.cs`
- Modify: `src/Api/Program.cs`
- Modify: `src/Api/appsettings.json`
- Modify: `tests/Api.IntegrationTests/ApiFactory.cs`
- Modify: `frontend/playwright.config.ts`
- Test: `tests/Api.IntegrationTests/Orders/OrderCachingTests.cs`

**Interfaces:**
- Consumes: `ICacheable`, `IInvalidatesCache`, `CacheOptions`, and both behaviors from Tasks 1–3.
- Produces: no new types. `GetOrders`, `GetOrder`, and `PlaceOrder` gain interface implementations only.

- [ ] **Step 1: Turn the cache off for the existing integration suite, first**

Do this before opting anything in, so the existing suite never runs against a cache it was not
written for. In `tests/Api.IntegrationTests/ApiFactory.cs`, in `ConfigureWebHost`, add after the
existing `RateLimiting:Auth:PermitLimit` line:

```csharp
        // Off for every test in this project, for the same reason the rate limit is raised above:
        // these tests were written against uncached reads, and a hit would make an unrelated
        // assertion fail as though the endpoint were broken. The cache's own behaviour is covered
        // by Orders/OrderCachingTests, which stands up its own host with it switched on — the
        // same split AuthRateLimitTests uses for the limiter.
        builder.UseSetting("Cache:Enabled", "false");
```

- [ ] **Step 2: Bind the configuration section in `Program.cs`**

`AddCaching()` deliberately does not bind configuration (see its remarks). Add the binding in
`src/Api/Program.cs`, immediately after the existing `builder.Services.AddInfrastructure(connectionString);`:

```csharp
// Bound here rather than inside AddCaching, which must stay resolvable from a bare
// ServiceCollection in unit tests. Same shape as Wolverine:Durable and RateLimiting:Auth above:
// Api reads its own configuration and hands the values to Infrastructure.
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));
```

Add the using directive, keeping the block alphabetical:

```csharp
using AiFramework.Infrastructure.Caching;
```

This is a DI registration, which is the only thing the `Api → Infrastructure` cell of the
dependency table permits.

- [ ] **Step 3: Add the configuration section**

In `src/Api/appsettings.json`, add a `Cache` section after `ConnectionStrings`:

```json
  "ConnectionStrings": {
    "Default": ""
  },
  "Cache": {
    "Enabled": true,
    "MaximumDuration": "00:01:00"
  }
```

No secret is involved, so the no-secrets hook has nothing to object to.

- [ ] **Step 4: Turn the cache off for the e2e API**

The e2e API is started by Playwright, **not** by `docker-compose.e2e.yml` — that file runs only
Postgres. In `frontend/playwright.config.ts`, add to the first `webServer` entry's `env` block,
after `RateLimiting__Auth__PermitLimit`:

```ts
        // The same move ApiFactory makes. The order specs place an order and then assert the
        // list contains it; a cached page would turn the eviction path into a source of
        // intermittent failures in a suite that is not testing the cache. Backend integration
        // tests cover it instead, with the cache deliberately on.
        Cache__Enabled: 'false',
```

- [ ] **Step 5: Write the failing integration tests**

**One deliberate deviation from the spec, which lists "a served hit" among the three
end-to-end cases.** A cache hit is not observable over HTTP: both a hit and a miss return the
same 200 with the same body, and the only difference — that the handler did not run — is
invisible to a client. Proving it would mean writing a row straight into the database behind the
API's back and asserting a subsequent `GET` does *not* show it, which makes an assertion out of
staleness and would start failing the moment a TTL boundary is crossed mid-test.

So the hit is asserted where it is actually observable, at the Infrastructure level, by
`SendAsync_TwiceForTheSameUserAndArguments_RunsTheHandlerOnce` in Task 2. The integration suite
covers the three things only real HTTP can show: user isolation across two real sessions,
read-your-own-writes across a real commit, and that a 404 is not retained. If you would rather
have the database-bypass hit test as well, that is a scope addition — raise it, do not add it
silently.

Create `tests/Api.IntegrationTests/Orders/OrderCachingTests.cs`:

```csharp
using System.Net.Http.Json;
using AiFramework.Api.Orders;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Orders;

/// <summary>
/// The cache's own behaviour over real HTTP, with it deliberately switched on. Joins
/// ApiFactoryCollection so it reuses the one Postgres container the project already starts, then
/// layers a second host on top with Cache:Enabled=true — WithWebHostBuilder composes over
/// ApiFactory.ConfigureWebHost, so the container's connection string and the outbox-pump removal
/// both still apply.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class OrderCachingTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _cached;

    public OrderCachingTests(ApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _cached = factory.WithWebHostBuilder(
            builder => builder.UseSetting("Cache:Enabled", "true"));
    }

    public void Dispose() => _cached.Dispose();

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = _cached.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                Username = $"u{Guid.NewGuid():N}"[..32],
                Password = "a long enough test password",
                DisplayName = "Cache Test User",
            });

        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task PlaceAsync(HttpClient client, string sku)
    {
        var response = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = sku, Quantity = 1 });

        response.EnsureSuccessStatusCode();
    }

    private static async Task<OrderPageResponse> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/api/orders", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<OrderPageResponse>();
        page.Should().NotBeNull();
        return page!;
    }

    [Fact]
    public async Task PlacingAnOrder_ThenListingImmediately_ReturnsTheNewOrder()
    {
        using var client = await SignedInClientAsync();
        await PlaceAsync(client, "CACHE-FIRST");
        await ListAsync(client);

        // The page is now cached. Without synchronous eviction this second SKU would be missing.
        await PlaceAsync(client, "CACHE-SECOND");
        var page = await ListAsync(client);

        page.Items.Select(i => i.Sku).Should().Contain(
            "CACHE-SECOND",
            "a client that refetches straight after a 201 must read its own write; this is the " +
            "whole reason eviction is synchronous rather than riding the outbox");
    }

    [Fact]
    public async Task ListingOrders_ForTwoDifferentUsers_DoesNotShareAPage()
    {
        using var alice = await SignedInClientAsync();
        using var bob = await SignedInClientAsync();

        await PlaceAsync(alice, "ALICE-ONLY");
        await ListAsync(alice);

        var bobsPage = await ListAsync(bob);

        bobsPage.Items.Select(i => i.Sku).Should().NotContain(
            "ALICE-ONLY",
            "the cache key is scoped to ICurrentUser.Id; sharing one entry across callers would " +
            "be a data leak, not a stale read");
    }

    [Fact]
    public async Task FetchingAnOrderThatDoesNotExist_TwiceInARow_Returns404Both()
    {
        using var client = await SignedInClientAsync();
        var missing = Guid.NewGuid();

        var first = await client.GetAsync(new Uri($"/api/orders/{missing}", UriKind.Relative));
        var second = await client.GetAsync(new Uri($"/api/orders/{missing}", UriKind.Relative));

        first.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        second.StatusCode.Should().Be(
            System.Net.HttpStatusCode.NotFound,
            "a failure is never cached, so the second request re-runs the handler rather than " +
            "being served a stored 404");
    }
}
```

- [ ] **Step 6: Run them and verify they fail**

Run: `dotnet test tests/Api.IntegrationTests --nologo --verbosity quiet --filter FullyQualifiedName~OrderCachingTests`

Expected: all three PASS, because nothing is cached yet — the queries have not opted in. **This is
the one place in this plan where a passing test is the "red" state.** Confirm they pass now, so
that after Step 7 you know the caching path is what they are exercising rather than an
accidentally-uncached one. Requires Docker for Testcontainers; if Docker is not running, say so
and stop rather than skipping.

- [ ] **Step 7: Opt the three types in**

In `src/Application/Orders/GetOrders.cs`, replace the record declaration:

```csharp
public sealed record GetOrders(int Limit, string? Cursor) : IQuery<OrderPage>, ICacheable
{
    // The user scope is NOT here — the caching behavior prepends the query type and the caller's
    // id. Putting a user id in this string would duplicate it, not secure it.
    public string CacheKey => $"{Limit}:{Cursor}";

    // Thirty seconds: long enough that a refocus-driven refetch is a hit, short enough that a
    // write from another device shows up without anyone waiting on it. Clamped by
    // CacheOptions.MaximumDuration.
    public TimeSpan Duration => TimeSpan.FromSeconds(30);
}
```

In `src/Application/Orders/GetOrder.cs`:

```csharp
public sealed record GetOrder(Guid Id) : IQuery<OrderView>, ICacheable
{
    public string CacheKey => Id.ToString();

    public TimeSpan Duration => TimeSpan.FromSeconds(30);
}
```

In `src/Application/Orders/PlaceOrder.cs`:

```csharp
public sealed record PlaceOrder(string Sku, int Quantity) : ICommand<Guid>, IInvalidatesCache
{
    // Both order reads, because a new order changes the list and nothing else. nameof rather
    // than a literal, so renaming a query type is a compile error here instead of a silent
    // eviction that stops matching anything.
    public IReadOnlyList<string> Tags => [nameof(GetOrders), nameof(GetOrder)];
}
```

- [ ] **Step 8: Run the integration tests again**

Run: `dotnet test tests/Api.IntegrationTests --nologo --verbosity quiet --filter FullyQualifiedName~OrderCachingTests`

Expected: PASS, 3 tests — now with the cache genuinely in the path. If
`PlacingAnOrder_ThenListingImmediately_ReturnsTheNewOrder` fails, eviction is not reaching the
key the query wrote; check that `PlaceOrder.Tags` uses `nameof(GetOrders)` and that
`EvictAsync` runs after `CommitAsync`.

- [ ] **Step 9: Run the full backend suite**

Run: `dotnet build --nologo --verbosity quiet` then `dotnet test --nologo --verbosity quiet`

Expected: build clean, everything green. The existing order tests run with the cache off, per
Step 1.

- [ ] **Step 10: Confirm the contract did not move**

No controller, DTO, or `[ProducesResponseType]` changed, so the committed OpenAPI document and
the generated client must be untouched. Confirm:

```bash
git status --short openapi frontend/src/api/schema.d.ts
```

Expected: no output. If either file shows as modified, something in `Api` changed that this task
did not intend — investigate before committing.

- [ ] **Step 11: Commit**

```bash
git add src/Application/Orders src/Api/Program.cs src/Api/appsettings.json \
        tests/Api.IntegrationTests/ApiFactory.cs \
        tests/Api.IntegrationTests/Orders/OrderCachingTests.cs \
        frontend/playwright.config.ts
git commit -m "feat(orders): cache the order reads, evicting on place

GetOrders and GetOrder opt in at thirty seconds; PlaceOrder evicts both for the
caller who placed it. The integration tests stand up their own host with the
cache on — ApiFactory and the Playwright webServer both switch it off, so the
suites written against uncached reads keep testing what they were written for.

The e2e switch goes in playwright.config.ts, not docker-compose.e2e.yml: that
compose file runs only Postgres, and the API is started by Playwright.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: Guard the registration, and document the decision

**Files:**
- Modify: `tests/Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs`
- Create: `docs/adr/0009-caching-scoped-to-the-caller.md`
- Modify: `CLAUDE.md`
- Modify: `src/Application/CLAUDE.md`
- Modify: `src/Infrastructure/CLAUDE.md`
- Modify: `.claude/skills/dotnet-conventions/SKILL.md`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: no code consumed by later tasks. This is the final task.

- [ ] **Step 1: Write the failing completeness test**

The existing file already guards "every command and query is registered". The gap this adds:
every `ICacheable` query must be a registered query, so a type that opts into caching but was
never registered fails a test rather than a request. Add to
`tests/Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs`:

```csharp
    [Fact]
    public void EveryCacheableType_IsARegisteredQuery()
    {
        var services = new ServiceCollection();
        services.AddMessaging();
        var registered = services
            .Select(d => d.ImplementationInstance)
            .OfType<QueryDescriptor>()
            .Select(d => d.QueryType)
            .ToHashSet();

        var cacheable = ApplicationMarker.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                && typeof(ICacheable).IsAssignableFrom(t))
            .ToArray();

        cacheable.Should().NotBeEmpty(
            "the scan must find at least GetOrders; an empty result means it is looking at the " +
            "wrong assembly, not that nothing is cacheable");

        var unregistered = cacheable.Where(t => !registered.Contains(t));

        unregistered.Should().BeEmpty(
            "an ICacheable type that is not a registered query is caching nothing — the marker " +
            "only takes effect inside AddQuery's dispatch delegate");
    }

    [Fact]
    public void EveryInvalidatingCommand_DeclaresAtLeastOneTag()
    {
        var invalidating = ApplicationMarker.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                && typeof(IInvalidatesCache).IsAssignableFrom(t))
            .ToArray();

        invalidating.Should().NotBeEmpty(
            "the scan must find at least PlaceOrder; an empty result means it is looking at the " +
            "wrong assembly");

        // Constructed through FormatterServices-free means: every command here is a record with
        // a primary constructor, so Activator needs its arguments. Reading the property off an
        // uninitialized instance is enough, because Tags is a computed property that does not
        // touch constructor state in any current implementation — and if one ever does, this
        // test will tell you by throwing rather than by passing wrongly.
        foreach (var type in invalidating)
        {
            var instance = System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(type);

            var tags = ((IInvalidatesCache)instance).Tags;

            tags.Should().NotBeEmpty(
                $"'{type.Name}' implements IInvalidatesCache but evicts nothing, which is a "
                + "no-op that reads as protection");
        }
    }
```

- [ ] **Step 2: Run them and verify they pass**

Run: `dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet --filter FullyQualifiedName~RegistrationCompletenessTests`

Expected: PASS. These two guard against a future mistake rather than fixing a present one, so
they are green on arrival — that is correct for a completeness test.

**If `EveryInvalidatingCommand_DeclaresAtLeastOneTag` throws** rather than failing an assertion,
`PlaceOrder.Tags` depends on constructor state in a way this test cannot reach. Replace
`GetUninitializedObject` with an explicit `new PlaceOrder("x", 1)` and drop the reflection loop —
report that you did, and why.

- [ ] **Step 3: Recover the measured publish numbers**

```bash
git log --format=%B -20 | grep -A1 "Release publish"
```

Use the two numbers Task 1 recorded. If Task 1 recorded `not measured`, write that, with its
reason, into the ADR rather than a number.

- [ ] **Step 4: Write ADR 0009**

Create `docs/adr/0009-caching-scoped-to-the-caller.md`. Follow the format of
`docs/adr/0008-brute-force-protection.md`: `# 0009. <title>`, then `**Date:**` and `**Status:**`,
then `## Context`, `## Decision`, `## Consequences`. Write it as prose that argues, not as a
bulleted summary — each decision states what was chosen and what it rejected.

It must record, at minimum:

- **Context:** no cache existed; the driver is the pattern rather than a measured hot path; both
  order reads hard-scope to `ICurrentUser.Id`, which makes a key that omits the user a data leak
  rather than a staleness bug.
- **The `Result<T>` constraint.** Its `Error` accessor throws when read on a success, so a
  successful `Result` cannot be serialized and its `internal` constructor makes deserialization
  impossible. The cache stores `TResponse`. This forced the design; it was not preferred.
- **Opt-in pipeline behavior**, and the three alternatives rejected: in-handler calls, a
  repository decorator, HTTP `ETag`/`304`.
- **The behavior composes the key**, so a query cannot forget the caller; a cacheable query with
  no caller throws rather than sharing one entry.
- **`HybridCache`, L1 only** — for stampede protection, `RemoveByTagAsync`, and an L2 tier later;
  Redis rejected for this slice.
- **Synchronous eviction**, because the outbox is polled and read-your-own-writes would break;
  and because an event-driven eviction would have required `OrderPlaced` to grow an `OwnerId`.
- **The TTL on the query, the cap and kill switch in config.**
- **Off in tests and e2e**, following ADR 0008's limiter precedent, with the cache's own tests
  turning it on.
- **Consequences:** no OpenAPI regeneration, no Wolverine codegen, no migration; the measured
  publish delta from Step 3.
- **Accepted risks:** the unbounded key space (bounded in practice by the duration cap), and the
  uncaught eviction failure — with an explicit note that the latter must be revisited if an L2
  tier is added, since a committed write would then be able to surface a 500.

Cross-reference the spec at `docs/superpowers/specs/2026-09-09-query-caching-design.md`.

- [ ] **Step 5: Add the traps to the root `CLAUDE.md`**

Insert a `## Caching` section after the existing `## The API contract` section:

```markdown
## Caching

Queries opt in by implementing `ICacheable` (`src/Application/Abstractions/Caching.cs`); commands
opt in to eviction with `IInvalidatesCache`. `GetOrders` and `GetOrder` are cached at thirty
seconds; `PlaceOrder` evicts both for the caller who placed the order. Nothing on the auth path is
cached, deliberately and permanently — ADR 0008's lockout state must be read every time.

Three things that will cost you time:

- **The cache stores `TResponse`, not `Result<T>`.** `Result<T>.Error` throws when read on a
  success, so `System.Text.Json` fails on a successful `Result` and the `internal` constructor
  makes deserializing one impossible. The behavior rebuilds `Result.Success(value)` on a hit.
- **`CacheKey` must NOT contain a user id.** The behavior prepends the query type and
  `ICurrentUser.Id` via `CacheScope`, which is also what `IInvalidatesCache.Tags` is composed
  with — the tag is the key's own prefix, and that is the whole eviction mechanism. An
  `ICacheable` query dispatched with no current user throws rather than sharing one entry across
  every caller.
- **The cache is OFF under test.** `ApiFactory` sets `Cache:Enabled=false` and
  `frontend/playwright.config.ts`'s `webServer` env sets `Cache__Enabled=false` — not
  `docker-compose.e2e.yml`, which runs only Postgres. `Orders/OrderCachingTests` stands up its own
  host with the cache on, the same split `AuthRateLimitTests` uses for the rate limiter.

No test waits for a TTL to lapse; `HybridCache` expires on its own clock, which `IClock` cannot
reach. The only TTL arithmetic is `CacheDuration.Clamp`, tested directly.

See ADR 0009.
```

- [ ] **Step 6: Add the layer notes**

Read each file first and match its existing structure and tone rather than appending a
mismatched block.

To `src/Application/CLAUDE.md`, add a short section covering: how to opt a query in
(`: IQuery<T>, ICacheable`, `CacheKey` is arguments only, `Duration` is a literal); that no
caching package may be referenced from this layer; and that `IInvalidatesCache.Tags` uses
`nameof(TheQuery)` rather than a string literal.

To `src/Infrastructure/CLAUDE.md`, add a short section covering: `Behaviors.CachedAsync` and
`Behaviors.EvictAsync` as the two cache behaviors and where they hook into
`MessagingRegistration`; `CacheScope` as the single source of key and tag composition; and that
`CacheOptions` is bound in `Program.cs` rather than in `AddCaching`, with the reason.

- [ ] **Step 7: Add the convention to the `dotnet-conventions` skill**

In `.claude/skills/dotnet-conventions/SKILL.md`, add a caching subsection so the rule loads when
someone writes a query rather than only when they read the ADR. Keep it to the two rules that
prevent bugs: `CacheKey` never contains a user id (the behavior prepends it), and a query is
cached only when it opts in — never add `ICacheable` to anything on the auth path.

- [ ] **Step 8: Verify the whole repository**

Run each, and report honestly which ran:

```bash
dotnet build --nologo --verbosity quiet
dotnet test --nologo --verbosity quiet
npm run lint --prefix frontend
npm run build --prefix frontend
npm test --prefix frontend -- --run
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/hooks/tests/run-hook-tests.ps1
```

Then, if Docker is running:

```bash
npm run e2e --prefix frontend
```

Stop the dev API first if one is running — `npm run e2e` starts its own on 5234 with
`reuseExistingServer: false`. A skipped step is never reported as a pass.

- [ ] **Step 9: Commit**

```bash
git add tests/Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs \
        docs/adr/0009-caching-scoped-to-the-caller.md \
        CLAUDE.md src/Application/CLAUDE.md src/Infrastructure/CLAUDE.md \
        .claude/skills/dotnet-conventions/SKILL.md
git commit -m "docs: ADR 0009 records the caching decisions, and guard the markers

Two completeness tests join the existing set: an ICacheable type that is not a
registered query caches nothing, and an IInvalidatesCache command with no tags
evicts nothing. Both are no-ops that read as protection, so they fail a test
rather than a request.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Notes for the executor

**Two places where a passing test is the red state.** Task 4 Step 6 runs the integration tests
before the queries opt in, and they pass — that is deliberate, and confirming it is what tells
you Step 8's pass exercises the caching path rather than an accidentally-uncached one. Task 5
Step 2's completeness tests are green on arrival for the same reason every completeness test in
this repository is.

**The package version is pinned to `10.9.0`, and the reason is not obvious.**
`Microsoft.Extensions.Caching.Hybrid` versions on its own release counter — `9.3.0 … 9.10.0`,
then `10.0.0 … 10.9.0` — rather than tracking the shared framework's patch line, so it does not
and cannot match the `10.0.11` on the other `Microsoft.Extensions.*` references. An earlier draft
of this plan told the executor to stop if the version was not `10.0.x`; that condition was
checking the wrong thing and has been replaced. What mattered was the API surface, and it was
verified by compile probe against `10.9.0`: the six-argument `GetOrCreateAsync` used in Task 2
Step 5, `HybridCacheEntryOptions.Expiration`, `AddHybridCache`'s options callback, and
`RemoveByTagAsync` returning `ValueTask`.

**If `AddHybridCache` turns out not to need `ILogger`,** the `services.AddLogging()` calls in the
test helpers are harmless and should stay: they cost nothing and they stop the tests depending on
an implementation detail of the library.

**Do not regenerate the OpenAPI document or the Wolverine codegen.** Neither changes here, and
Task 4 Step 10 asserts that. If `git status` shows either moving, find out why before committing.
