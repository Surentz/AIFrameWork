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
