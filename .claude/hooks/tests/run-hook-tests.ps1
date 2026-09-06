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
        # Name of a fixture under tests/fixtures/. Used by every caller except
        # the format-and-lint real-file assertions below, which need a fixture
        # that lives outside the repo and pass -FixturePath instead.
        [string]$Fixture,
        [string]$FixturePath
    )
    $hookPath = Join-Path $HookDir $Script
    if (-not [string]::IsNullOrWhiteSpace($FixturePath)) {
        $resolvedFixturePath = $FixturePath
    } else {
        $resolvedFixturePath = Join-Path $Fixtures $Fixture
    }
    if (-not (Test-Path -LiteralPath $hookPath))            { return -100 }
    if (-not (Test-Path -LiteralPath $resolvedFixturePath)) { return -101 }

    $json = Get-Content -LiteralPath $resolvedFixturePath -Raw
    $json | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hookPath | Out-Null
    return $LASTEXITCODE
}

function Assert-Exit {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Script,
        [string]$Fixture,
        [string]$FixturePath,
        [Parameter(Mandatory)][int]$Expected
    )
    $actual = Invoke-Hook -Script $Script -Fixture $Fixture -FixturePath $FixturePath
    if ($actual -eq $Expected) {
        Write-Host "  PASS  $Name"
        $script:Passed++
    } else {
        Write-Host "  FAIL  $Name -- expected exit $Expected, got $actual" -ForegroundColor Red
        $script:Failed++
    }
}

