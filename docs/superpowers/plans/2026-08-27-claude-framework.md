# Claude Code Framework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the project-level `.claude` framework and its supporting repo config, so Clean Architecture's dependency rule is machine-checked, warnings are structurally impossible to ignore, and Claude gets the right conventions in front of it per layer.

**Architecture:** Five PowerShell hooks enforce the rules Claude Code can check at edit time and turn end; `Directory.Build.props` and `.editorconfig` enforce everything the compiler can check; layered `CLAUDE.md` files, four skills, three agents, and four slash commands carry the rest. The hooks are the only executable part and are the only part that gets a test cycle — they are tested by feeding fixture payloads to each script and asserting exit codes.

**Tech Stack:** PowerShell 5.1 (`powershell.exe`), JSON, Markdown, MSBuild props, EditorConfig, ESLint flat config. No .NET or Node code is written by this plan.

**Spec:** `docs/superpowers/specs/2026-08-27-claude-framework-design.md`

## Global Constraints

Every task's requirements implicitly include this section.

- **Shell is Windows PowerShell 5.1** (`powershell.exe`), invoked `-NoProfile -ExecutionPolicy Bypass -File`. `pwsh` is **not installed** — never reference it.
- **PS 5.1 syntax only:** no `&&`, no `||`, no ternary `?:`, no `??`, no `?.`, no `ConvertFrom-Json -AsHashtable`.
- **Every hook fails open.** Exit `0` silently on: unparseable payload, missing `file_path`, path outside the repo, absent `dotnet`/`node`, or any unexpected exception. A hook that wedges the session is worse than a hook that misses a violation.
- **Never call `exit` inside a `try` block.** Set a `$denyMessage` variable, and exit after the `try`/`catch`. This is the pattern in every hook below — follow it exactly.
- **Blocking is `exit 2` with the reason on stderr.** Claude reads stderr and acts on it. Anything else is not a block.
- **Root namespace is read from `.claude/hooks/hooks.config.json`**, defaulting to `AiFramework`. No hook hardcodes it.
- **Toolchain is absent on this machine — verified 2026-08-27:** `dotnet` is host 6.0.5 runtime-only ("No SDKs were found"), and `node`/`npm`/`ng` are not on PATH. **No task may run `dotnet build`, `dotnet test`, `npm`, or `ng`, and no task may claim such a command was verified.** Hook tests must pass on this machine as-is.
- **Never invoke `npx` for a tool that may not be installed** — it silently downloads packages. Guard on `frontend/node_modules/.bin/<tool>.cmd` existing, and exit 0 when it doesn't.
- **Version numbers in `CLAUDE.md` stay placeholders** (spec D10). Do not guess a .NET or Angular version.
- **Commit after every task**, using the message given in that task's final step.

---

## File Structure

| File | Responsibility |
|---|---|
| `.gitignore` | Ignore .NET, Angular, and Rider build output |
| `.gitattributes` | Normalise line endings; `.ps1` stays CRLF |
| `Directory.Build.props` | Warnings-as-errors, nullable, analyzer packages — inherited by every future `.csproj` |
| `.editorconfig` | C# style plus the curated analyzer severity elevations |
| `.claude/hooks/lib/payload.ps1` | Shared stdin/JSON/config helpers. No policy logic. |
| `.claude/hooks/hooks.config.json` | Root namespace and layer names. The only place they appear. |
| `.claude/hooks/dependency-rule.ps1` | Blocks banned `using` per layer |
| `.claude/hooks/no-secrets.ps1` | Blocks populated secrets in `appsettings*.json` |
| `.claude/hooks/protect-migrations.ps1` | Blocks edits to existing EF migrations |
| `.claude/hooks/format-and-lint.ps1` | Formats `.cs`; lints and formats frontend files |
| `.claude/hooks/verify-build.ps1` | Stop hook: blocks turn end on a dirty build |
| `.claude/hooks/tests/run-hook-tests.ps1` | Fixture-driven exit-code assertions |
| `.claude/settings.json` | Permissions and hook wiring |
| `CLAUDE.md` + 6 per-layer files | Layered context |
| `.claude/agents/*.md` | Three review agents |
| `.claude/commands/*.md` | Four slash commands |
| `.claude/skills/*/SKILL.md` | Four convention skills |

Policy lives in the individual hook scripts; shared plumbing lives in `payload.ps1`. Keeping them apart is what lets a hook be tested by swapping one fixture.

---

## Task 1: Repo hygiene

**Files:**
- Create: `.gitignore`
- Create: `.gitattributes`

**Interfaces:**
- Consumes: nothing
- Produces: a repo where `bin/`, `obj/`, `node_modules/`, and `.idea/` are ignored. Later tasks assume `git status` is clean apart from files they create.

- [ ] **Step 1: Write `.gitignore`**

```gitignore
# --- .NET ---
[Bb]in/
[Oo]bj/
[Dd]ebug/
[Rr]elease/
*.user
*.suo
*.userprefs
artifacts/
TestResults/
[Tt]est[Rr]esult*/
*.coverage
*.trx
project.lock.json
.packages/
*.nupkg
!.nuget/packages/

# --- Secrets ---
*.pfx
*.p12
secrets.json
appsettings.*.Local.json
.env
.env.*
!.env.example

# --- Node / Angular ---
node_modules/
dist/
.angular/
npm-debug.log*
yarn-error.log*
*.tsbuildinfo
coverage/

# --- Rider / JetBrains ---
.idea/
*.sln.iml

# --- OS ---
Thumbs.db
.DS_Store

# --- Claude Code ---
.claude/settings.local.json
```

- [ ] **Step 2: Write `.gitattributes`**

`.ps1` is pinned to CRLF because Windows PowerShell 5.1 can misparse LF-only scripts in some encodings, and these hooks must never fail to run.

```gitattributes
* text=auto eol=lf

*.ps1   text eol=crlf
*.cmd   text eol=crlf
*.bat   text eol=crlf
*.sln   text eol=crlf

*.cs    text eol=lf diff=csharp
*.ts    text eol=lf
*.html  text eol=lf
*.scss  text eol=lf
*.json  text eol=lf
*.md    text eol=lf

*.png   binary
*.jpg   binary
*.ico   binary
*.pfx   binary
```

- [ ] **Step 3: Verify `.idea/` is now ignored**

Run:

```bash
git check-ignore -v .idea/workspace.xml
git status --short
```

Expected: the first command prints a `.gitignore` line matching `.idea/`; `git status --short` no longer lists `?? .idea/`.

- [ ] **Step 4: Commit**

```bash
git add .gitignore .gitattributes
git commit -m "chore: add .gitignore and .gitattributes"
```

---

## Task 2: Compile-time enforcement

**Files:**
- Create: `Directory.Build.props`
- Create: `.editorconfig`

**Interfaces:**
- Consumes: nothing
- Produces: MSBuild properties inherited by every future `.csproj`, and the analyzer severity elevations the spec's §6.2 depends on. No later task modifies these.

**Note on verification:** there is no .NET SDK on this machine, so these files cannot be exercised by a build. Verification is limited to well-formedness. Do not claim otherwise.

- [ ] **Step 1: Write `Directory.Build.props`**

The three `TreatWarningsAsErrors` properties cover three distinct warning sources — compiler, analyzers, and build/restore. Setting only the first leaves the other two able to emit ignorable warnings.

```xml
<Project>

  <PropertyGroup>
    <!-- TargetFramework and LangVersion are intentionally NOT set here.
         Pin them when the solution is scaffolded and the installed SDK is known. -->
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- Warnings are errors, from all three sources. -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <CodeAnalysisTreatWarningsAsErrors>true</CodeAnalysisTreatWarningsAsErrors>
    <MSBuildTreatWarningsAsErrors>true</MSBuildTreatWarningsAsErrors>

    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisMode>Recommended</AnalysisMode>

    <!-- Swagger needs the XML doc file; CS1591 (missing doc comment) fires on
         every public member and would stall development under warnings-as-errors. -->
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>

    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <!-- Version attributes are omitted deliberately: pin them at scaffold time
         against the SDK that is actually installed. -->
    <PackageReference Include="SonarAnalyzer.CSharp" PrivateAssets="all" />
    <PackageReference Include="Meziantou.Analyzer" PrivateAssets="all" />
    <PackageReference Include="AsyncFixer" PrivateAssets="all" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write `.editorconfig`**

The `dotnet_diagnostic` entries are load-bearing: `AnalysisMode=Recommended` leaves CA1031 and CA1062 **disabled**, and `CodeAnalysisTreatWarningsAsErrors` cannot promote a rule that never fires. Without these lines the "no catch-all exceptions" rule silently does nothing.

```ini
root = true

[*]
charset = utf-8
end_of_line = lf
indent_style = space
insert_final_newline = true
trim_trailing_whitespace = true

[*.{cs,csx}]
indent_size = 4
csharp_style_namespace_declarations = file_scoped:error
csharp_style_var_when_type_is_apparent = true:suggestion
csharp_prefer_braces = true:error
dotnet_style_require_accessibility_modifiers = always:error
csharp_style_prefer_null_check_over_type_check = true:error

# --- Exception discipline (spec 6.4) ---
dotnet_diagnostic.CA1031.severity = error   # do not catch general exception types
dotnet_diagnostic.CA2200.severity = error   # rethrow to preserve stack details

# --- Argument and null validation (spec 6.2) ---
dotnet_diagnostic.CA1062.severity = error   # validate arguments of public methods

# --- Disposal ---
dotnet_diagnostic.CA2000.severity = error
dotnet_diagnostic.CA1063.severity = error

# --- Deliberately off: no synchronization context in ASP.NET Core ---
dotnet_diagnostic.CA2007.severity = none

[*.{ts,js,mjs,cjs}]
indent_size = 2
quote_type = single

[*.{html,scss,css,json,yml,yaml}]
indent_size = 2

[*.{xml,csproj,props,targets}]
indent_size = 2

[*.ps1]
indent_size = 4
end_of_line = crlf

[*.md]
trim_trailing_whitespace = false
```

- [ ] **Step 3: Verify both files parse**

Run:

```powershell
[xml]$p = Get-Content -LiteralPath Directory.Build.props -Raw
"props root: $($p.Project.LocalName)"
$ec = Get-Content -LiteralPath .editorconfig
"editorconfig sections: $(($ec | Where-Object { $_ -match '^\[' }).Count)"
"CA1031 line: $($ec | Where-Object { $_ -match 'CA1031' })"
```

Expected: `props root: Project`; a non-zero section count; and the CA1031 line printed as `dotnet_diagnostic.CA1031.severity = error`. No exception from the XML cast.

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props .editorconfig
git commit -m "chore: enforce warnings-as-errors and analyzer severities"
```

---

## Task 3: Hook plumbing and test harness

This is the foundation every hook task builds on. It is written test-first: the harness is created, run, and observed to fail before `payload.ps1` exists.

**Files:**
- Create: `.claude/hooks/hooks.config.json`
- Create: `.claude/hooks/lib/payload.ps1`
- Create: `.claude/hooks/tests/run-hook-tests.ps1`

**Interfaces:**
- Consumes: nothing
- Produces, all dot-sourced by every hook in Tasks 4–8:
  - `Read-HookPayload` → parsed payload object, or `$null` if stdin is empty or unparseable
  - `Get-Prop -Object <obj> -Name <string>` → property value or `$null`, never throws
  - `Get-HookConfig -HookDir <string>` → parsed `hooks.config.json` or `$null`
  - `Get-TargetPath -Payload <obj>` → `tool_input.file_path` or `$null`
  - `Get-WrittenText -Payload <obj>` → `string`, all text the tool is about to write, joined by newline
  - `Get-RepoRoot -Payload <obj> -HookDir <string>` → repo root path
- Also produces `Assert-Exit` and `Invoke-Hook` in the harness, used by Tasks 4–8.

- [ ] **Step 1: Write the failing test harness**

