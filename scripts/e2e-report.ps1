#requires -Version 5.1
<#
.SYNOPSIS
  Opens the HTML report from the last Playwright run.
.DESCRIPTION
  Separate from the run scripts because the report is worth reopening after the window that
  produced it has been closed - the normal case for someone driving this from the control panel.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$frontend = Join-Path (Split-Path -Parent $PSScriptRoot) 'frontend'
$report = Join-Path $frontend 'playwright-report/index.html'

if (-not (Test-Path $report)) {
    Write-Host 'No e2e report found. Run the suite first (control panel option 6 or 7).' -ForegroundColor DarkGray
    exit 0
}

Push-Location $frontend
try {
    npm run e2e:report
}
finally {
    Pop-Location
}
