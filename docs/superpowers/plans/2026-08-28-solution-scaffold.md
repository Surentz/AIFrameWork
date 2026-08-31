# Solution Scaffold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the four `CLAUDE.md`-only layer directories into a building, testable .NET solution with the guardrails demonstrably firing.

**Architecture:** Four class libraries under `src/` matching the existing directory names, four xUnit projects under `tests/` mirroring them, wired into an `.slnx` solution. Project references form the Clean Architecture DAG. Each test project carries one architecture test asserting its layer's assembly references, which closes the `.csproj` blind spot ADR 0002 documents.

**Tech Stack:** .NET SDK 10.0.400, `net10.0`, xUnit, FluentAssertions, NSubstitute, `Microsoft.AspNetCore.Mvc.Testing`.

**Spec:** `docs/superpowers/specs/2026-08-28-in-process-messaging-design.md` — this plan builds the foundation that spec's §6 and §7–§10 are implemented on. No messaging code is written here.

**Prerequisite for:** Plan 2 (request path, spec §6) and Plan 3 (event path, spec §7–§10).

## Global Constraints

- **.NET SDK 10.0.400.** Verified present via `dotnet --list-sdks` on 2026-08-28.
- **Root namespace `AiFramework`.** Fixed by `.claude/hooks/hooks.config.json`.
- **Layer directories stay exactly `src/Domain`, `src/Application`, `src/Infrastructure`, `src/Api`.** The hook regex is `/src/(?:[\w.]+\.)?(Domain|Application|Infrastructure|Api)/`. Renaming a directory to something not in `hooks.config.json` silently disarms the rule for that layer.
- **Test project directories are `tests/Domain.Tests`, `tests/Application.Tests`, `tests/Infrastructure.Tests`, `tests/Api.IntegrationTests`,** per `tests/CLAUDE.md`.
- **Warnings are errors** from compiler, analyzers and build — `Directory.Build.props` sets all three. A warning is a build failure.
- **Nullable is enabled.** Already set in `Directory.Build.props`.
- **Never suppress a diagnostic without a justification comment.** No suppressions are expected in this plan; if one becomes necessary, stop and report rather than adding it silently.
- **Package versions are resolved at execution time and then pinned,** never written from memory. `Directory.Build.props` already records this practice for the analyzer packages.
- **No EF Core, Npgsql, or Testcontainers in this plan.** They arrive in Plan 3 alongside the code that needs them.

## File structure

| File | Responsibility |
|---|---|
| `AiFramework.slnx` | Solution, all eight projects |
| `Directory.Build.props` | *Modify* — pin `TargetFramework` and `LangVersion` |
| `src/Domain/AiFramework.Domain.csproj` | Innermost layer. No project references. |
| `src/Domain/AssemblyMarker.cs` | Stable handle on the assembly, for architecture tests now and assembly scanning in Plan 2 |
| `src/Application/AiFramework.Application.csproj` | References `Domain` |
| `src/Application/AssemblyMarker.cs` | As above |
| `src/Infrastructure/AiFramework.Infrastructure.csproj` | References `Application` |
| `src/Infrastructure/AssemblyMarker.cs` | As above |
| `src/Api/AiFramework.Api.csproj` | References `Application` and `Infrastructure`. Web SDK. |
| `tests/*.Tests/*.csproj` | One per layer, each with the architecture test for that layer |
| `CLAUDE.md` | *Modify* — record the pinned `net10.0` in the Versions table |

**Architecture tests, and their honest limit.** `Assembly.GetReferencedAssemblies()` lists only assemblies the compiler actually emitted a reference to — an unused `ProjectReference` does not appear. So these tests catch a layer that *uses* something it must not, which is the case that matters, but they do not catch a dangling unused reference. That residual gap stays a review responsibility, as ADR 0002 says.

---

### Task 1: Pin the toolchain and create the solution with the Domain layer

**Files:**
- Create: `AiFramework.slnx`
- Create: `src/Domain/AiFramework.Domain.csproj`
- Modify: `Directory.Build.props:5-7`

**Interfaces:**
- Consumes: nothing
- Produces: `AiFramework.slnx`; project `src/Domain/AiFramework.Domain.csproj` with assembly name `AiFramework.Domain` and root namespace `AiFramework.Domain`