Create `.claude/hooks/tests/run-hook-tests.ps1`:

```powershell
#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$TestsDir = $PSScriptRoot
$HookDir  = Split-Path -Parent $TestsDir
$Fixtures = Join-Path $TestsDir 'fixtures'

$script:Passed = 0
$script:Failed = 0

function Invoke-Hook {
    param(
        [Parameter(Mandatory)][string]$Script,
        [Parameter(Mandatory)][string]$Fixture
    )
    $hookPath    = Join-Path $HookDir $Script
    $fixturePath = Join-Path $Fixtures $Fixture
    if (-not (Test-Path -LiteralPath $hookPath))    { return -100 }
    if (-not (Test-Path -LiteralPath $fixturePath)) { return -101 }

    $json = Get-Content -LiteralPath $fixturePath -Raw
    $json | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hookPath | Out-Null
    return $LASTEXITCODE
}

function Assert-Exit {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Script,
        [Parameter(Mandatory)][string]$Fixture,
        [Parameter(Mandatory)][int]$Expected
    )
    $actual = Invoke-Hook -Script $Script -Fixture $Fixture
    if ($actual -eq $Expected) {
        Write-Host "  PASS  $Name"
        $script:Passed++
    } else {
        Write-Host "  FAIL  $Name -- expected exit $Expected, got $actual" -ForegroundColor Red
        $script:Failed++
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Condition
    )
    if ($Condition) {
        Write-Host "  PASS  $Name"
        $script:Passed++
    } else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        $script:Failed++
    }
}

Write-Host ''
Write-Host 'payload.ps1 library'
. (Join-Path $HookDir 'lib\payload.ps1')

$sample = '{"tool_name":"Write","tool_input":{"file_path":"C:\\r\\src\\Domain\\A.cs","content":"using System;"}}' | ConvertFrom-Json
Assert-True -Name 'Get-Prop returns a present property'      -Condition ((Get-Prop -Object $sample -Name 'tool_name') -eq 'Write')
Assert-True -Name 'Get-Prop returns null for a missing one'  -Condition ($null -eq (Get-Prop -Object $sample -Name 'nope'))
Assert-True -Name 'Get-Prop tolerates a null object'         -Condition ($null -eq (Get-Prop -Object $null -Name 'x'))
Assert-True -Name 'Get-TargetPath reads file_path'           -Condition ((Get-TargetPath -Payload $sample) -like '*Domain*A.cs')
Assert-True -Name 'Get-WrittenText reads Write content'      -Condition ((Get-WrittenText -Payload $sample) -eq 'using System;')

$edit = '{"tool_name":"Edit","tool_input":{"file_path":"a.cs","old_string":"x","new_string":"using Foo;"}}' | ConvertFrom-Json
Assert-True -Name 'Get-WrittenText reads Edit new_string'    -Condition ((Get-WrittenText -Payload $edit) -eq 'using Foo;')

$multi = '{"tool_name":"MultiEdit","tool_input":{"file_path":"a.cs","edits":[{"new_string":"one"},{"new_string":"two"}]}}' | ConvertFrom-Json
Assert-True -Name 'Get-WrittenText joins MultiEdit edits'    -Condition ((Get-WrittenText -Payload $multi) -match 'one' -and (Get-WrittenText -Payload $multi) -match 'two')

Write-Host ''
Write-Host ("Passed: {0}   Failed: {1}" -f $script:Passed, $script:Failed)
if ($script:Failed -gt 0) { exit 1 }
exit 0
```

- [ ] **Step 2: Run it and watch it fail**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: FAIL. The dot-source of `lib\payload.ps1` throws because the file does not exist yet — the script terminates before any assertion runs.

- [ ] **Step 3: Write `hooks.config.json`**

```json
{
  "rootNamespace": "AiFramework",
  "layers": ["Domain", "Application", "Infrastructure", "Api"],
  "frontendDir": "frontend"
}
```

- [ ] **Step 4: Write `lib/payload.ps1`**

Note `Get-Prop`: under `Set-StrictMode -Version Latest`, reading a missing property off a `PSCustomObject` **throws**. Every property read in every hook goes through this helper for that reason.

```powershell
#requires -Version 5.1
# Shared plumbing for Claude Code hooks. Contains no policy - policy lives in each hook.

function Get-Prop {
    param($Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

function Read-HookPayload {
    # Claude Code writes the hook payload as JSON on stdin.
    # Returns $null when stdin is empty or unparseable, so callers fail open.
    try {
        $raw = [Console]::In.ReadToEnd()
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return ($raw | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Get-HookConfig {
    param([Parameter(Mandatory)][string]$HookDir)
    $path = Join-Path $HookDir 'hooks.config.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Get-TargetPath {
    param($Payload)
    $toolInput = Get-Prop -Object $Payload -Name 'tool_input'
    $path = Get-Prop -Object $toolInput -Name 'file_path'
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    return $path
}

function Get-WrittenText {
    # Every piece of text the pending tool call would put on disk:
    #   Write -> content, Edit -> new_string, MultiEdit -> edits[].new_string
    param($Payload)
    $toolInput = Get-Prop -Object $Payload -Name 'tool_input'
    if ($null -eq $toolInput) { return '' }

    $parts = New-Object System.Collections.ArrayList

    $content = Get-Prop -Object $toolInput -Name 'content'
    if (-not [string]::IsNullOrEmpty($content)) { [void]$parts.Add($content) }

    $newString = Get-Prop -Object $toolInput -Name 'new_string'
    if (-not [string]::IsNullOrEmpty($newString)) { [void]$parts.Add($newString) }

    $edits = Get-Prop -Object $toolInput -Name 'edits'
    if ($null -ne $edits) {
        foreach ($edit in $edits) {
            $value = Get-Prop -Object $edit -Name 'new_string'
            if (-not [string]::IsNullOrEmpty($value)) { [void]$parts.Add($value) }
        }
    }

    if ($parts.Count -eq 0) { return '' }
    return ($parts -join "`n")
}

function Get-RepoRoot {
    param($Payload, [Parameter(Mandatory)][string]$HookDir)
    $cwd = Get-Prop -Object $Payload -Name 'cwd'
    if (-not [string]::IsNullOrWhiteSpace($cwd)) { return $cwd }
    # .claude/hooks -> .claude -> repo root
    return (Split-Path -Parent (Split-Path -Parent $HookDir))
}

function ConvertTo-ForwardSlash {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    return ($Path -replace '\\', '/')
}
```

- [ ] **Step 5: Run the harness and watch it pass**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: `Passed: 7   Failed: 0`, exit code 0.

- [ ] **Step 6: Commit**

```bash
git add .claude/hooks/hooks.config.json .claude/hooks/lib/payload.ps1 .claude/hooks/tests/run-hook-tests.ps1
git commit -m "feat(hooks): add payload plumbing and fixture test harness"
```

---

## Task 4: `dependency-rule` hook

**Files:**
- Create: `.claude/hooks/dependency-rule.ps1`
- Create: `.claude/hooks/tests/fixtures/domain-ef-violation.json`
- Create: `.claude/hooks/tests/fixtures/domain-clean.json`
- Create: `.claude/hooks/tests/fixtures/domain-annotations-violation.json`
- Create: `.claude/hooks/tests/fixtures/application-infrastructure-violation.json`
- Create: `.claude/hooks/tests/fixtures/api-all-layers.json`
- Create: `.claude/hooks/tests/fixtures/malformed.json`
- Modify: `.claude/hooks/tests/run-hook-tests.ps1` — append a section before the summary block

**Interfaces:**
- Consumes: `Read-HookPayload`, `Get-Prop`, `Get-HookConfig`, `Get-TargetPath`, `Get-WrittenText`, `ConvertTo-ForwardSlash` from Task 3; `Assert-Exit` from the harness
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Write the fixtures**

`domain-ef-violation.json`:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Domain/Orders/Order.cs",
    "content": "using System;\nusing Microsoft.EntityFrameworkCore;\n\nnamespace AiFramework.Domain.Orders;\n\npublic sealed class Order { }\n"
  }
}
```

`domain-clean.json`:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Domain/Orders/Order.cs",
    "content": "using System;\nusing System.Collections.Generic;\n\nnamespace AiFramework.Domain.Orders;\n\npublic sealed class Order\n{\n    public required Guid Id { get; init; }\n}\n"
  }
}
```

`domain-annotations-violation.json`:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Domain/Orders/Order.cs",
    "content": "using System.ComponentModel.DataAnnotations;\n\nnamespace AiFramework.Domain.Orders;\n\npublic sealed class Order\n{\n    [Required]\n    public string Name { get; set; } = string.Empty;\n}\n"
  }
}
```

`application-infrastructure-violation.json`:

```json
{
  "tool_name": "Edit",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Application/Orders/CreateOrderHandler.cs",
    "old_string": "using System;",
    "new_string": "using System;\nusing AiFramework.Infrastructure.Persistence;"
  }
}
```

`api-all-layers.json`:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Api/Controllers/OrdersController.cs",
    "content": "using AiFramework.Domain.Orders;\nusing AiFramework.Application.Orders;\nusing AiFramework.Infrastructure.Persistence;\nusing Microsoft.AspNetCore.Mvc;\n\nnamespace AiFramework.Api.Controllers;\n\npublic sealed class OrdersController : ControllerBase { }\n"
  }
}
```

`malformed.json` — deliberately not valid JSON, to prove the hook fails open:

```
{ this is not json
```

- [ ] **Step 2: Append the failing assertions to the harness**

In `run-hook-tests.ps1`, insert immediately **before** the `Write-Host ''` / summary block at the end:

```powershell
Write-Host ''
Write-Host 'dependency-rule.ps1'
Assert-Exit -Name 'Domain + EF Core is blocked'            -Script 'dependency-rule.ps1' -Fixture 'domain-ef-violation.json'                 -Expected 2
Assert-Exit -Name 'Domain + DataAnnotations is blocked'    -Script 'dependency-rule.ps1' -Fixture 'domain-annotations-violation.json'        -Expected 2
Assert-Exit -Name 'Domain with only System is allowed'     -Script 'dependency-rule.ps1' -Fixture 'domain-clean.json'                        -Expected 0
Assert-Exit -Name 'Application + Infrastructure blocked'   -Script 'dependency-rule.ps1' -Fixture 'application-infrastructure-violation.json' -Expected 2
Assert-Exit -Name 'Api may reference every layer'          -Script 'dependency-rule.ps1' -Fixture 'api-all-layers.json'                      -Expected 0
Assert-Exit -Name 'Malformed payload fails open'           -Script 'dependency-rule.ps1' -Fixture 'malformed.json'                           -Expected 0
```

- [ ] **Step 3: Run the harness and watch the new assertions fail**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: the 7 library assertions still pass; all 6 new ones FAIL with `got -100` (the hook script does not exist yet). Exit code 1.

- [ ] **Step 4: Write `dependency-rule.ps1`**

Note the `$denyMessage` pattern — no `exit` inside the `try`, per Global Constraints.

```powershell
#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\payload.ps1')

$denyMessage = $null

try {
    $payload = Read-HookPayload
    $path = Get-TargetPath -Payload $payload

    if ($null -ne $path -and $path -match '\.cs$') {
        $normalized = ConvertTo-ForwardSlash -Path $path

        $layer = $null
        if ($normalized -match '/src/(Domain|Application|Infrastructure|Api)/') {
            $layer = $Matches[1]
        }

        if ($null -ne $layer) {
            $config = Get-HookConfig -HookDir $PSScriptRoot
            $root = Get-Prop -Object $config -Name 'rootNamespace'
            if ([string]::IsNullOrWhiteSpace($root)) { $root = 'AiFramework' }

            $banned = @{
                'Domain' = @(
                    "$root.Application", "$root.Infrastructure", "$root.Api",
                    'Microsoft.EntityFrameworkCore', 'Microsoft.AspNetCore',
                    'Microsoft.Extensions.DependencyInjection', 'System.Data',
                    'System.ComponentModel.DataAnnotations'
                )
                'Application' = @(
                    "$root.Infrastructure", "$root.Api",
                    'Microsoft.EntityFrameworkCore', 'Microsoft.AspNetCore'
                )
                'Infrastructure' = @("$root.Api")
                'Api' = @()
            }

            $text = Get-WrittenText -Payload $payload

            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $violations = New-Object System.Collections.ArrayList
                foreach ($namespaceName in $banned[$layer]) {
                    $escaped = [regex]::Escape($namespaceName)
                    # matches "using X;", "global using X;", "using static X.Y;"
                    if ($text -match "(?m)^\s*(global\s+)?using\s+(static\s+)?$escaped\b") {
                        [void]$violations.Add($namespaceName)
                    }
                }

                if ($violations.Count -gt 0) {
                    $list = ($violations | ForEach-Object { "  - $_" }) -join "`n"
                    $denyMessage = @"
BLOCKED: Clean Architecture dependency rule violation in the $layer layer.

File: $path
Banned namespace(s) referenced:
$list

The $layer layer must not reference these. Depend on an abstraction instead:
put the interface in Application and the implementation in Infrastructure.

See src/$layer/CLAUDE.md, and section 6.1 of
docs/superpowers/specs/2026-08-27-claude-framework-design.md
"@
                }
            }
        }
    }
} catch {
    $denyMessage = $null   # fail open: a bug in this hook must never wedge the session
}