function Assert-True {
    # $Condition is deliberately untyped rather than [bool]: a strictly-typed
    # [bool] parameter throws a terminating type-conversion error (instead of
    # failing the assertion) when a caller's expression evaluates to $null or
    # an array under Set-StrictMode - e.g. ($x -match 'pattern') when $x is
    # $null returns an empty System.Object[], not $false. Using PowerShell's
    # own truthiness in the `if` below turns that crash into an honest FAIL.
    # This is a defensive backstop, not a fix for any specific caller - see
    # the stop-normal.json fixture rework below for the actual bug this
    # papered over the first time.
    param(
        [Parameter(Mandatory)][string]$Name,
        $Condition
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
Write-Host 'dependency-rule.ps1'
Assert-Exit -Name 'Domain + EF Core is blocked'            -Script 'dependency-rule.ps1' -Fixture 'domain-ef-violation.json'                 -Expected 2
Assert-Exit -Name 'Domain + DataAnnotations is blocked'    -Script 'dependency-rule.ps1' -Fixture 'domain-annotations-violation.json'        -Expected 2
Assert-Exit -Name 'Domain with only System is allowed'     -Script 'dependency-rule.ps1' -Fixture 'domain-clean.json'                        -Expected 0
Assert-Exit -Name 'Application + Infrastructure blocked'   -Script 'dependency-rule.ps1' -Fixture 'application-infrastructure-violation.json' -Expected 2
Assert-Exit -Name 'Api may reference every layer'          -Script 'dependency-rule.ps1' -Fixture 'api-all-layers.json'                      -Expected 0
Assert-Exit -Name 'Malformed payload fails open'           -Script 'dependency-rule.ps1' -Fixture 'malformed.json'                           -Expected 0
# The conventional "dotnet new classlib -o src/AiFramework.Domain" layout produces a
# dotted, root-namespace-prefixed folder. Matching bare folder names only meant the
# rule silently switched itself off for the layout most repos actually use.
Assert-Exit -Name 'Prefixed layer folder src/AiFramework.Domain is enforced' -Script 'dependency-rule.ps1' -Fixture 'domain-prefixed-layer-violation.json' -Expected 2
# ...and the prefix tolerance must not turn into prefix over-matching.
Assert-Exit -Name 'src/DomainServices is NOT the Domain layer'               -Script 'dependency-rule.ps1' -Fixture 'domain-services-not-a-layer.json'    -Expected 0
# Claude Code on Windows sends backslash file_path values; every other path fixture
# uses forward slashes, so this pins ConvertTo-ForwardSlash actually doing its job.
Assert-Exit -Name 'Backslash Windows path + EF Core is blocked'              -Script 'dependency-rule.ps1' -Fixture 'domain-ef-violation-backslash.json'   -Expected 2
Assert-Exit -Name 'Backslash Windows path, clean Domain, is allowed'         -Script 'dependency-rule.ps1' -Fixture 'domain-clean-backslash.json'         -Expected 0

Write-Host ''
Write-Host 'no-secrets.ps1'
Assert-Exit -Name 'Populated Password= is blocked'      -Script 'no-secrets.ps1' -Fixture 'appsettings-password-secret.json'      -Expected 2
Assert-Exit -Name 'Password=${VAR} is allowed'          -Script 'no-secrets.ps1' -Fixture 'appsettings-password-placeholder.json' -Expected 0
Assert-Exit -Name 'Populated ApiKey is blocked'         -Script 'no-secrets.ps1' -Fixture 'appsettings-apikey-secret.json'        -Expected 2
Assert-Exit -Name 'Empty and REPLACE_ME are allowed'    -Script 'no-secrets.ps1' -Fixture 'appsettings-apikey-empty.json'         -Expected 0
Assert-Exit -Name 'Non-appsettings files are ignored'   -Script 'no-secrets.ps1' -Fixture 'other-json-with-password.json'         -Expected 0
Assert-Exit -Name 'Malformed payload fails open'        -Script 'no-secrets.ps1' -Fixture 'malformed.json'                        -Expected 0
Assert-Exit -Name 'Bare "Password" JSON key is blocked'    -Script 'no-secrets.ps1' -Fixture 'appsettings-bare-password-key.json'         -Expected 2
Assert-Exit -Name 'Bare "Password" with ${VAR} is allowed' -Script 'no-secrets.ps1' -Fixture 'appsettings-bare-password-placeholder.json' -Expected 0
# Real key names carry affixes. An exact-word alternation let "AccessKeyId" and
# "SecretAccessKey" - a complete AWS credential pair - through untouched.
Assert-Exit -Name 'AWS AccessKeyId / SecretAccessKey are blocked' -Script 'no-secrets.ps1' -Fixture 'appsettings-aws-secret-access-key.json'   -Expected 2
# The ASP.NET convention for a JWT signing secret is "Jwt": { "Key": "..." }.
Assert-Exit -Name 'Bare "Key" (Jwt signing key) is blocked'       -Script 'no-secrets.ps1' -Fixture 'appsettings-jwt-key.json'                 -Expected 2
# ...and the key name alone must not be the trigger: a Trusted_Connection string
# holds no secret and blocking it was a pure false positive.
Assert-Exit -Name 'ConnectionString with no password is allowed'  -Script 'no-secrets.ps1' -Fixture 'appsettings-connectionstring-trusted.json'  -Expected 0
Assert-Exit -Name 'ConnectionString containing Password= blocked' -Script 'no-secrets.ps1' -Fixture 'appsettings-connectionstring-password.json' -Expected 2

Write-Host ''
Write-Host 'protect-migrations.ps1'

# The guard now keys on whether the target is a migration ALREADY COMMITTED to git
# history (HEAD), not merely one that exists on disk - ae88b80 moved the check from
# Test-Path to `git ls-tree HEAD`. Exercising that for real needs an actual git
# repository, so a throwaway one is created in a temp directory outside this repo:
# `git init`, a local user.email/user.name so the commit works on any machine, a
# migration file added and committed. Payload JSON is generated around the paths
# inside it, the same isolation technique the format-and-lint and verify-build
# blocks below use for their own fixtures.
$MigRoot = Join-Path $env:TEMP ("claude-hook-migrations-{0}" -f $PID)
$MigDir  = Join-Path $MigRoot 'Migrations'
New-Item -ItemType Directory -Path $MigDir -Force | Out-Null
try {
    & git -C $MigRoot init --quiet | Out-Null
    & git -C $MigRoot config user.email 'hook-tests@example.com' | Out-Null
    & git -C $MigRoot config user.name 'Hook Tests' | Out-Null

    $ExistingMigration = Join-Path $MigDir '20260101120000_InitialCreate.cs'
    Set-Content -LiteralPath $ExistingMigration -Value 'public partial class InitialCreate { }' -NoNewline
    & git -C $MigRoot add 'Migrations/20260101120000_InitialCreate.cs' | Out-Null
    & git -C $MigRoot commit --quiet -m 'Add InitialCreate migration' | Out-Null

    $UncommittedMigration = Join-Path $MigDir '20260201090000_AddOrderTotal.cs'
    Set-Content -LiteralPath $UncommittedMigration -Value "// generated by dotnet ef migrations add AddOrderTotal`n" -NoNewline
    # Deliberately left uncommitted - this is the permissive case ae88b80 exists to allow.

    $ExistingFs    = ConvertTo-ForwardSlash -Path $ExistingMigration
    $UncommittedFs = ConvertTo-ForwardSlash -Path $UncommittedMigration
    # Same existing file, addressed the way Claude Code on Windows actually sends it.
    $ExistingBackslash = $ExistingMigration -replace '\\', '\\'

    $EditExistingPath     = Join-Path $MigRoot 'edit-existing.json'
    $WriteExistingPath    = Join-Path $MigRoot 'write-existing.json'
    $WriteUncommittedPath = Join-Path $MigRoot 'write-uncommitted.json'
    $EditBackslashPath    = Join-Path $MigRoot 'edit-existing-backslash.json'
    $WriteDeletedPath     = Join-Path $MigRoot 'write-deleted.json'

    @"
{
  "tool_name": "Edit",
  "tool_input": {
    "file_path": "$ExistingFs",
    "old_string": "nullable: true",
    "new_string": "nullable: false"
  }
}
"@ | Set-Content -LiteralPath $EditExistingPath

    @"
{
  "tool_name": "Write",
  "tool_input": {
    "file_path": "$ExistingFs",
    "content": "public partial class InitialCreate { /* rewritten */ }"
  }
}
"@ | Set-Content -LiteralPath $WriteExistingPath

    @"
{
  "tool_name": "Write",
  "tool_input": {
    "file_path": "$UncommittedFs",
    "content": "// generated by dotnet ef migrations add AddOrderTotal, edited before the first commit\n"
  }
}
"@ | Set-Content -LiteralPath $WriteUncommittedPath

    @"
{
  "tool_name": "Edit",
  "tool_input": {
    "file_path": "$ExistingBackslash",
    "old_string": "nullable: true",
    "new_string": "nullable: false"
  }
}
"@ | Set-Content -LiteralPath $EditBackslashPath

    @"
{
  "tool_name": "Write",
  "tool_input": {
    "file_path": "$ExistingFs",
    "content": "public partial class InitialCreate { /* recreated after delete */ }"
  }
}
"@ | Set-Content -LiteralPath $WriteDeletedPath

    Assert-Exit -Name 'Edit over a COMMITTED migration is blocked'                       -Script 'protect-migrations.ps1' -FixturePath $EditExistingPath     -Expected 2
    Assert-Exit -Name 'Write over a COMMITTED migration is blocked'                      -Script 'protect-migrations.ps1' -FixturePath $WriteExistingPath    -Expected 2
    Assert-Exit -Name 'Backslash path over a COMMITTED migration is blocked'             -Script 'protect-migrations.ps1' -FixturePath $EditBackslashPath    -Expected 2
    Assert-Exit -Name 'Write to an UNCOMMITTED migration in the same repo is allowed'    -Script 'protect-migrations.ps1' -FixturePath $WriteUncommittedPath -Expected 0

    # The delete-then-recreate bypass ae88b80 set out to close: Test-Path saw no file on
    # disk and waved the write through. HEAD still has it, so this must still be blocked.
    Remove-Item -LiteralPath $ExistingMigration -Force
    Assert-Exit -Name 'Write recreating a COMMITTED migration after deletion is blocked' -Script 'protect-migrations.ps1' -FixturePath $WriteDeletedPath    -Expected 2
}
finally {
    if (Test-Path -LiteralPath $MigRoot) {
        Remove-Item -LiteralPath $MigRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# migration-write-new.json and migration-edit.json both point at a path under
# C:/repo, a directory that does not exist anywhere on disk - `git -C` on it cannot
# even start a checkout there, so both are really "git cannot answer" cases, not
# "this migration is new" ones. The real new-and-uncommitted case is covered above,
# against an actual git repository, so both of these now exercise the fail-closed
# behaviour for a path git cannot evaluate at all.
Assert-Exit -Name 'Write to a migration path git cannot evaluate is refused'  -Script 'protect-migrations.ps1' -Fixture 'migration-write-new.json' -Expected 2
Assert-Exit -Name 'Edit to a migration path git cannot evaluate is refused'   -Script 'protect-migrations.ps1' -Fixture 'migration-edit.json'      -Expected 2
Assert-Exit -Name 'Ordinary .cs edits are untouched'                          -Script 'protect-migrations.ps1' -Fixture 'ordinary-cs-edit.json'    -Expected 0
Assert-Exit -Name 'Malformed payload fails open'                              -Script 'protect-migrations.ps1' -Fixture 'malformed.json'           -Expected 0

Write-Host ''
Write-Host 'format-and-lint.ps1  (toolchain absent on this machine - these pin the fail-open path)'

# format-cs.json / format-ts.json must point at files that genuinely exist on
# disk. format-and-lint.ps1 guards on Test-Path -LiteralPath and returns
# before ever reaching the toolchain check otherwise, so a fixture pointing
# at a nonexistent path would pass while testing nothing. Real files are
# created in a temp directory outside the repo, and fixture JSON referencing
# them is generated here at test time and handed to Invoke-Hook via
# -FixturePath, so the -Fixture contract (a name resolved under the fixtures
# dir) is untouched for every other caller.
$TempRoot = Join-Path $env:TEMP ("claude-hook-tests-{0}" -f $PID)
New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null
try {
    $TempCsFile = Join-Path $TempRoot 'Order.cs'
    $TempTsFile = Join-Path $TempRoot 'orders.component.ts'
    Set-Content -LiteralPath $TempCsFile -Value 'public sealed class Order { }' -NoNewline
    Set-Content -LiteralPath $TempTsFile -Value 'export class OrdersComponent {}' -NoNewline

    $RepoRootFs   = ConvertTo-ForwardSlash -Path (Split-Path -Parent (Split-Path -Parent $HookDir))
    $TempCsFileFs = ConvertTo-ForwardSlash -Path $TempCsFile
    $TempTsFileFs = ConvertTo-ForwardSlash -Path $TempTsFile

    $CsFixturePath = Join-Path $TempRoot 'format-cs.json'
    $TsFixturePath = Join-Path $TempRoot 'format-ts.json'

    @"
{
  "tool_name": "Write",
  "cwd": "$RepoRootFs",
  "tool_input": {
    "file_path": "$TempCsFileFs",
    "content": "public sealed class Order { }"
  }
}
"@ | Set-Content -LiteralPath $CsFixturePath

    @"
{
  "tool_name": "Write",
  "cwd": "$RepoRootFs",
  "tool_input": {
    "file_path": "$TempTsFileFs",
    "content": "export class OrdersComponent {}"
  }
}
"@ | Set-Content -LiteralPath $TsFixturePath

    Assert-Exit -Name '.cs with a real file exits 0 when no .NET SDK'   -Script 'format-and-lint.ps1' -FixturePath $CsFixturePath -Expected 0
    Assert-Exit -Name '.ts with a real file exits 0 when eslint absent' -Script 'format-and-lint.ps1' -FixturePath $TsFixturePath -Expected 0
    Assert-Exit -Name 'Unhandled extension exits 0'                     -Script 'format-and-lint.ps1' -Fixture 'format-unknown-ext.json' -Expected 0
    Assert-Exit -Name 'Malformed payload fails open'                    -Script 'format-and-lint.ps1' -Fixture 'malformed.json'          -Expected 0
}
finally {
    if (Test-Path -LiteralPath $TempRoot) {
        Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host 'verify-build.ps1'

# stop-normal.json used to be a static fixture with cwd hardcoded to this
# repo's own root. That worked only as long as the repo had no solution file
# at its root; Task 1 added AiFramework.slnx there, so Find-BuildFile now
# finds a real build target and the hook runs an actual `dotnet build`
# instead of taking the "no solution" skip path this fixture is named for -
# same exit code (0), for an entirely different reason, and with empty
# stderr where a skip reason was expected. A directory freshly created under
# $env:TEMP is guaranteed to hold no .sln/.slnx/.csproj, so the fixture is
# generated here against one, the same isolation technique protect-migrations
# and format-and-lint use above, rather than pointing at the live repo.
$NoSolutionRoot = Join-Path $env:TEMP ("claude-hook-no-solution-{0}" -f $PID)
New-Item -ItemType Directory -Path $NoSolutionRoot -Force | Out-Null
try {
    $NoSolutionRootFs = ConvertTo-ForwardSlash -Path $NoSolutionRoot
    $StopNormalFixturePath = Join-Path $NoSolutionRoot 'stop-normal.json'
    @"
{
  "hook_event_name": "Stop",
  "cwd": "$NoSolutionRootFs",
  "stop_hook_active": false
}
"@ | Set-Content -LiteralPath $StopNormalFixturePath

    Assert-Exit -Name 'stop_hook_active short-circuits (loop guard)' -Script 'verify-build.ps1' -Fixture 'stop-hook-active.json' -Expected 0
    Assert-Exit -Name 'Repo root with no solution exits 0'           -Script 'verify-build.ps1' -FixturePath $StopNormalFixturePath -Expected 0
    Assert-Exit -Name 'Malformed payload fails open'                 -Script 'verify-build.ps1' -Fixture 'malformed.json'        -Expected 0

    # A gate that skips silently is indistinguishable from a gate that passed, which is
    # how a single non-recursive root *.sln search stayed unnoticed. Every skip except
    # the loop guard must say so on stderr.
    function Get-HookStderr {
        # Native-command stderr becomes a terminating error record under
        # $ErrorActionPreference = 'Stop', so it is relaxed for the duration of the
        # capture and restored immediately afterwards.
        param(
            [Parameter(Mandatory)][string]$Script,
            [string]$Fixture,
            [string]$FixturePath
        )
        $resolvedFixturePath = if (-not [string]::IsNullOrWhiteSpace($FixturePath)) { $FixturePath } else { Join-Path $Fixtures $Fixture }
        $errFile = Join-Path $env:TEMP ("claude-hook-stderr-{0}.txt" -f $PID)
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $payload = Get-Content -LiteralPath $resolvedFixturePath -Raw
            $payload | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $HookDir $Script) 2> $errFile | Out-Null
            if (Test-Path -LiteralPath $errFile) { return (Get-Content -LiteralPath $errFile -Raw) }
            return ''
        }
        finally {
            $ErrorActionPreference = $previous
            if (Test-Path -LiteralPath $errFile) { Remove-Item -LiteralPath $errFile -Force -ErrorAction SilentlyContinue }
        }
    }

    $vbSkipErr = Get-HookStderr -Script 'verify-build.ps1' -FixturePath $StopNormalFixturePath
    Assert-True -Name 'A skipped build gate announces why on stderr' -Condition ($vbSkipErr -match 'build gate skipped')

    $vbLoopErr = Get-HookStderr -Script 'verify-build.ps1' -Fixture 'stop-hook-active.json'
    Assert-True -Name 'The loop guard stays silent'                  -Condition ([string]::IsNullOrWhiteSpace($vbLoopErr))

    $vbBadErr = Get-HookStderr -Script 'verify-build.ps1' -Fixture 'malformed.json'
    Assert-True -Name 'An unparseable payload stays silent'          -Condition ([string]::IsNullOrWhiteSpace($vbBadErr))
}
finally {
    if (Test-Path -LiteralPath $NoSolutionRoot) {
        Remove-Item -LiteralPath $NoSolutionRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host ("Passed: {0}   Failed: {1}" -f $script:Passed, $script:Failed)
if ($script:Failed -gt 0) { exit 1 }
exit 0