- [ ] **Step 1: Create the solution file**

```bash
cd /c/Users/Sures/RiderProjects/AIFrameWork
dotnet new sln -n AiFramework --format slnx
```

Expected: `AiFramework.slnx` created.

- [ ] **Step 2: Create the Domain class library in the existing directory**

`dotnet new classlib` writes into an existing directory without complaint. `-n` sets the project name, `-o` the output directory, so the `.csproj` lands beside the existing `CLAUDE.md`.

```bash
dotnet new classlib -n AiFramework.Domain -o src/Domain
rm src/Domain/Class1.cs
dotnet sln AiFramework.slnx add src/Domain/AiFramework.Domain.csproj
```

Expected: `src/Domain/AiFramework.Domain.csproj` exists, `src/Domain/CLAUDE.md` untouched, `Class1.cs` gone.

- [ ] **Step 3: Discover the real LangVersion rather than guessing it**

```bash
dotnet msbuild src/Domain/AiFramework.Domain.csproj -getProperty:LangVersion -getProperty:TargetFramework
```

Record both values from the output. Do not write a version you did not see printed.

- [ ] **Step 4: Pin TargetFramework and LangVersion**

In `Directory.Build.props`, replace the placeholder comment:

```xml
    <!-- TargetFramework and LangVersion are intentionally NOT set here.
         Pin them when the solution is scaffolded and the installed SDK is known. -->
```

with the values printed in Step 3 — using those exact values, not the illustrative ones below:

```xml
    <!-- Pinned 2026-08-28 against SDK 10.0.400, read from
         `dotnet msbuild -getProperty:LangVersion`. Bump deliberately. -->
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
```

- [ ] **Step 5: Build and confirm it is clean**

```bash
dotnet build --nologo --verbosity quiet
```

Expected: build succeeds, zero warnings. Any warning is a failure here — report the diagnostic with `file:line` and stop.

- [ ] **Step 6: Prove warnings-are-errors actually fires**

This repo's central claim is that a warning cannot accumulate. Verify it rather than trusting it.

```bash
cat > src/Domain/WarningProbe.cs <<'EOF'
namespace AiFramework.Domain;

internal static class WarningProbe
{
    internal static int Probe()
    {
        int unused = 1;
        return 0;
    }
}
EOF
dotnet build --nologo --verbosity quiet
```

Expected: **build FAILS** with CS0219 (variable assigned but never used) reported as an error. An analyzer may fire on this file first (Sonar or Meziantou on the unused member) — any diagnostic reported as an *error* proves the mechanism; the specific rule id does not matter.

If the build succeeds, warnings-as-errors is not working. Stop and report it — every later task depends on this guarantee.

- [ ] **Step 7: Remove the probe and rebuild**

```bash
rm src/Domain/WarningProbe.cs
dotnet build --nologo --verbosity quiet
```

Expected: build succeeds.

- [ ] **Step 8: Commit**

```bash
git add AiFramework.slnx Directory.Build.props src/Domain/AiFramework.Domain.csproj
git commit -m "chore: create the solution and pin the toolchain to net10.0"
```

---

### Task 2: Domain.Tests, and the first architecture test

**Files:**
- Create: `src/Domain/AssemblyMarker.cs`
- Create: `tests/Domain.Tests/AiFramework.Domain.Tests.csproj`
- Create: `tests/Domain.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: `AiFramework.Domain` from Task 1
- Produces: `AiFramework.Domain.AssemblyMarker.Assembly` — a `static Assembly` property. Tasks 3, 4 and Plan 2's registration-completeness test use the same shape per layer.

- [ ] **Step 1: Add the assembly marker**

An empty marker class trips Sonar S2094 ("classes should not be empty"), which is an error here. A static class with a real member does not, and the property is what the tests actually need.

Create `src/Domain/AssemblyMarker.cs`:

```csharp
using System.Reflection;

namespace AiFramework.Domain;