if ($null -ne $denyMessage) {
    [Console]::Error.WriteLine($denyMessage)
    exit 2
}
exit 0
```

- [ ] **Step 5: Run the harness and watch everything pass**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: `Passed: 13   Failed: 0`, exit code 0.

- [ ] **Step 6: Commit**

```bash
git add .claude/hooks/dependency-rule.ps1 .claude/hooks/tests/
git commit -m "feat(hooks): enforce the Clean Architecture dependency rule"
```

---

## Task 5: `no-secrets` hook

**Files:**
- Create: `.claude/hooks/no-secrets.ps1`
- Create: `.claude/hooks/tests/fixtures/appsettings-password-secret.json`
- Create: `.claude/hooks/tests/fixtures/appsettings-password-placeholder.json`
- Create: `.claude/hooks/tests/fixtures/appsettings-apikey-secret.json`
- Create: `.claude/hooks/tests/fixtures/appsettings-apikey-empty.json`
- Create: `.claude/hooks/tests/fixtures/other-json-with-password.json`
- Modify: `.claude/hooks/tests/run-hook-tests.ps1`

**Interfaces:**
- Consumes: the Task 3 library and harness
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Write the fixtures**

`appsettings-password-secret.json`:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Api/appsettings.json",
    "content": "{\n  \"ConnectionStrings\": {\n    \"Default\": \"Server=db;Database=app;User Id=sa;Password=hunter2;\"\n  }\n}\n"
  }
}
```

`appsettings-password-placeholder.json`:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Api/appsettings.json",
    "content": "{\n  \"ConnectionStrings\": {\n    \"Default\": \"Server=db;Database=app;User Id=sa;Password=${DB_PASSWORD};\"\n  }\n}\n"
  }
}
```

`appsettings-apikey-secret.json`:

```json
{
  "tool_name": "Edit",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Api/appsettings.Development.json",
    "old_string": "\"ApiKey\": \"\"",
    "new_string": "\"ApiKey\": \"sk-live-9f8a7b6c5d4e3f2a1b\""
  }
}
```

`appsettings-apikey-empty.json`:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Api/appsettings.json",
    "content": "{\n  \"ApiKey\": \"\",\n  \"ClientSecret\": \"REPLACE_ME\"\n}\n"
  }
}
```

`other-json-with-password.json` — proves the hook is scoped to `appsettings*.json` and does not police every file:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/.claude/hooks/tests/fixtures/sample.json",
    "content": "{ \"Password\": \"hunter2\" }"
  }
}
```

- [ ] **Step 2: Append the failing assertions**

Insert before the summary block in `run-hook-tests.ps1`:

```powershell
Write-Host ''
Write-Host 'no-secrets.ps1'
Assert-Exit -Name 'Populated Password= is blocked'      -Script 'no-secrets.ps1' -Fixture 'appsettings-password-secret.json'      -Expected 2
Assert-Exit -Name 'Password=${VAR} is allowed'          -Script 'no-secrets.ps1' -Fixture 'appsettings-password-placeholder.json' -Expected 0
Assert-Exit -Name 'Populated ApiKey is blocked'         -Script 'no-secrets.ps1' -Fixture 'appsettings-apikey-secret.json'        -Expected 2
Assert-Exit -Name 'Empty and REPLACE_ME are allowed'    -Script 'no-secrets.ps1' -Fixture 'appsettings-apikey-empty.json'         -Expected 0
Assert-Exit -Name 'Non-appsettings files are ignored'   -Script 'no-secrets.ps1' -Fixture 'other-json-with-password.json'         -Expected 0
Assert-Exit -Name 'Malformed payload fails open'        -Script 'no-secrets.ps1' -Fixture 'malformed.json'                        -Expected 0
```

- [ ] **Step 3: Run and watch the new assertions fail**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: 13 passing, 6 failing with `got -100`.

- [ ] **Step 4: Write `no-secrets.ps1`**

```powershell
#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\payload.ps1')

function Test-IsPlaceholder {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $true }
    $trimmed = $Value.Trim()
    if ($trimmed -match '^\$\{.*\}$') { return $true }   # ${DB_PASSWORD}
    if ($trimmed -match '^#\{.*\}$')  { return $true }   # #{OctopusVariable}
    if ($trimmed -match '^%.*%$')     { return $true }   # %ENV_VAR%
    if ($trimmed -match '^<.*>$')     { return $true }   # <your-key-here>
    if ($trimmed -match '^(?i)(REPLACE_ME|REPLACEME|CHANGEME|CHANGE_ME|TODO|PLACEHOLDER|SECRET|X{3,}|\*{3,}|\.{3})$') { return $true }
    return $false
}

$denyMessage = $null

try {
    $payload = Read-HookPayload
    $path = Get-TargetPath -Payload $payload

    if ($null -ne $path) {
        $leaf = Split-Path -Leaf (ConvertTo-ForwardSlash -Path $path)

        if ($leaf -match '^(?i)appsettings.*\.json$') {
            $text = Get-WrittenText -Payload $payload

            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $findings = New-Object System.Collections.ArrayList

                # Connection-string style: Password=..., Pwd=..., AccountKey=...
                foreach ($keyword in @('Password', 'Pwd', 'AccountKey')) {
                    $pattern = "(?i)\b$keyword\s*=\s*([^;""\\]*)"
                    foreach ($match in [regex]::Matches($text, $pattern)) {
                        $value = $match.Groups[1].Value
                        if (-not (Test-IsPlaceholder -Value $value)) {
                            [void]$findings.Add("$keyword= (connection string)")
                        }
                    }
                }

                # JSON key style: "ApiKey": "...."
                $jsonKeys = 'ApiKey|ClientSecret|Secret|Token|SigningKey|PrivateKey'
                foreach ($match in [regex]::Matches($text, "(?i)""($jsonKeys)""\s*:\s*""([^""]*)""")) {
                    $value = $match.Groups[2].Value
                    if (-not (Test-IsPlaceholder -Value $value)) {
                        [void]$findings.Add(('"{0}"' -f $match.Groups[1].Value))
                    }
                }

                if ($text -match '(?i)SharedAccessSignature\s*=') {
                    [void]$findings.Add('SharedAccessSignature=')
                }

                if ($findings.Count -gt 0) {
                    $list = (($findings | Select-Object -Unique) | ForEach-Object { "  - $_" }) -join "`n"
                    $denyMessage = @"
BLOCKED: a real secret is about to be written into $leaf.

File: $path
Detected:
$list

Configuration files are committed; secrets must not be. Use one of:
  dotnet user-secrets set "<Key>" "<value>"     (local development)
  environment variables                        (deployed environments)

Placeholders are fine and pass this check: "", `${ENV_VAR}`, <your-key>, REPLACE_ME.

See section 6.6 of docs/superpowers/specs/2026-08-27-claude-framework-design.md
"@
                }
            }
        }
    }
} catch {
    $denyMessage = $null
}

if ($null -ne $denyMessage) {
    [Console]::Error.WriteLine($denyMessage)
    exit 2
}
exit 0
```

- [ ] **Step 5: Run and watch everything pass**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: `Passed: 19   Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add .claude/hooks/no-secrets.ps1 .claude/hooks/tests/
git commit -m "feat(hooks): block secrets written into appsettings files"
```

---

## Task 6: `protect-migrations` hook

**Files:**
- Create: `.claude/hooks/protect-migrations.ps1`
- Create: `.claude/hooks/tests/fixtures/migration-edit.json`
- Create: `.claude/hooks/tests/fixtures/migration-write-new.json`
- Create: `.claude/hooks/tests/fixtures/ordinary-cs-edit.json`
- Modify: `.claude/hooks/tests/run-hook-tests.ps1`

**Interfaces:**
- Consumes: the Task 3 library and harness
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Write the fixtures**

`migration-edit.json`:

```json
{
  "tool_name": "Edit",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Infrastructure/Migrations/20260101120000_InitialCreate.cs",
    "old_string": "table.Column<string>(name: \"Name\", nullable: true)",
    "new_string": "table.Column<string>(name: \"Name\", nullable: false)"
  }
}
```

`migration-write-new.json` — creating a new migration stays allowed:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Infrastructure/Migrations/20260201090000_AddOrderTotal.cs",
    "content": "// generated by dotnet ef migrations add AddOrderTotal\n"
  }
}
```

`ordinary-cs-edit.json`:

```json
{
  "tool_name": "Edit",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Infrastructure/Persistence/OrderRepository.cs",
    "old_string": "return null;",
    "new_string": "return order;"
  }
}
```

- [ ] **Step 2: Append the failing assertions**

```powershell
Write-Host ''
Write-Host 'protect-migrations.ps1'
Assert-Exit -Name 'Editing an existing migration is blocked' -Script 'protect-migrations.ps1' -Fixture 'migration-edit.json'      -Expected 2
Assert-Exit -Name 'Creating a new migration is allowed'      -Script 'protect-migrations.ps1' -Fixture 'migration-write-new.json' -Expected 0
Assert-Exit -Name 'Ordinary .cs edits are untouched'         -Script 'protect-migrations.ps1' -Fixture 'ordinary-cs-edit.json'    -Expected 0
Assert-Exit -Name 'Malformed payload fails open'             -Script 'protect-migrations.ps1' -Fixture 'malformed.json'           -Expected 0
```

- [ ] **Step 3: Run and watch the new assertions fail**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: 19 passing, 4 failing with `got -100`.

- [ ] **Step 4: Write `protect-migrations.ps1`**

```powershell
#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\payload.ps1')

$denyMessage = $null

try {
    $payload = Read-HookPayload
    $toolName = Get-Prop -Object $payload -Name 'tool_name'

    # Only mutation of an existing file is blocked. Write creates new migrations,
    # which is exactly what "dotnet ef migrations add" produces.
    if ($toolName -eq 'Edit' -or $toolName -eq 'MultiEdit') {
        $path = Get-TargetPath -Payload $payload

        if ($null -ne $path) {
            $normalized = ConvertTo-ForwardSlash -Path $path

            if ($normalized -match '/Migrations/[^/]+\.cs$') {
                $leaf = Split-Path -Leaf $normalized
                $denyMessage = @"
BLOCKED: refusing to edit an existing EF Core migration.

File: $path

Migrations are an append-only history. Editing one that has been applied puts
the database out of sync with the model snapshot, and every other environment
keeps the old version.

Create a new migration instead:
  dotnet ef migrations add <DescriptiveName> --project src/Infrastructure --startup-project src/Api

If this migration has definitely never been applied anywhere, remove it with
"dotnet ef migrations remove" and regenerate it rather than hand-editing $leaf.

See section 6.7 of docs/superpowers/specs/2026-08-27-claude-framework-design.md
"@
            }
        }
    }
} catch {
    $denyMessage = $null
}

if ($null -ne $denyMessage) {
    [Console]::Error.WriteLine($denyMessage)
    exit 2
}
exit 0
```

