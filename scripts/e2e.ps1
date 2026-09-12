#requires -Version 5.1
<#
.SYNOPSIS
  Runs the Playwright e2e suite against a stack it starts itself.
.DESCRIPTION
  A thin wrapper over `npm run e2e`, so the npm scripts stay the source of truth - the same
  relationship scripts/dev.ps1 has with the dev loop. Exists so local-run/control-panel.bat can
  offer the run without encoding any logic of its own.

  This starts its own API on 5234. Stop the dev loop first (scripts/stop-dev.ps1) or set
  API_PORT, or the two collide.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PlaywrightArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$frontend = Join-Path (Split-Path -Parent $PSScriptRoot) 'frontend'

Push-Location $frontend
try {
    npm run e2e -- @PlaywrightArgs
    if ($LASTEXITCODE -ne 0) { throw "The e2e suite failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'e2e passed.' -ForegroundColor Green