/// <summary>Stable handle on this assembly for tests and assembly scanning.</summary>
public static class AssemblyMarker
{
    public static Assembly Assembly => typeof(AssemblyMarker).Assembly;
}
```

- [ ] **Step 2: Create the test project and wire it up**

```bash
dotnet new xunit -n AiFramework.Domain.Tests -o tests/Domain.Tests
rm tests/Domain.Tests/UnitTest1.cs
dotnet sln AiFramework.slnx add tests/Domain.Tests/AiFramework.Domain.Tests.csproj
dotnet add tests/Domain.Tests/AiFramework.Domain.Tests.csproj reference src/Domain/AiFramework.Domain.csproj
```

- [ ] **Step 3: Add FluentAssertions — and check its licence before pinning**

`tests/CLAUDE.md` mandates FluentAssertions. Note that FluentAssertions changed licence at **v8**: v7 and earlier are Apache-2.0, v8+ requires a paid licence for commercial use.

```bash
dotnet list tests/Domain.Tests/AiFramework.Domain.Tests.csproj package
dotnet add tests/Domain.Tests/AiFramework.Domain.Tests.csproj package FluentAssertions
dotnet list tests/Domain.Tests/AiFramework.Domain.Tests.csproj package
```

Read the resolved version. **If it resolved to 8.x or later, stop and ask** whether to accept the commercial licence or pin `--version 7.*`. Do not decide this unilaterally; it is a licensing commitment, and it will be repeated in three more test projects.

- [ ] **Step 4: Write the failing architecture test**

Create `tests/Domain.Tests/ArchitectureTests.cs`:

Note on the usings: `ImplicitUsings` is enabled repo-wide, so `System` and `System.Linq` are
already in scope, and `AiFramework.Domain` resolves without a using because C# walks up from
`AiFramework.Domain.Tests`. Adding any of them trips SonarAnalyzer S1128, which
`CodeAnalysisTreatWarningsAsErrors` turns into a build failure. Keep the using list minimal.

```csharp
using FluentAssertions;
using Xunit;

namespace AiFramework.Domain.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_references_no_other_layer()
    {
        var referenced = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("AiFramework.", StringComparison.Ordinal))
            .ToArray();

        referenced.Should().BeEmpty(
            "Domain is the innermost layer and must reference no other layer");
    }
}
```

- [ ] **Step 5: Run it and confirm it passes**

```bash
dotnet test tests/Domain.Tests --nologo --verbosity quiet
```

Expected: PASS. This one passes immediately because Task 1 created Domain with no references — the test is a regression guard, and it is worth confirming it *can* fail. Temporarily add a reference to prove it:

```bash
dotnet add src/Domain/AiFramework.Domain.csproj reference src/Application/AiFramework.Application.csproj 2>&1 | head -3
```

Expected: this command **fails**, because `src/Application` does not exist yet. That is fine — Task 3 revisits this check once Application exists. Move on.

- [ ] **Step 6: Commit**

```bash
git add src/Domain/AssemblyMarker.cs tests/Domain.Tests/
git commit -m "test: add Domain.Tests with an assembly-reference architecture test"
```

---

### Task 3: Application layer and its tests

**Files:**
- Create: `src/Application/AiFramework.Application.csproj`, `src/Application/AssemblyMarker.cs`
- Create: `tests/Application.Tests/AiFramework.Application.Tests.csproj`, `tests/Application.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: `AiFramework.Domain` (Task 1), the marker shape from Task 2
- Produces: `AiFramework.Application.AssemblyMarker.Assembly`; project reference `Application → Domain`

- [ ] **Step 1: Create the project and its reference**

```bash
dotnet new classlib -n AiFramework.Application -o src/Application
rm src/Application/Class1.cs
dotnet sln AiFramework.slnx add src/Application/AiFramework.Application.csproj
dotnet add src/Application/AiFramework.Application.csproj reference src/Domain/AiFramework.Domain.csproj
```

- [ ] **Step 2: Add the assembly marker**

Create `src/Application/AssemblyMarker.cs`:

```csharp
using System.Reflection;

namespace AiFramework.Application;

/// <summary>Stable handle on this assembly for tests and assembly scanning.</summary>
public static class AssemblyMarker
{
    public static Assembly Assembly => typeof(AssemblyMarker).Assembly;
}
```

- [ ] **Step 3: Create the test project**