- [ ] **Step 5: Run and watch everything pass**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: `Passed: 23   Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add .claude/hooks/protect-migrations.ps1 .claude/hooks/tests/
git commit -m "feat(hooks): protect applied EF migrations from hand edits"
```

---

## Task 7: `format-and-lint` hook

The only hook that shells out to the toolchain. On this machine both toolchains are absent, so **every assertion here proves the fail-open path.** That is the behaviour most likely to break the session, so it is the behaviour worth pinning down first.

**Files:**
- Create: `.claude/hooks/format-and-lint.ps1`
- Create: `.claude/hooks/tests/fixtures/format-cs.json`
- Create: `.claude/hooks/tests/fixtures/format-ts.json`
- Create: `.claude/hooks/tests/fixtures/format-unknown-ext.json`
- Modify: `.claude/hooks/tests/run-hook-tests.ps1`

**Interfaces:**
- Consumes: the Task 3 library and harness
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Write the fixtures**

`format-cs.json`:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/src/Domain/Orders/Order.cs",
    "content": "public sealed class Order { }"
  }
}
```

`format-ts.json`:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/frontend/src/app/orders/orders.component.ts",
    "content": "export class OrdersComponent {}"
  }
}
```

`format-unknown-ext.json`:

```json
{
  "tool_name": "Write",
  "cwd": "C:/repo",
  "tool_input": {
    "file_path": "C:/repo/README.txt",
    "content": "hello"
  }
}
```

- [ ] **Step 2: Append the failing assertions**

```powershell
Write-Host ''
Write-Host 'format-and-lint.ps1  (toolchain absent on this machine - these pin the fail-open path)'
Assert-Exit -Name '.cs exits 0 when no .NET SDK'      -Script 'format-and-lint.ps1' -Fixture 'format-cs.json'          -Expected 0
Assert-Exit -Name '.ts exits 0 when eslint absent'    -Script 'format-and-lint.ps1' -Fixture 'format-ts.json'          -Expected 0
Assert-Exit -Name 'Unhandled extension exits 0'       -Script 'format-and-lint.ps1' -Fixture 'format-unknown-ext.json' -Expected 0
Assert-Exit -Name 'Malformed payload fails open'      -Script 'format-and-lint.ps1' -Fixture 'malformed.json'          -Expected 0
```

- [ ] **Step 3: Run and watch the new assertions fail**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: 23 passing, 4 failing with `got -100`.

- [ ] **Step 4: Write `format-and-lint.ps1`**

`npx` is never used: it silently downloads packages that are not installed. Local `node_modules/.bin` binaries are probed directly instead.

```powershell
#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\payload.ps1')

function Test-DotnetSdkPresent {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) { return $false }
    try {
        $sdks = & dotnet --list-sdks
        if ($LASTEXITCODE -ne 0) { return $false }
        return (@($sdks).Count -gt 0)
    } catch {
        return $false
    }
}

$denyMessage = $null

try {
    $payload = Read-HookPayload
    $path = Get-TargetPath -Payload $payload

    if ($null -ne $path -and (Test-Path -LiteralPath $path)) {
        $extension = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
        $repoRoot = Get-RepoRoot -Payload $payload -HookDir $PSScriptRoot

        if ($extension -eq '.cs') {
            if (Test-DotnetSdkPresent) {
                & dotnet format whitespace --include $path --no-restore --verbosity quiet | Out-Null
            }
            # No SDK: nothing to do. Warnings are caught at build time instead.
        }
        elseif (@('.ts', '.html', '.scss', '.css', '.js', '.mjs') -contains $extension) {
            $binDir = Join-Path $repoRoot 'frontend\node_modules\.bin'
            $eslint = Join-Path $binDir 'eslint.cmd'
            $prettier = Join-Path $binDir 'prettier.cmd'

            if (Test-Path -LiteralPath $eslint) {
                $lintOutput = & $eslint --fix $path
                $lintExit = $LASTEXITCODE

                if (Test-Path -LiteralPath $prettier) {
                    & $prettier --write $path | Out-Null
                }

                if ($lintExit -ne 0) {
                    $detail = ($lintOutput | Out-String).Trim()
                    $denyMessage = @"
Lint errors remain in $path after eslint --fix.

$detail

Fix them before continuing. See frontend/CLAUDE.md and the angular-conventions skill.
"@
                }
            }
            # eslint not installed: nothing to do.
        }
    }
} catch {
    $denyMessage = $null
}

if ($null -ne $denyMessage) {
    [Console]::Error.WriteLine($denyMessage)
    exit 2
}
exit 0
```

- [ ] **Step 5: Run and watch everything pass**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: `Passed: 29   Failed: 0`. The two toolchain fixtures point at REAL temp files created by the harness (ruling R4), so the hook genuinely reaches its toolchain check instead of returning at the `Test-Path` guard. Baseline is 29 rather than the originally-written 27 because an approved fix round on Task 5 added two no-secrets assertions.

- [ ] **Step 6: Commit**

```bash
git add .claude/hooks/format-and-lint.ps1 .claude/hooks/tests/
git commit -m "feat(hooks): format on write and surface lint errors to Claude"
```

---

## Task 8: `verify-build` Stop hook

**Files:**
- Create: `.claude/hooks/verify-build.ps1`
- Create: `.claude/hooks/tests/fixtures/stop-hook-active.json`
- Create: `.claude/hooks/tests/fixtures/stop-normal.json`
- Modify: `.claude/hooks/tests/run-hook-tests.ps1`

**Interfaces:**
- Consumes: the Task 3 library and harness
- Produces: nothing consumed by later tasks

**Critical:** the `stop_hook_active` guard is what stops a build that cannot be made to pass from looping the session forever. It is the first thing the hook checks.

- [ ] **Step 1: Write the fixtures**

`stop-hook-active.json`:

```json
{
  "hook_event_name": "Stop",
  "cwd": "C:/repo",
  "stop_hook_active": true
}
```

`stop-normal.json`:

```json
{
  "hook_event_name": "Stop",
  "cwd": "C:/repo",
  "stop_hook_active": false
}
```

- [ ] **Step 2: Append the failing assertions**

```powershell
Write-Host ''
Write-Host 'verify-build.ps1'
Assert-Exit -Name 'stop_hook_active short-circuits (loop guard)' -Script 'verify-build.ps1' -Fixture 'stop-hook-active.json' -Expected 0
Assert-Exit -Name 'No solution present exits 0'                  -Script 'verify-build.ps1' -Fixture 'stop-normal.json'      -Expected 0
Assert-Exit -Name 'Malformed payload fails open'                 -Script 'verify-build.ps1' -Fixture 'malformed.json'        -Expected 0
```

- [ ] **Step 3: Run and watch the new assertions fail**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: 27 passing, 3 failing with `got -100`.

- [ ] **Step 4: Write `verify-build.ps1`**

```powershell
#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\payload.ps1')

function Test-DotnetSdkPresent {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) { return $false }
    try {
        $sdks = & dotnet --list-sdks
        if ($LASTEXITCODE -ne 0) { return $false }
        return (@($sdks).Count -gt 0)
    } catch {
        return $false
    }
}

$denyMessage = $null

try {
    $payload = Read-HookPayload

    # Loop guard: without this, a build that cannot be fixed re-triggers Stop forever.
    $alreadyActive = Get-Prop -Object $payload -Name 'stop_hook_active'

    if ($alreadyActive -ne $true) {
        $repoRoot = Get-RepoRoot -Payload $payload -HookDir $PSScriptRoot

        if (Test-Path -LiteralPath $repoRoot) {
            $solution = Get-ChildItem -LiteralPath $repoRoot -Filter '*.sln' -File -ErrorAction SilentlyContinue |
                        Select-Object -First 1

            if ($null -ne $solution -and (Test-DotnetSdkPresent)) {
                $buildOutput = & dotnet build $solution.FullName --nologo --verbosity quiet
                if ($LASTEXITCODE -ne 0) {
                    $detail = ($buildOutput | Out-String).Trim()
                    $denyMessage = @"
BLOCKED: the build is not clean, so this turn is not finished.

$detail

Warnings are errors in this repo (Directory.Build.props). Fix every diagnostic
above rather than suppressing it. If a suppression is genuinely correct, add it
with a justification comment.

To disable this gate, remove the "Stop" hook from .claude/settings.json -
see .claude/settings.local.json.example.
"@
                }
            }
        }
    }
} catch {
    $denyMessage = $null
}

if ($null -ne $denyMessage) {
    [Console]::Error.WriteLine($denyMessage)
    exit 2
}
exit 0
```

- [ ] **Step 5: Run and watch everything pass**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: `Passed: 32   Failed: 0`, exit code 0. All five hooks are now covered.

- [ ] **Step 6: Commit**

```bash
git add .claude/hooks/verify-build.ps1 .claude/hooks/tests/
git commit -m "feat(hooks): gate turn end on a warning-clean build"
```

---

## Task 9: Wire the hooks and permissions

**Files:**
- Create: `.claude/settings.json`
- Create: `.claude/settings.local.json.example`

**Interfaces:**
- Consumes: all five hook scripts from Tasks 4–8
- Produces: the live configuration. Project hooks **merge with** the user-level Orca hooks rather than replacing them.

- [ ] **Step 1: Write `.claude/settings.json`**

`$CLAUDE_PROJECT_DIR` is expanded by Claude Code, so the hooks work regardless of the session's working directory.

```json
{
  "permissions": {
    "allow": [
      "Bash(dotnet build:*)",
      "Bash(dotnet test:*)",
      "Bash(dotnet restore:*)",
      "Bash(dotnet format:*)",
      "Bash(dotnet ef:*)",
      "Bash(dotnet new:*)",
      "Bash(dotnet sln:*)",
      "Bash(dotnet user-secrets:*)",
      "Bash(npm ci)",
      "Bash(npm run:*)",
      "Bash(npm install:*)",
      "Bash(npx ng:*)",
      "Bash(git status:*)",
      "Bash(git diff:*)",
      "Bash(git log:*)",
      "Bash(git branch:*)",
      "Bash(git show:*)"
    ],
    "ask": [
      "Bash(git push:*)"
    ],
    "deny": [
      "Read(**/.env)",
      "Read(**/.env.*)",
      "Read(**/secrets.json)",
      "Read(**/*.pfx)",
      "Read(**/*.p12)",
      "Bash(dotnet ef database drop:*)"
    ]
  },
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Edit|MultiEdit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"$CLAUDE_PROJECT_DIR/.claude/hooks/dependency-rule.ps1\"",
            "timeout": 10
          },
          {
            "type": "command",
            "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"$CLAUDE_PROJECT_DIR/.claude/hooks/no-secrets.ps1\"",
            "timeout": 10
          }
        ]
      },
      {
        "matcher": "Edit|MultiEdit",
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"$CLAUDE_PROJECT_DIR/.claude/hooks/protect-migrations.ps1\"",
            "timeout": 10
          }
        ]
      }
    ],
    "PostToolUse": [
      {
        "matcher": "Edit|MultiEdit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"$CLAUDE_PROJECT_DIR/.claude/hooks/format-and-lint.ps1\"",
            "timeout": 60
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"$CLAUDE_PROJECT_DIR/.claude/hooks/verify-build.ps1\"",
            "timeout": 300
          }
        ]
      }
    ]
  }
}
```

- [ ] **Step 2: Write `.claude/settings.local.json.example`**

```json
{
  "_comment_1": "Copy to settings.local.json for personal overrides. That file is gitignored.",
  "_comment_2": "To disable the build gate that blocks turn end on a dirty build (spec D9),",
  "_comment_3": "set the Stop hook array to empty as shown below.",
  "hooks": {
    "Stop": []
  }
}
```

