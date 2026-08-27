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