```bash
dotnet new xunit -n AiFramework.Application.Tests -o tests/Application.Tests
rm tests/Application.Tests/UnitTest1.cs
dotnet sln AiFramework.slnx add tests/Application.Tests/AiFramework.Application.Tests.csproj
dotnet add tests/Application.Tests/AiFramework.Application.Tests.csproj reference src/Application/AiFramework.Application.csproj
dotnet add tests/Application.Tests/AiFramework.Application.Tests.csproj package FluentAssertions
dotnet add tests/Application.Tests/AiFramework.Application.Tests.csproj package NSubstitute
```

Use the same FluentAssertions version decided in Task 2, Step 3.

- [ ] **Step 4: Write the architecture test**

Create `tests/Application.Tests/ArchitectureTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace AiFramework.Application.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Application_references_Domain_only()
    {
        // Assert on the DISALLOWED subset, not with OnlyContain. FluentAssertions 7.2.2
        // throws on an empty collection ("but the collection is empty") rather than
        // treating OnlyContain as vacuously true - and this collection IS empty today,
        // because Application uses no Domain type yet so the compiler omits the
        // reference. BeEmpty on the disallowed subset is correct in both cases.
        var disallowed = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null
                && n.StartsWith("AiFramework.", StringComparison.Ordinal)
                && !n.Equals("AiFramework.Domain", StringComparison.Ordinal))
            .ToArray();

        disallowed.Should().BeEmpty(
            "Application may depend on Domain and nothing else inward-facing");
    }
}
```

- [ ] **Step 5: Run it**

```bash
dotnet test tests/Application.Tests --nologo --verbosity quiet
```

Expected: PASS.

Note: `Application` currently uses no Domain type, so the compiler omits the reference entirely and the `AiFramework.*` set is empty. The assertion above is written against the *disallowed* subset precisely because of that — asserting `OnlyContain(n => n == "AiFramework.Domain")` on the allowed set does **not** pass vacuously in FluentAssertions 7.2.2; it fails with "but the collection is empty" (verified empirically 2026-08-28 against the pinned 7.2.2, by both the Task 3 implementer and the controller). Task 4 is unaffected: `NotContain` is safe on an empty sequence.

The residual limit of the technique still stands — an empty set cannot prove Application *does* reach Domain, only that it reaches nothing forbidden. That half becomes a real assertion once Plan 2 adds code touching Domain.

- [ ] **Step 6: Prove the Domain test from Task 2 can actually fail**

Now that Application exists, complete the check deferred in Task 2, Step 5.

```bash
dotnet add src/Domain/AiFramework.Domain.csproj reference src/Application/AiFramework.Application.csproj
dotnet build --nologo --verbosity quiet
```

Expected: **build FAILS** with a circular-dependency error (Application already references Domain). That failure is itself the proof the DAG is wired correctly. Revert:

```bash
dotnet remove src/Domain/AiFramework.Domain.csproj reference src/Application/AiFramework.Application.csproj
dotnet build --nologo --verbosity quiet
```

Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/Application/ tests/Application.Tests/ AiFramework.slnx
git commit -m "feat: add the Application layer with its architecture test"
```

---

### Task 4: Infrastructure layer and its tests

**Files:**
- Create: `src/Infrastructure/AiFramework.Infrastructure.csproj`, `src/Infrastructure/AssemblyMarker.cs`
- Create: `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj`, `tests/Infrastructure.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: `AiFramework.Application` (Task 3)
- Produces: `AiFramework.Infrastructure.AssemblyMarker.Assembly`; project reference `Infrastructure → Application`

No EF Core or Npgsql here — Plan 3 adds them with the outbox code that uses them.

- [ ] **Step 1: Create the project and its reference**

```bash
dotnet new classlib -n AiFramework.Infrastructure -o src/Infrastructure
rm src/Infrastructure/Class1.cs
dotnet sln AiFramework.slnx add src/Infrastructure/AiFramework.Infrastructure.csproj
dotnet add src/Infrastructure/AiFramework.Infrastructure.csproj reference src/Application/AiFramework.Application.csproj
```

`Infrastructure` reaches `Domain` transitively through `Application`, which the matrix permits. Do not add a direct `Domain` reference.

- [ ] **Step 2: Add the assembly marker**

Create `src/Infrastructure/AssemblyMarker.cs`:

```csharp
using System.Reflection;

namespace AiFramework.Infrastructure;

/// <summary>Stable handle on this assembly for tests and assembly scanning.</summary>
public static class AssemblyMarker
{
    public static Assembly Assembly => typeof(AssemblyMarker).Assembly;
}
```