- [ ] **Step 3: Verify the JSON parses and every referenced hook exists**

Run:

```powershell
$settings = Get-Content -LiteralPath .claude\settings.json -Raw | ConvertFrom-Json
"permissions.allow entries: $($settings.permissions.allow.Count)"
$missing = 0
foreach ($file in @('dependency-rule','no-secrets','protect-migrations','format-and-lint','verify-build')) {
    $path = ".claude\hooks\$file.ps1"
    if (Test-Path -LiteralPath $path) { "  OK      $file.ps1" } else { "  MISSING $file.ps1"; $missing++ }
}
"missing: $missing"
Get-Content -LiteralPath .claude\settings.local.json.example -Raw | ConvertFrom-Json | Out-Null
"local example parses"
```

Expected: a non-zero allow count, `OK` for all five hooks, `missing: 0`, and `local example parses`.

- [ ] **Step 4: Commit**

```bash
git add .claude/settings.json .claude/settings.local.json.example
git commit -m "feat(claude): wire hooks and scope tool permissions"
```

---

## Task 10: The context layer

**Files:**
- Create: `CLAUDE.md`
- Create: `src/Domain/CLAUDE.md`
- Create: `src/Application/CLAUDE.md`
- Create: `src/Infrastructure/CLAUDE.md`
- Create: `src/Api/CLAUDE.md`
- Create: `frontend/CLAUDE.md`
- Create: `tests/CLAUDE.md`

**Interfaces:**
- Consumes: nothing executable
- Produces: the directory tree the `dependency-rule` hook's `/src/<Layer>/` pattern matches against

Creating these directories is what makes the layer paths real. No `.csproj`, no code.

- [ ] **Step 1: Write the root `CLAUDE.md`**

````markdown
# AIFrameWork

.NET backend + Angular frontend, Clean Architecture, single repo.

## Versions

<!-- PLACEHOLDER: no .NET SDK and no Node were installed when this repo was set up.
     Pin real versions here the moment the toolchain is installed. Do not guess. -->

| | Version |
|---|---|
| .NET SDK | _unpinned_ |
| Angular | _unpinned_ |
| Node | _unpinned_ |

## Layout

| Path | Contents |
|---|---|
| `src/Domain` | Entities, value objects, domain events, domain exceptions |
| `src/Application` | Use cases, ports, `Result<T>`, validators |
| `src/Infrastructure` | EF Core, repositories, external clients |
| `src/Api` | Controllers, DTOs, exception handling, composition root |
| `frontend` | Angular workspace |
| `tests` | Test projects, one per layer |

## The dependency rule

Dependencies point inward. This is enforced by `.claude/hooks/dependency-rule.ps1`,
which blocks the edit rather than warning about it.

| From ↓ / To → | Domain | Application | Infrastructure | Api |
|---|---|---|---|---|
| **Domain** | — | ✗ | ✗ | ✗ |
| **Application** | ✓ | — | ✗ | ✗ |
| **Infrastructure** | ✓ | ✓ | — | ✗ |
| **Api** | ✓ | ✓ | ✓ DI only | — |

`Domain` additionally may not reference `Microsoft.EntityFrameworkCore`,
`Microsoft.AspNetCore`, `Microsoft.Extensions.DependencyInjection`, `System.Data`,
or `System.ComponentModel.DataAnnotations`.

## Non-negotiables

- **Warnings are errors.** All three sources — compiler, analyzers, build — see
  `Directory.Build.props`. Fix diagnostics; do not suppress them without a justification comment.
- **Nullable is enabled.** A missing null check does not compile.
- **`required` keyword in `Domain`, never `[Required]`.** DataAnnotations belong on `Api` DTOs.
- **`catch (Exception)` only in the global handler.** `throw;`, never `throw ex;`.
- **Never hand-edit an applied EF migration.** Add a new one.
- **No secrets in `appsettings*.json`.** Use `dotnet user-secrets` or environment variables.

## Commands

| Command | Does |
|---|---|
| `/feature <name>` | Scaffold a feature across all four layers, with tests |
| `/ng-feature <name>` | Scaffold an Angular feature |
| `/verify` | Build, test, and lint both stacks |
| `/adr <title>` | Record an architecture decision |

## More context

Each layer has its own `CLAUDE.md`, loaded when you work in that directory.
Conventions live in the `dotnet-conventions`, `dotnet-testing`, `angular-conventions`,
and `angular-testing` skills.

Design rationale: `docs/superpowers/specs/2026-08-27-claude-framework-design.md`
````

- [ ] **Step 2: Write `src/Domain/CLAUDE.md`**

```markdown
# Domain

The innermost layer. It knows about the business and nothing else.

## Belongs here

Entities, value objects, enums, domain events, domain exceptions
(`DomainException` and its subtypes), and pure business rules.

## Never appears here

- `Microsoft.EntityFrameworkCore` — persistence is Infrastructure's problem
- `Microsoft.AspNetCore` — HTTP is Api's problem
- `Microsoft.Extensions.DependencyInjection`, `System.Data`
- `System.ComponentModel.DataAnnotations` — use the C# `required` keyword instead.
  `[Required]` drags validation and persistence concerns into this layer.
- Any `AiFramework.Application`, `.Infrastructure`, or `.Api` namespace
- `async` / `Task` — there is nothing to await in pure business logic

The dependency-rule hook blocks all of these at edit time.

## Shape

Properties are `required` and non-nullable, or genuinely optional and nullable —
never nullable-and-assumed-present. Setters are private; state changes go through
methods that enforce invariants. Collections are exposed as `IReadOnlyCollection<T>`
and initialised, never null.

## Tests

`tests/Domain.Tests`. Pure unit tests, no mocks, no fixtures, no I/O.
If a Domain test needs a mock, the logic is in the wrong layer.
```

- [ ] **Step 3: Write `src/Application/CLAUDE.md`**

```markdown
# Application

Use cases. Orchestrates Domain objects; owns no infrastructure.

## Belongs here

Use-case handlers, **ports** (the interfaces Infrastructure implements —
`IOrderRepository`, `IUnitOfWork`, `IClock`, `IEmailSender`), request and response
models, FluentValidation validators, and `Result<T>`.

## Never appears here

- `Microsoft.EntityFrameworkCore` — this layer depends on the port, not on EF
- `Microsoft.AspNetCore`
- Any `AiFramework.Infrastructure` or `.Api` namespace

Blocked by the dependency-rule hook.

## Error handling

Expected failures — not found, conflict, validation — return `Result<T>` rather
than throwing. Exceptions are for genuinely exceptional conditions. A handler that
returns `Result` gives the Api layer something to map to a status code without a
`try`/`catch`.

## Tests

`tests/Application.Tests`. Substitute the ports with NSubstitute. Assert on the
`Result` and on the calls made to the ports. Never touch a database.
```

- [ ] **Step 4: Write `src/Infrastructure/CLAUDE.md`**

```markdown
# Infrastructure

Implements the ports that Application declares. This is the only layer that knows EF exists.

## Belongs here

`DbContext`, `IEntityTypeConfiguration<T>` classes, repository implementations,
migrations, HTTP clients for external services, and the DI registration extension
this layer exposes to Api.

## Never appears here

Any `AiFramework.Api` namespace. Blocked by the dependency-rule hook.

## EF rules

- **All mapping lives in `IEntityTypeConfiguration<T>`.** Never annotate a Domain type.
  Constraints, lengths, indexes, and required-ness are configured here.
- `AsNoTracking()` on every read that is not followed by a write.
- Lazy loading stays off. Load related data with explicit `Include`, or project to a DTO.
- Project with `Select` before materialising — never `ToListAsync()` then filter in memory.
- Never call `SaveChangesAsync` inside a loop.
- Migrations are append-only. Never hand-edit one; the protect-migrations hook blocks it.

## Tests

`tests/Infrastructure.Tests`. Repository behaviour against a real database
(Testcontainers) or the in-memory provider for pure mapping checks. Prefer the
real engine wherever query translation matters — the in-memory provider does not
reproduce it faithfully.
```

- [ ] **Step 5: Write `src/Api/CLAUDE.md`**

```markdown
# Api

HTTP boundary and composition root.

## Belongs here

Controllers, request/response DTOs, FluentValidation validators for those DTOs,
the global `IExceptionHandler`, middleware, and DI wiring.

## Rules

- **Controllers are thin.** Bind, delegate to an Application handler, map the
  `Result` to a status code. No business logic, no EF queries, no `if` chains
  over domain state.
- **DTOs use `required` + `init`**, and are separate types from Domain entities.
  Never return an entity directly.
- `[Required]` and other DataAnnotations are legitimate **here** — this is the
  layer they belong to.
- **`Infrastructure` may only be referenced for DI registration** in the composition
  root. A controller reaching into a repository is a violation. This one is not
  hook-enforceable, so it is on you and on `dotnet-reviewer`.

## Exception handling

One `IExceptionHandler` maps the domain hierarchy to RFC 9457 `ProblemDetails`:

| Exception | Status |
|---|---|
| `ValidationException` | 400 |
| `NotFoundException` | 404 |
| `ConflictException` | 409 |
| anything else | 500, logged, message not leaked |

This is the **only** place `catch (Exception)` is permitted. CA1031 is an error everywhere else.

## Tests

`tests/Api.IntegrationTests`, via `WebApplicationFactory<Program>`. Assert status
codes, `ProblemDetails` shape, and validation responses.
```

- [ ] **Step 6: Write `frontend/CLAUDE.md`**

````markdown
# Frontend

Angular workspace.

## After `ng new`, apply this `tsconfig.json` delta

`ng new` generates this file, so these cannot be pre-written — apply them once:

```jsonc
{
  "compilerOptions": {
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "exactOptionalPropertyTypes": true,
    "noImplicitOverride": true,
    "noFallthroughCasesInSwitch": true,
    "noImplicitReturns": true
  },
  "angularCompilerOptions": {
    "strictTemplates": true,
    "strictInjectionParameters": true
  }
}
```

## Conventions

- **Standalone components only.** No `NgModule`.
- **`inject()`** over constructor injection.
- **Signals** for component state; `computed()` for derived state.
- **`ChangeDetectionStrategy.OnPush`** on every component.
- **`@if` / `@for` / `@switch`**, not `*ngIf` / `*ngFor`. `@for` needs `track`.
- **`takeUntilDestroyed()`** for subscription lifecycle. No manual `Subscription` fields,
  no `ngOnDestroy` bookkeeping.
- **Typed reactive forms.** Never `FormGroup<any>`.
- **Never swallow an error.** `catchError` must rethrow, return a typed failure, or
  surface the problem to the user — never `of(null)` to make a red line go away.

## Commands

| | |
|---|---|
| `npm start` | dev server |
| `npm run build` | production build |
| `npm test` | Vitest |
| `npm run lint` | `ng lint --max-warnings 0` |

Lint runs with `--max-warnings 0`: one warning is a failure.
````

- [ ] **Step 7: Write `tests/CLAUDE.md`**

```markdown
# Tests

One project per layer, mirroring `src/`.

| Project | Tests | Style |
|---|---|---|
| `Domain.Tests` | Entities, value objects, invariants | Pure. No mocks, no I/O. |
| `Application.Tests` | Use-case handlers | Ports substituted with NSubstitute |
| `Infrastructure.Tests` | Repositories, EF mapping | Real database via Testcontainers |
| `Api.IntegrationTests` | HTTP contract | `WebApplicationFactory<Program>` |

## Stack

xUnit + FluentAssertions + NSubstitute.

## Rules

- Name tests `MethodName_Scenario_ExpectedOutcome`.
- One behaviour per test. If the name needs "and", split it.
- Assert on behaviour, not on implementation detail.
- Never mock a type you do not own — wrap it in a port and substitute that.
- No `Thread.Sleep`. Inject an `IClock`.
- A test that needs `[Fact(Skip = ...)]` is either deleted or fixed.
```

