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

function Find-BuildFile {
    # Recursive search for the first file matching any of $Patterns, skipping the
    # directories that make a naive -Recurse over a real repo pathological
    # (node_modules above all). Returns a full path, or $null.
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string[]]$Patterns
    )
    $skip = @('node_modules', 'bin', 'obj', '.git', '.vs', '.idea', 'dist', 'packages')

    $queue = New-Object System.Collections.Generic.Queue[string]
    $queue.Enqueue($Root)

    while ($queue.Count -gt 0) {
        $dir = $queue.Dequeue()

        foreach ($pattern in $Patterns) {
            $hit = Get-ChildItem -LiteralPath $dir -Filter $pattern -File -ErrorAction SilentlyContinue |
                   Sort-Object Name |
                   Select-Object -First 1
            if ($null -ne $hit) { return $hit.FullName }
        }

        $children = Get-ChildItem -LiteralPath $dir -Directory -Force -ErrorAction SilentlyContinue
        foreach ($child in $children) {
            if ($skip -notcontains $child.Name) { $queue.Enqueue($child.FullName) }
        }
    }
    return $null
}

$denyMessage = $null
# Why the gate did not run this turn. A dead gate that says nothing is
# indistinguishable from a passing one, so every skip except the loop guard
# announces itself on stderr before exiting 0.
$skipReason = $null

try {
    $payload = Read-HookPayload

    # Loop guard: without this, a build that cannot be fixed re-triggers Stop forever.
    # Deliberately first and unconditional, and deliberately silent - it fires on
    # every legitimate second pass and must not add noise.
    $alreadyActive = Get-Prop -Object $payload -Name 'stop_hook_active'

    # An unparseable payload exits 0 SILENTLY per the spec 7 contract - the skip
    # reason below is for a live gate that found nothing to build, not for a
    # payload we never understood in the first place.
    if ($null -ne $payload -and $alreadyActive -ne $true) {
        $repoRoot = Get-RepoRoot -Payload $payload -HookDir $PSScriptRoot

        if (-not (Test-Path -LiteralPath $repoRoot)) {
            $skipReason = "repo root '$repoRoot' does not exist"
        }
        else {
            # .slnx is the .NET 9+ solution format, and a solution need not sit at
            # the repo root; a repo may also carry no solution at all and just
            # csproj files. A single non-recursive *.sln search at the root
            # silently disabled this gate for all three of those layouts.
            $buildTarget = Find-BuildFile -Root $repoRoot -Patterns @('*.sln', '*.slnx')

            if ($null -eq $buildTarget) {
                $firstProject = Find-BuildFile -Root $repoRoot -Patterns @('*.csproj')
                if ($null -ne $firstProject) {
                    # Projects but no solution: build the directory and let MSBuild
                    # work out the graph.
                    $buildTarget = $repoRoot
                }
            }

            if ($null -eq $buildTarget) {
                $skipReason = "no .sln, .slnx or .csproj found under '$repoRoot'"
            }
            elseif (-not (Test-DotnetSdkPresent)) {
                $skipReason = 'no .NET SDK on PATH (dotnet --list-sdks reports none)'
            }
            else {
                $buildOutput = & dotnet build $buildTarget --nologo --verbosity quiet
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
    $skipReason = $null
}

if ($null -ne $denyMessage) {
    [Console]::Error.WriteLine($denyMessage)
    exit 2
}
if ($null -ne $skipReason) {
    [Console]::Error.WriteLine("verify-build: build gate skipped - $skipReason.")
}
exit 0