- [ ] **Step 3: Create the test project**

```bash
dotnet new xunit -n AiFramework.Infrastructure.Tests -o tests/Infrastructure.Tests
rm tests/Infrastructure.Tests/UnitTest1.cs
dotnet sln AiFramework.slnx add tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj
dotnet add tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj reference src/Infrastructure/AiFramework.Infrastructure.csproj
dotnet add tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj package FluentAssertions
dotnet add tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj package NSubstitute
```

- [ ] **Step 4: Write the architecture test**

Create `tests/Infrastructure.Tests/ArchitectureTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace AiFramework.Infrastructure.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Infrastructure_does_not_reference_Api()
    {
        var referenced = AssemblyMarker.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referenced.Should().NotContain("AiFramework.Api",
            "Infrastructure is inside Api and must never depend on it");
    }
}
```

- [ ] **Step 5: Run it**

```bash
dotnet test tests/Infrastructure.Tests --nologo --verbosity quiet
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Infrastructure/ tests/Infrastructure.Tests/ AiFramework.slnx
git commit -m "feat: add the Infrastructure layer with its architecture test"
```

---

### Task 5: Api layer and integration test host

**Files:**
- Create: `src/Api/AiFramework.Api.csproj`, `src/Api/Program.cs`
- Create: `tests/Api.IntegrationTests/AiFramework.Api.IntegrationTests.csproj`, `tests/Api.IntegrationTests/HealthTests.cs`
- Delete: the `webapi` template's generated weather sample

**Interfaces:**
- Consumes: `AiFramework.Application` (Task 3), `AiFramework.Infrastructure` (Task 4)
- Produces: `AiFramework.Api.Program` reachable from `WebApplicationFactory<Program>`; a `GET /health` endpoint returning 200

- [ ] **Step 1: Create the Api project**

```bash
dotnet new webapi -n AiFramework.Api -o src/Api
dotnet sln AiFramework.slnx add src/Api/AiFramework.Api.csproj
dotnet add src/Api/AiFramework.Api.csproj reference src/Application/AiFramework.Application.csproj
dotnet add src/Api/AiFramework.Api.csproj reference src/Infrastructure/AiFramework.Infrastructure.csproj
```

The `Infrastructure` reference is legal **for DI registration only** — the one matrix cell the hook cannot check. Nothing in this plan uses it beyond wiring.

- [ ] **Step 2: Replace the template's sample with a health endpoint**