- [ ] **Step 8: Verify every file landed and the hook pattern matches the tree**

Run:

```powershell
$expected = @(
  'CLAUDE.md',
  'src\Domain\CLAUDE.md','src\Application\CLAUDE.md',
  'src\Infrastructure\CLAUDE.md','src\Api\CLAUDE.md',
  'frontend\CLAUDE.md','tests\CLAUDE.md'
)
foreach ($file in $expected) {
    if (Test-Path -LiteralPath $file) { "  OK      $file" } else { "  MISSING $file" }
}
# The dependency-rule hook keys off "/src/<Layer>/" - confirm the tree produces that shape.
(Resolve-Path 'src\Domain').Path -replace '\\','/' -match '/src/Domain$'
```

Expected: `OK` for all seven, and `True` from the final line.

- [ ] **Step 9: Commit**

```bash
git add CLAUDE.md src/ frontend/CLAUDE.md tests/CLAUDE.md
git commit -m "docs(claude): add layered context files"
```

---

## Task 11: Frontend lint and format config

**Files:**
- Create: `frontend/eslint.config.js`
- Create: `.prettierrc`
- Create: `.prettierignore`

**Interfaces:**
- Consumes: nothing
- Produces: the config `format-and-lint.ps1` uses once `frontend/node_modules` exists

**Verification limit:** Node is not installed, so **these files cannot be executed or validated by a linter.** Verification is limited to file presence and a read-through. Do not claim the config was tested.

- [ ] **Step 1: Write `frontend/eslint.config.js`**

```javascript
// @ts-check
const eslint = require('@eslint/js');
const tseslint = require('typescript-eslint');
const angular = require('angular-eslint');

module.exports = tseslint.config(
  {
    files: ['**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.strictTypeChecked,
      ...tseslint.configs.stylisticTypeChecked,
      ...angular.configs.tsRecommended,
    ],
    languageOptions: {
      parserOptions: { projectService: true },
    },
    processor: angular.processInlineTemplates,
    rules: {
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'app', style: 'camelCase' },
      ],
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: 'app', style: 'kebab-case' },
      ],
      // Modern Angular, per frontend/CLAUDE.md
      '@angular-eslint/prefer-standalone': 'error',
      '@angular-eslint/prefer-on-push-component-change-detection': 'error',
      '@angular-eslint/use-lifecycle-interface': 'error',
      '@angular-eslint/no-empty-lifecycle-method': 'error',

      // Type safety
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/no-non-null-assertion': 'error',
      '@typescript-eslint/explicit-function-return-type': [
        'error',
        { allowExpressions: true },
      ],
      '@typescript-eslint/no-floating-promises': 'error',
      '@typescript-eslint/no-misused-promises': 'error',

      // Never silently swallow a failure - see frontend/CLAUDE.md
      'no-empty': ['error', { allowEmptyCatch: false }],
    },
  },
  {
    files: ['**/*.html'],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
    rules: {
      '@angular-eslint/template/prefer-control-flow': 'error',
      '@angular-eslint/template/no-any': 'error',
    },
  },
  {
    files: ['**/*.spec.ts'],
    rules: {
      '@typescript-eslint/no-non-null-assertion': 'off',
    },
  },
);
```

- [ ] **Step 2: Write `.prettierrc`**

```json
{
  "printWidth": 100,
  "singleQuote": true,
  "trailingComma": "all",
  "semi": true,
  "arrowParens": "always",
  "endOfLine": "lf",
  "overrides": [
    {
      "files": "*.html",
      "options": { "parser": "angular" }
    }
  ]
}
```

- [ ] **Step 3: Write `.prettierignore`**

```gitignore
node_modules/
dist/
.angular/
coverage/
**/bin/
**/obj/
*.min.js
package-lock.json
```

- [ ] **Step 4: Verify presence**

Run:

```powershell
foreach ($file in @('frontend\eslint.config.js','.prettierrc','.prettierignore')) {
    if (Test-Path -LiteralPath $file) { "  OK      $file" } else { "  MISSING $file" }
}
Get-Content -LiteralPath .prettierrc -Raw | ConvertFrom-Json | Out-Null
"prettierrc parses as JSON"
```

Expected: `OK` for all three, and `prettierrc parses as JSON`. `eslint.config.js` **cannot be validated** without Node — note that in the commit and move on.

- [ ] **Step 5: Commit**

```bash
git add frontend/eslint.config.js .prettierrc .prettierignore
git commit -m "chore(frontend): add eslint flat config and prettier rules

eslint.config.js is unvalidated - Node is not installed on this machine."
```

---

## Task 12: Review agents

**Files:**
- Create: `.claude/agents/dotnet-reviewer.md`
- Create: `.claude/agents/angular-reviewer.md`
- Create: `.claude/agents/test-runner.md`

**Interfaces:**
- Consumes: the conventions in the Task 10 context files
- Produces: three agents callable by name

- [ ] **Step 1: Write `.claude/agents/dotnet-reviewer.md`**

````markdown
---
name: dotnet-reviewer
description: Reviews C# changes against this repo's Clean Architecture, nullability, and exception-handling rules. Use after implementing or modifying backend code, before committing.
tools: Read, Grep, Glob, Bash
---

You review C# in a Clean Architecture repo. Report findings; do not edit files.

Read `CLAUDE.md` and the `CLAUDE.md` of each layer you are reviewing before you start.

## Check, in priority order

1. **Dependency rule.** `Domain` referencing anything outward; `Application` referencing
   `Infrastructure`, `Api`, or EF Core; `Infrastructure` referencing `Api`; a controller
   using a repository directly instead of an Application handler. That last one is not
   hook-enforceable, so it is specifically your job.
2. **The annotation trap.** `[Required]`, `[MaxLength]`, or any `DataAnnotations` on a
   Domain type. Domain uses the C# `required` keyword; mapping belongs in
   `IEntityTypeConfiguration<T>`.
3. **Nullability.** `!` used without a justifying comment. Nullable reference types
   assumed non-null. Collections left null instead of empty.
4. **Exception handling.** `catch (Exception)` outside the global `IExceptionHandler`.
   `throw ex;` instead of `throw;`. Empty catch blocks. Catch-log-continue that hides a
   failure from the caller. Expected failures thrown as exceptions where `Result<T>` fits.
5. **EF pitfalls.** Missing `AsNoTracking()` on reads. N+1 from lazy access in a loop.
   `SaveChangesAsync` inside a loop. Filtering in memory after `ToListAsync()`. A hand-edited
   migration.
6. **Async.** `async void` outside event handlers. `.Result` or `.Wait()`. Sync I/O in an
   async method. Missing `CancellationToken` on a method that does I/O.
7. **Tests.** New behaviour with no test. A test asserting implementation rather than behaviour.

## Output

Group findings by severity — **Blocking**, **Should fix**, **Consider**. For each:
`file:line`, one sentence on what is wrong, and the concrete fix. If a category is clean,
say so in one line rather than padding. Cite the rule from `CLAUDE.md` you are applying.
````

- [ ] **Step 2: Write `.claude/agents/angular-reviewer.md`**

````markdown
---
name: angular-reviewer
description: Reviews Angular and TypeScript changes against this repo's modern-Angular conventions. Use after implementing or modifying frontend code, before committing.
tools: Read, Grep, Glob, Bash
---

You review Angular code. Report findings; do not edit files.

Read `frontend/CLAUDE.md` before you start.

## Check, in priority order

1. **Subscription leaks.** A `.subscribe()` with no `takeUntilDestroyed()` and no `async`
   pipe. Manual `Subscription` fields and `ngOnDestroy` bookkeeping where
   `takeUntilDestroyed()` belongs.
2. **Swallowed errors.** `catchError(() => of(null))` or any handler that discards a failure
   without rethrowing, returning a typed failure, or surfacing it to the user.
3. **Change detection.** A component without `ChangeDetectionStrategy.OnPush`. Mutation of
   an object instead of replacement. `signal.set` with the same reference.
4. **Modern Angular.** `NgModule` instead of standalone. Constructor injection instead of
   `inject()`. `*ngIf` / `*ngFor` instead of `@if` / `@for`. `@for` without `track`.
5. **Type safety.** `any`. Non-null `!`. `FormGroup<any>` or untyped form controls.
   A component input without an explicit type.
6. **Templates.** Missing a11y attributes on interactive elements. Logic in a template that
   belongs in a `computed()`.
7. **Tests.** New component or service with no spec.

## Output

Group findings by severity — **Blocking**, **Should fix**, **Consider**. For each:
`file:line`, one sentence on what is wrong, and the concrete fix. If a category is clean,
say so in one line. Cite the rule from `frontend/CLAUDE.md` you are applying.
````

- [ ] **Step 3: Write `.claude/agents/test-runner.md`**

````markdown
---
name: test-runner
description: Runs the backend and frontend test suites and reports parsed failures. Use when you need test results without build logs filling the conversation.
tools: Read, Grep, Glob, Bash
---

You run tests and report what failed. You do not fix anything.

## Procedure

1. Check what is installed before running anything:
   - `dotnet --list-sdks` — if this prints nothing, there is **no .NET SDK**. Say so and skip
     the backend. Do not report this as a test failure.
   - `node --version` — if this fails, **Node is not installed**. Say so and skip the frontend.
2. Backend, if an SDK and a `.sln` exist: `dotnet test --nologo --verbosity quiet`
3. Frontend, if `frontend/node_modules` exists: `npm test --prefix frontend -- --run`

## Output

Lead with one line: `N passed, M failed, K skipped` per suite, or the reason a suite was skipped.

Then, for each failure only:
- test name
- the assertion message
- the `file:line` it points at
- one sentence on the likely cause

**Never paste raw build or test logs into your report.** Keeping them out of the main context
is the entire reason you exist. If a suite fails to build, report the first three compiler
errors with file and line, not the whole output.
````

- [ ] **Step 4: Verify frontmatter parses on all three**

Run:

```powershell
foreach ($file in Get-ChildItem .claude\agents\*.md) {
    $lines = Get-Content -LiteralPath $file.FullName
    $hasOpen = $lines[0] -eq '---'
    $nameLine = $lines | Where-Object { $_ -match '^name:\s*\S' } | Select-Object -First 1
    $descLine = $lines | Where-Object { $_ -match '^description:\s*\S' } | Select-Object -First 1
    "{0}: frontmatter={1} name={2} desc={3}" -f $file.Name, $hasOpen, [bool]$nameLine, [bool]$descLine
}
```

Expected: three lines, each `frontmatter=True name=True desc=True`.

- [ ] **Step 5: Commit**

```bash
git add .claude/agents/
git commit -m "feat(claude): add dotnet, angular, and test-runner agents"
```

---

## Task 13: Slash commands

**Files:**
- Create: `.claude/commands/feature.md`
- Create: `.claude/commands/ng-feature.md`
- Create: `.claude/commands/verify.md`
- Create: `.claude/commands/adr.md`

**Interfaces:**
- Consumes: the Task 10 context files and Task 12 agents
- Produces: `/feature`, `/ng-feature`, `/verify`, `/adr`

- [ ] **Step 1: Write `.claude/commands/feature.md`**

````markdown
---
description: Scaffold a backend feature across all four Clean Architecture layers, with tests
argument-hint: <FeatureName>
---

Implement the feature `$ARGUMENTS` across every layer, in dependency order.

Working outward in this order is what stops Clean Architecture degrading into one layer
edited and the other three forgotten. Do not skip ahead.

Read `CLAUDE.md` plus the `CLAUDE.md` of each layer as you reach it.

## 1. Domain — `src/Domain/`

Entity or value object, invariants enforced in methods, private setters, `required`
properties. Add a domain exception if the feature has a failure mode the domain owns.
**No** DataAnnotations, no EF, no async.

## 2. Application — `src/Application/`

- Request and response models
- The port(s) this use case needs, if they do not exist yet — interfaces only
- The handler, returning `Result<T>`
- A FluentValidation validator for the request

Reference `Domain` only.

## 3. Infrastructure — `src/Infrastructure/`

- `IEntityTypeConfiguration<T>` for any new entity — all mapping, lengths, indexes here
- The port implementation
- **Do not hand-write a migration.** Tell the user the exact `dotnet ef migrations add`
  command to run.

## 4. Api — `src/Api/`

- Request/response DTOs, `required` + `init`, distinct from Domain types
- A thin controller action: bind, delegate, map `Result` to a status code
- DI registration if a new port was added

## 5. Tests

Write these as you go, not at the end:

| Project | Covers |
|---|---|
| `tests/Domain.Tests` | invariants, pure logic — no mocks |
| `tests/Application.Tests` | the handler, ports substituted with NSubstitute |
| `tests/Api.IntegrationTests` | the endpoint via `WebApplicationFactory` |

## 6. Finish

Run `/verify`. Then dispatch the `dotnet-reviewer` agent over the diff.

If any step is blocked — missing SDK, an unclear requirement — stop and say so rather than
inventing a shape for the layers below it.
````

- [ ] **Step 2: Write `.claude/commands/ng-feature.md`**

````markdown
---
description: Scaffold an Angular feature - routed standalone component, service, model, specs
argument-hint: <feature-name>
---

Create the Angular feature `$ARGUMENTS` under `frontend/src/app/`.

Read `frontend/CLAUDE.md` first and follow it exactly.

## Files

```
frontend/src/app/<feature-name>/
  <feature-name>.component.ts        standalone, OnPush, inject(), signals
  <feature-name>.component.html      @if / @for with track
  <feature-name>.component.scss
  <feature-name>.component.spec.ts
  <feature-name>.service.ts          typed HttpClient calls, typed error handling
  <feature-name>.service.spec.ts
  <feature-name>.model.ts            interfaces matching the Api DTOs
```

## Requirements

- Standalone component, `ChangeDetectionStrategy.OnPush`, dependencies via `inject()`
- State in signals; derived state in `computed()`
- Every subscription uses `takeUntilDestroyed()`, or the template uses the `async` pipe
- `catchError` returns a typed failure or rethrows — it never returns `of(null)`
- Register the route in the app routes with `loadComponent` for lazy loading
- No `any`, no `!`

## Finish

Run `npm run lint --prefix frontend`, then dispatch the `angular-reviewer` agent over the diff.

If Node is not installed, say so plainly and skip the lint step — do not report it as passing.
````

- [ ] **Step 3: Write `.claude/commands/verify.md`**

````markdown
---
description: Build, test, and lint both stacks and report what actually ran
---

Verify the repo. **Check each toolchain before using it**, and report honestly on anything
that could not run — a skipped step is never reported as a pass.

## 1. Backend

```
dotnet --list-sdks
```

Prints nothing? There is no .NET SDK. Say so, skip to step 2, do not report a failure.

Otherwise:

```
dotnet build --nologo --verbosity quiet
dotnet test --nologo --verbosity quiet
```

Warnings are errors here, so a warning is a build failure. Report every diagnostic with
`file:line`.

## 2. Frontend

```
node --version
```

Fails? Node is not installed. Say so and skip to step 3.

`frontend/node_modules` missing? Say it needs `npm ci --prefix frontend` and skip.

Otherwise:

```
npm run lint --prefix frontend
npm run build --prefix frontend
npm test --prefix frontend -- --run
```

`ng lint` runs with `--max-warnings 0`: one warning is a failure.

## 3. Hooks

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/hooks/tests/run-hook-tests.ps1
```

This one always runs — it has no toolchain dependency.

## Report

A table: step, ran or skipped, result. Then the failures with `file:line`. State plainly which
steps were skipped and why. Never summarise a skipped step as passing.
````

- [ ] **Step 4: Write `.claude/commands/adr.md`**

````markdown
---
description: Record an architecture decision in docs/adr/
argument-hint: <short title>
---

Write an ADR for: `$ARGUMENTS`

1. Find the highest-numbered file in `docs/adr/` and use the next number, zero-padded to four
   digits.
2. Create `docs/adr/NNNN-<kebab-case-title>.md`:

```markdown
# NNNN. <Title>

**Date:** YYYY-MM-DD
**Status:** Accepted

## Context

What forced this decision. The constraints that were real at the time.

## Decision

What was decided, in the active voice.

## Consequences

What this makes easy, what it makes hard, and what it rules out.
Include the costs — an ADR that lists only benefits is not being honest.

## Alternatives considered

Each option, and the specific reason it lost.
```

Fill every section from the conversation. If a section cannot be filled because the
information was never discussed, ask rather than inventing a rationale.
````

- [ ] **Step 5: Verify all four have frontmatter with a description**

Run:

```powershell
foreach ($file in Get-ChildItem .claude\commands\*.md) {
    $lines = Get-Content -LiteralPath $file.FullName
    $descLine = $lines | Where-Object { $_ -match '^description:\s*\S' } | Select-Object -First 1
    "{0}: frontmatter={1} desc={2}" -f $file.Name, ($lines[0] -eq '---'), [bool]$descLine
}
```

Expected: four lines, each `frontmatter=True desc=True`.

- [ ] **Step 6: Commit**

```bash
git add .claude/commands/
git commit -m "feat(claude): add feature, ng-feature, verify, and adr commands"
```

---

## Task 14: Convention skills

**Files:**
- Create: `.claude/skills/dotnet-conventions/SKILL.md`
- Create: `.claude/skills/dotnet-testing/SKILL.md`
- Create: `.claude/skills/angular-conventions/SKILL.md`
- Create: `.claude/skills/angular-testing/SKILL.md`

**Interfaces:**
- Consumes: nothing
- Produces: four skills invocable by name

These describe **what good code looks like in this repo**. Superpowers describes **how to work**. Do not duplicate Superpowers here — two sources of truth on process is worse than one.

- [ ] **Step 1: Write `.claude/skills/dotnet-conventions/SKILL.md`**

````markdown
---
name: dotnet-conventions
description: Use when writing or modifying C# in this repo - covers nullability, required-ness, the DataAnnotations trap, exception handling, and EF Core patterns.
---

# .NET Conventions

Warnings are errors here (`Directory.Build.props`). Most of this is enforced by the compiler;
the rest is enforced by review.

## Nullability

Nullable reference types are on and every nullable warning is a build error.

- A parameter that may be null is typed `T?`. One that may not is typed `T` — and callers
  are then free to rely on that.
- `!` requires an adjacent comment stating why null is impossible. No comment, no `!`.
- Guard public entry points: `ArgumentNullException.ThrowIfNull(order);`
  `ArgumentException.ThrowIfNullOrWhiteSpace(name);`
- Collections are initialised, never null. Absence is `[]`.

## Required-ness — and where annotations belong

This is the easiest rule in the repo to get backwards.

| Layer | Use | Never |
|---|---|---|
| `Domain` | C# `required` keyword, non-nullable types, private setters | `[Required]`, `[MaxLength]`, any DataAnnotations |
| `Api` DTOs | `required` + `init`, FluentValidation | business logic |
| `Infrastructure` | `IEntityTypeConfiguration<T>` for constraints | annotations on Domain types |

```csharp
// Domain - correct
public sealed class Order
{
    public required OrderId Id { get; init; }
    public required CustomerId CustomerId { get; init; }
    private readonly List<OrderLine> _lines = [];
    public IReadOnlyCollection<OrderLine> Lines => _lines.AsReadOnly();
}