The `webapi` template generates a weather-forecast sample. Delete it and write `src/Api/Program.cs` in full:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory</c> can find the entry point.</summary>
public partial class Program;
```

Remove any other generated file under `src/Api/` that the template added for the sample (for example `WeatherForecast.cs` or a sample controller), keeping `appsettings*.json`, `Properties/launchSettings.json`, and `CLAUDE.md`.

- [ ] **Step 3: Build and confirm zero warnings**

```bash
dotnet build --nologo --verbosity quiet
```

Expected: success. The `webapi` template plus `GenerateDocumentationFile` can surface documentation or nullability diagnostics — every one is an error here. Fix them properly; do not suppress.

- [ ] **Step 4: Create the integration test project**

```bash
dotnet new xunit -n AiFramework.Api.IntegrationTests -o tests/Api.IntegrationTests
rm tests/Api.IntegrationTests/UnitTest1.cs
dotnet sln AiFramework.slnx add tests/Api.IntegrationTests/AiFramework.Api.IntegrationTests.csproj
dotnet add tests/Api.IntegrationTests/AiFramework.Api.IntegrationTests.csproj reference src/Api/AiFramework.Api.csproj
dotnet add tests/Api.IntegrationTests/AiFramework.Api.IntegrationTests.csproj package FluentAssertions
dotnet add tests/Api.IntegrationTests/AiFramework.Api.IntegrationTests.csproj package Microsoft.AspNetCore.Mvc.Testing
```

- [ ] **Step 5: Write the failing test**

Create `tests/Api.IntegrationTests/HealthTests.cs`:

`System.Net` is genuinely needed for `HttpStatusCode`; `System.Threading.Tasks` is implicit.

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AiFramework.Api.IntegrationTests;

public sealed class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_200()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 6: Run it**

```bash
dotnet test tests/Api.IntegrationTests --nologo --verbosity quiet
```

Expected: PASS. This proves the composition root boots under `WebApplicationFactory`, which every Plan 2 endpoint test depends on.

- [ ] **Step 7: Commit**

```bash
git add src/Api/ tests/Api.IntegrationTests/ AiFramework.slnx
git commit -m "feat: add the Api layer with a health endpoint and integration host"
```

---

### Task 6: Verify the guardrails and record the pinned versions

**Files:**
- Modify: `CLAUDE.md` — the Versions table
- Verify: `.claude/hooks/dependency-rule.ps1` blocks a real violation

**Interfaces:**
- Consumes: everything from Tasks 1–5
- Produces: a solution where `/verify` is green and the dependency hook is demonstrated, not assumed

- [ ] **Step 1: Confirm the dependency-rule hook actually blocks**

The hook is a PreToolUse gate on `.cs` writes, so it is proven by attempting an edit, not by building. Attempt to add a banned `using` to a Domain file — use the Edit tool, not a shell heredoc, since the hook intercepts tool calls:

Try to add this line to `src/Domain/AssemblyMarker.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
```

Expected: the edit is **BLOCKED** with the message "BLOCKED: Clean Architecture dependency rule violation in the Domain layer."

If the edit succeeds, the hook is not firing. Stop and report it — CLAUDE.md and ADR 0002 both claim it blocks, and an unenforced guardrail is worse than a documented absence.

- [ ] **Step 2: Confirm the file was not modified**

```bash
git diff --stat src/Domain/AssemblyMarker.cs
```

Expected: no output. If the file changed, revert it with `git checkout -- src/Domain/AssemblyMarker.cs`.

- [ ] **Step 3: Run the full build and test suite**

```bash
dotnet build --nologo --verbosity quiet
dotnet test --nologo --verbosity quiet
```

Expected: build clean with zero warnings; all tests pass. Report the actual test count.

- [ ] **Step 4: Record the pinned versions**

Update the Versions table in `CLAUDE.md`. Replace the `.NET SDK` row's context and add the target framework, using the values actually pinned in Task 1:

```markdown
| | Version |
|---|---|
| .NET SDK | 10.0.400 |
| Target framework | net10.0 |
| Node | 24.20.0 |
| Angular | _unpinned — set at `ng new`_ |
```

Also update the line beneath it so it no longer claims the framework is unpinned:

```markdown
Verified 2026-08-28 from `dotnet --list-sdks` and `node --version`. The target framework was
pinned when the solution was scaffolded. Angular stays unpinned until the workspace is
created; pin it from the generated `package.json`, not from memory.
```

- [ ] **Step 5: Run `/verify` and report honestly**

Run the `/verify` command. The frontend half will report that the Angular workspace does not exist — that is expected and is not a failure. Report what actually ran.

- [ ] **Step 6: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: record the pinned target framework now that the solution exists"
```

---

## Definition of done

- `dotnet build` is clean with zero warnings across eight projects.
- `dotnet test` passes, with an architecture test for Domain, Application and Infrastructure, plus the Api health test. Api has no architecture test: it may legally reference every other layer, and the one Api-specific rule (Infrastructure for DI registration only) needs controllers to exist — deferred to the next plan.
- A deliberate unused variable fails the build (Task 1, Step 6).
- A banned `using` in `Domain` is blocked at edit time (Task 6, Step 1).
- `Directory.Build.props` pins `TargetFramework` and `LangVersion` to values read from the SDK.
- `CLAUDE.md`'s Versions table matches what is pinned.
- No EF Core, Npgsql, Testcontainers, or messaging code exists yet. Those are Plans 2 and 3.

## Decisions deferred to Plan 2 or 3

- **FluentAssertions v8 licensing.** Task 2, Step 3 stops and asks. Whatever is decided applies to all four test projects.
- **An architecture test for `Api → Infrastructure` DI-only usage.** ADR 0002 leaves that cell to review. A test asserting no controller type references an `Infrastructure` type would close it, but needs controllers to exist first — revisit in Plan 2.
- **EF Core, Npgsql and Testcontainers packages.** Plan 3, with the outbox.