// Domain - wrong: DataAnnotations drag persistence and validation into the core
public sealed class Order
{
    [Required, MaxLength(50)]
    public string Reference { get; set; } = string.Empty;
}
```

The dependency-rule hook blocks `System.ComponentModel.DataAnnotations` in `Domain`.

## Exception handling

Hierarchy:

```csharp
public abstract class DomainException(string message) : Exception(message);
public sealed class NotFoundException(string message)   : DomainException(message);
public sealed class ConflictException(string message)   : DomainException(message);
public sealed class ValidationException(string message) : DomainException(message);
```

Rules, all analyzer-enforced as errors:

- `catch (Exception)` **only** in the global `IExceptionHandler`. CA1031 is an error elsewhere.
- `throw;` never `throw ex;` — the second one erases the stack trace. CA2200.
- No empty catch blocks. No catch-log-continue that leaves the caller believing it succeeded.
- Expected failures return `Result<T>`; exceptions are for the genuinely exceptional. "Order
  not found" during a lookup is expected. A database being unreachable is not.

At the Api boundary, one `IExceptionHandler` maps the hierarchy to RFC 9457 `ProblemDetails`:
`ValidationException` → 400, `NotFoundException` → 404, `ConflictException` → 409, everything
else → 500, logged, message not leaked to the client.

## EF Core

- Mapping lives in `IEntityTypeConfiguration<T>`. Never annotate a Domain type.
- `AsNoTracking()` on every read not followed by a write.
- Lazy loading off. Explicit `Include`, or project to a DTO with `Select`.
- Project before materialising. Never `ToListAsync()` then filter in memory.
- Never `SaveChangesAsync` in a loop.
- Migrations are append-only. Add a new one; the protect-migrations hook blocks edits.

## Async

- `CancellationToken` on every I/O method, and pass it down.
- No `async void` outside event handlers. No `.Result`, no `.Wait()`.
- Do not add `ConfigureAwait(false)` — ASP.NET Core has no synchronization context, and
  CA2007 is deliberately off.
````

- [ ] **Step 2: Write `.claude/skills/dotnet-testing/SKILL.md`**

````markdown
---
name: dotnet-testing
description: Use when writing or modifying .NET tests in this repo - xUnit, FluentAssertions, NSubstitute, and what belongs at each layer.
---

# .NET Testing

Stack: xUnit + FluentAssertions + NSubstitute.

## What to test where

| Project | Tests | Never |
|---|---|---|
| `Domain.Tests` | invariants, value objects, pure logic | mocks, I/O, fixtures |
| `Application.Tests` | handlers with ports substituted | real database |
| `Infrastructure.Tests` | repositories, EF mapping, query translation | business rules |
| `Api.IntegrationTests` | HTTP contract, status codes, `ProblemDetails` | unit-level logic |

If a `Domain` test needs a mock, the logic is in the wrong layer. That is the signal, not an
inconvenience to work around.

## Shape

```csharp
[Fact]
public void Cancel_WhenAlreadyShipped_ThrowsConflict()
{
    var order = OrderBuilder.Shipped();

    var act = () => order.Cancel();

    act.Should().Throw<ConflictException>()
       .WithMessage("*already shipped*");
}
```

- Name: `MethodName_Scenario_ExpectedOutcome`.
- Arrange, act, assert — separated by blank lines, no comment headers.
- One behaviour per test. If the name needs "and", split it.
- `[Theory]` with `[InlineData]` for the same behaviour over several inputs; not for several
  behaviours.

## Rules

- Assert on behaviour, not implementation. A test that breaks on a rename but not on a bug is
  a liability.
- Never mock a type you do not own. Wrap it in a port and substitute that.
- No `Thread.Sleep`. Inject an `IClock`.
- No shared mutable state between tests. Build fresh data per test.
- A skipped test is deleted or fixed. `[Fact(Skip = ...)]` is not a resting state.

## Integration tests

`WebApplicationFactory<Program>` for the Api. Testcontainers for a real database where query
translation matters — the in-memory provider does not reproduce it faithfully, and a test that
passes against it can still fail in production.
````

- [ ] **Step 3: Write `.claude/skills/angular-conventions/SKILL.md`**

````markdown
---
name: angular-conventions
description: Use when writing or modifying Angular or TypeScript in this repo - standalone components, signals, OnPush, control flow, subscription lifecycle, and error handling.
---

# Angular Conventions

Lint runs with `--max-warnings 0`: one warning fails the build.

## Components

Standalone only. `OnPush` always. Dependencies via `inject()`.

```typescript
@Component({
  selector: 'app-orders',
  templateUrl: './orders.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OrdersComponent {
  private readonly service = inject(OrdersService);

  readonly orders = signal<readonly Order[]>([]);
  readonly openCount = computed(() => this.orders().filter((o) => o.isOpen).length);
}
```

- State in `signal()`, derived state in `computed()`. Never recompute in the template.
- Signals hold immutable values. Replace the array; do not mutate it — `OnPush` will not see
  a mutation.

## Templates

`@if` / `@for` / `@switch`. `@for` requires `track`.

```html
@if (orders().length > 0) {
  @for (order of orders(); track order.id) {
    <app-order-row [order]="order" />
  }
} @else {
  <p>No orders.</p>
}
```

## Subscriptions

Prefer the `async` pipe. Where you must subscribe, use `takeUntilDestroyed()`. Manual
`Subscription` fields and `ngOnDestroy` bookkeeping are a leak waiting to happen.

```typescript
this.service.watch()
  .pipe(takeUntilDestroyed())
  .subscribe((value) => this.orders.set(value));
```

## Error handling

Never swallow a failure.

```typescript
// Wrong - the user sees nothing and the bug is invisible
catchError(() => of(null))

// Right - surface it, typed
catchError((error: HttpErrorResponse) => {
  this.error.set(toUserMessage(error));
  return EMPTY;
})
```

A global `ErrorHandler` and a typed HTTP interceptor cover what components do not.

## Types

- No `any`. No `!`.
- Typed reactive forms — never `FormGroup<any>`.
- Explicit return types on functions.
- Models mirror the Api DTOs; keep them in `<feature>.model.ts`.
````

- [ ] **Step 4: Write `.claude/skills/angular-testing/SKILL.md`**

````markdown
---
name: angular-testing
description: Use when writing or modifying Angular tests in this repo - Vitest, component testing, HttpClient mocking, and what not to test.
---

# Angular Testing

Runner: Vitest. Karma is deprecated and is not used here.

## Components

Test behaviour through the rendered output, not through the class instance.

```typescript
describe('OrdersComponent', () => {
  it('renders one row per order', async () => {
    await TestBed.configureTestingModule({
      imports: [OrdersComponent],
      providers: [
        provideHttpClientTesting(),
        { provide: OrdersService, useValue: { watch: () => of([anOrder(), anOrder()]) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(OrdersComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('app-order-row')).toHaveLength(2);
  });
});
```

## Services

`provideHttpClientTesting()` with `HttpTestingController`. Assert the request method, URL, and
body, then flush a response. Always call `httpTesting.verify()` in `afterEach`.

## Rules

- Test what the user experiences: rendered output, emitted events, requests made.
- Do not test framework behaviour — that `@Input` assignment works is Angular's test, not yours.
- Do not assert on private fields or call private methods.
- One behaviour per `it`. The name completes the sentence "it ...".
- Prefer a fake object over a mocking framework for a service you own.
- No arbitrary timeouts. Await the fixture's stability instead.

## What not to test

Getters that only return a field. `computed()` that only reads one signal. Template bindings
with no logic. These cost maintenance and catch nothing.
````

- [ ] **Step 5: Verify all four skills have valid frontmatter**

Run:

```powershell
foreach ($dir in Get-ChildItem .claude\skills -Directory) {
    $skill = Join-Path $dir.FullName 'SKILL.md'
    if (-not (Test-Path -LiteralPath $skill)) { "  MISSING $($dir.Name)/SKILL.md"; continue }
    $lines = Get-Content -LiteralPath $skill
    $nameLine = $lines | Where-Object { $_ -match '^name:\s*\S' } | Select-Object -First 1
    $descLine = $lines | Where-Object { $_ -match '^description:\s*\S' } | Select-Object -First 1
    $nameValue = ($nameLine -replace '^name:\s*','').Trim()
    "{0}: frontmatter={1} desc={2} name-matches-dir={3}" -f `
        $dir.Name, ($lines[0] -eq '---'), [bool]$descLine, ($nameValue -eq $dir.Name)
}
```

Expected: four lines, each `frontmatter=True desc=True name-matches-dir=True`.

- [ ] **Step 6: Commit**

```bash
git add .claude/skills/
git commit -m "feat(claude): add dotnet and angular convention skills"
```

---

## Task 15: ADR seed and full verification

**Files:**
- Create: `docs/adr/0001-record-architecture-decisions.md`
- Create: `docs/adr/0002-clean-architecture-with-enforced-dependency-rule.md`

**Interfaces:**
- Consumes: everything above
- Produces: the final verified state

- [ ] **Step 1: Write `docs/adr/0001-record-architecture-decisions.md`**

```markdown
# 0001. Record architecture decisions

**Date:** 2026-08-27
**Status:** Accepted

## Context

Architectural choices made early in a project get forgotten, and are then either re-litigated
or violated by people who never knew the reasoning. A greenfield repo is the cheapest possible
moment to start recording them.

## Decision

Significant architectural decisions are recorded as ADRs in `docs/adr/`, numbered sequentially
and never rewritten once accepted. A decision that is later reversed gets a new ADR that
supersedes the old one; the old one stays in place with its status updated.

Use `/adr <title>` to create one.

## Consequences

Anyone reading the repo can reconstruct why it looks the way it does, including the options
that lost. The cost is the discipline to write one at the time rather than afterwards, and
ADRs written late are usually rationalisations rather than records.

## Alternatives considered

**A wiki or Confluence page.** Drifts from the code and needs separate access. ADRs are
reviewed in the same pull request as the change they describe.

**Nothing.** The default. It works until the second developer arrives, or until the first one
returns after six months.
```

- [ ] **Step 2: Write `docs/adr/0002-clean-architecture-with-enforced-dependency-rule.md`**

```markdown
# 0002. Clean Architecture with a machine-enforced dependency rule

**Date:** 2026-08-27
**Status:** Accepted

## Context

The backend needed a house style that both people and coding agents could follow. Clean
Architecture's boundaries are well understood, but a single feature spans four projects, so
edits fan out and the dependency rule is easy to break one `using` at a time. Documented
conventions are honoured only as long as they are remembered.

## Decision

Clean Architecture, with `Domain`, `Application`, `Infrastructure`, and `Api`.

The dependency rule is enforced by a PreToolUse hook that blocks the edit
(`.claude/hooks/dependency-rule.ps1`), not by documentation. Per-layer `CLAUDE.md` files put
each layer's rules in front of an agent at the moment it opens that directory. Warnings are
errors from all three sources — compiler, analyzers, and build — so nullability and exception
antipatterns fail the build rather than accumulating.

## Consequences

The rule cannot rot: a violating edit is rejected with an explanation rather than reviewed
later. Warnings cannot accumulate, because there is no warning state to accumulate in.

The costs are real. Four projects per feature is more ceremony than a layered app needs at
small scale. Warnings-as-errors means an SDK or analyzer upgrade can break the build on code
nobody touched. And one row of the matrix — `Api` → `Infrastructure`, legal for DI
registration only — cannot be checked by a hook, because nothing distinguishes a
`services.AddScoped<>()` call from a controller reaching into a repository. That row is
carried by review.

## Alternatives considered

**Vertical Slice with Minimal APIs.** A tighter blob of context per feature and fewer files
per change, which suits agent work better. Lost because the team wanted the more familiar and
widely documented structure.

**Layered controllers/services.** Lowest ceremony, but the least structural guidance and the
fastest to degrade as the app grows.

**Documenting the dependency rule without enforcing it.** Rejected: this is precisely the rule
that gets broken silently, one `using` at a time.
```

- [ ] **Step 3: Run the full hook suite**

Run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\tests\run-hook-tests.ps1
```

Expected: `Passed: 32   Failed: 0`, exit code 0.

- [ ] **Step 4: Verify every file the plan promised exists**

Run:

```powershell
$expected = @(
  'CLAUDE.md','.gitignore','.gitattributes','Directory.Build.props','.editorconfig',
  '.prettierrc','.prettierignore',
  '.claude/settings.json','.claude/settings.local.json.example',
  '.claude/hooks/hooks.config.json','.claude/hooks/lib/payload.ps1',
  '.claude/hooks/dependency-rule.ps1','.claude/hooks/no-secrets.ps1',
  '.claude/hooks/protect-migrations.ps1','.claude/hooks/format-and-lint.ps1',
  '.claude/hooks/verify-build.ps1','.claude/hooks/tests/run-hook-tests.ps1',
  '.claude/agents/dotnet-reviewer.md','.claude/agents/angular-reviewer.md',
  '.claude/agents/test-runner.md',
  '.claude/commands/feature.md','.claude/commands/ng-feature.md',
  '.claude/commands/verify.md','.claude/commands/adr.md',
  '.claude/skills/dotnet-conventions/SKILL.md','.claude/skills/dotnet-testing/SKILL.md',
  '.claude/skills/angular-conventions/SKILL.md','.claude/skills/angular-testing/SKILL.md',
  'src/Domain/CLAUDE.md','src/Application/CLAUDE.md','src/Infrastructure/CLAUDE.md',
  'src/Api/CLAUDE.md','frontend/CLAUDE.md','frontend/eslint.config.js','tests/CLAUDE.md',
  'docs/adr/0001-record-architecture-decisions.md',
  'docs/adr/0002-clean-architecture-with-enforced-dependency-rule.md'
)
$missing = @($expected | Where-Object { -not (Test-Path -LiteralPath $_) })
"expected: $($expected.Count)   missing: $($missing.Count)"
$missing | ForEach-Object { "  MISSING $_" }
```

Expected: `expected: 37   missing: 0`.

- [ ] **Step 5: Confirm the working tree is clean and `.idea/` is ignored**

Run:

```bash
git status --short
```

Expected: empty output. In particular, no `?? .idea/`.

- [ ] **Step 6: Commit**

```bash
git add docs/adr/
git commit -m "docs: seed ADR log with the architecture decision"
```

---

## Verification summary

What this plan can and cannot prove, stated plainly so no task overclaims:

| Component | Verification | Real? |
|---|---|---|
| Five hook scripts | 30 fixture assertions, exit codes | **Yes — executes on this machine** |
| `payload.ps1` | 7 direct assertions | **Yes** |
| `settings.json` | JSON parses; every referenced hook exists | Yes, structural |
| `Directory.Build.props` | XML parses | Structural only — **no SDK to build with** |
| `.editorconfig` | CA1031 elevation present | Structural only |
| `eslint.config.js` | file present | **Unvalidated — no Node** |
| `CLAUDE.md`, agents, commands, skills | frontmatter parses, files present | Structural only |

The hooks are genuinely tested. Everything else is unexecuted until the toolchain is installed
and the solution is scaffolded. Do not report otherwise.

## Self-review notes

Checked after writing:

- **Spec coverage.** Every section of the spec maps to a task: §6.1 → Task 4; §6.2 → Task 2;
  §6.3 → Tasks 10, 14; §6.4 → Tasks 2, 10, 14; §6.5 → Tasks 10, 11, 14; §6.6 → Task 5;
  §6.7 → Task 6; §7 → Tasks 3–8; §8 → Task 9; §9 → Task 10; §10 → Task 12; §11 → Task 13;
  §12 → Task 14; §13 → Tasks 3–8, 15.
- **Type consistency.** The six `payload.ps1` function names produced in Task 3 are used with
  identical signatures in Tasks 4–8. `Assert-Exit` and `Invoke-Hook` keep the same parameter
  names throughout. Fixture filenames match between the create step and the assertion step in
  every hook task.
- **Running assertion counts** are cumulative and consistent: 7 → 13 → 19 → 23 → 27 → 30.
- **Gap found and closed while reviewing:** `.gitignore` was originally scheduled after the
  hook tasks, which would have left `.idea/` dirtying `git status` through every intermediate
  commit. It moved to Task 1.


