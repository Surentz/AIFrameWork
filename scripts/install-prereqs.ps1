#requires -Version 5.1
<#
.SYNOPSIS
  Installs (or checks for) everything this repo's other scripts assume is already on the
  machine: the .NET SDK, Node.js, Docker Desktop, kind, and k9s.
.DESCRIPTION
  dev.ps1 and deploy.ps1 both assume a machine that already has their tooling. That is fine for
  the machine this repo was scaffolded on, but not for a genuinely new one — this script is the
  thing to run first, once, before either of them.

  Philosophy: install what is missing; report, don't silently touch, what is present but older
  than expected. Silently upgrading an already-installed tool (especially Docker Desktop, which
  may be running, or Node.js, which every other Node project on the machine also uses) is a
  machine-wide side effect this script has no business taking without the person reading what it
  found first. A missing tool has no such installed state to disturb, so those install directly.

  winget itself ships with modern Windows; if it is missing, everything here is unreachable and
  this script says so rather than guessing at another install method.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'  # one tool's failure should not stop the rest from being checked

$results = New-Object System.Collections.Generic.List[PSCustomObject]

function Add-Result {
    param([string]$Tool, [string]$Status, [string]$Detail = '')
    $results.Add([PSCustomObject]@{ Tool = $Tool; Status = $Status; Detail = $Detail })
}

function Test-CommandExists {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Install-Winget {
    param([string]$Id, [string]$DisplayName)
    Write-Host "==> Installing $DisplayName" -ForegroundColor Cyan
    winget install --id $Id --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -eq 0) {
        Add-Result -Tool $DisplayName -Status 'Installed'
    }
    else {
        Add-Result -Tool $DisplayName -Status 'FAILED' -Detail "winget exited $LASTEXITCODE"
    }
}

if (-not (Test-CommandExists 'winget')) {
    Write-Host 'winget is not available on this machine.' -ForegroundColor Red
    Write-Host 'It ships with Windows 11 and current Windows 10 by default (App Installer' -ForegroundColor Red
    Write-Host 'from the Microsoft Store). Install that first, then re-run this script.' -ForegroundColor Red
    exit 1
}

# --- .NET SDK -----------------------------------------------------------------------------
# net10.0 is the pinned target framework (see the root CLAUDE.md); no global.json pins an exact
# SDK patch, so any 10.x SDK satisfies it — this checks the major version band, not a specific
# build.
Write-Host '==> Checking .NET SDK' -ForegroundColor Cyan
$dotnetSdks = if (Test-CommandExists 'dotnet') { dotnet --list-sdks 2>$null } else { @() }
$has10Sdk = @($dotnetSdks | Where-Object { $_ -match '^10\.' }).Count -gt 0
if ($has10Sdk) {
    Add-Result -Tool '.NET SDK' -Status 'OK' -Detail (($dotnetSdks | Where-Object { $_ -match '^10\.' }) -join '; ')
}
else {
    Install-Winget -Id 'Microsoft.DotNet.SDK.10' -DisplayName '.NET SDK 10'
}

# --- Node.js --------------------------------------------------------------------------------
# CLAUDE.md documents 24.20.0 as tested. The hard floor is 24.15.0 — below that, this repo's own
# npm install crashes outright (an arborist bug in the npm version old Node ships bundled, and
# jsdom's own engines field refuses to install at all) — established the hard way earlier on
# this exact machine. A version at or above the floor is left alone rather than force-upgraded
# to the documented one, since Node is shared machine-wide.
Write-Host '==> Checking Node.js' -ForegroundColor Cyan
$nodeFloor = [version]'24.15.0'
if (-not (Test-CommandExists 'node')) {
    Install-Winget -Id 'OpenJS.NodeJS.LTS' -DisplayName 'Node.js (LTS)'
}
else {
    $nodeVersionRaw = (node --version) -replace '^v', ''
    try {
        $nodeOk = [version]$nodeVersionRaw -ge $nodeFloor
    }
    catch {
        # A pre-release/odd version string (e.g. "24.20.0-nightly") — treat as "can't confirm",
        # not as a hard failure; a human should look rather than have this script guess.
        $nodeOk = $null
    }

    if ($nodeOk -eq $true) {
        Add-Result -Tool 'Node.js' -Status 'OK' -Detail "v$nodeVersionRaw"
    }
    elseif ($nodeOk -eq $false) {
        Add-Result -Tool 'Node.js' -Status 'TOO OLD' `
            -Detail "v$nodeVersionRaw is below v$nodeFloor. Run: winget install --id OpenJS.NodeJS.LTS"
    }
    else {
        Add-Result -Tool 'Node.js' -Status 'CHECK MANUALLY' -Detail "Unparseable version: $nodeVersionRaw"
    }
}

# --- Docker Desktop -------------------------------------------------------------------------
# Never auto-upgraded even when missing wouldn't be the case here — Docker Desktop cannot be
# silently finished either way: a fresh install needs the WSL2 backend accepted, the license
# terms accepted, and a manual first launch before `docker version` answers at all. This script
# starts that install but cannot finish it unattended, and says so.
Write-Host '==> Checking Docker' -ForegroundColor Cyan
$dockerOk = $false
if (Test-CommandExists 'docker') {
    docker version --format '{{.Server.Version}}' 2>$null | Out-Null
    $dockerOk = $LASTEXITCODE -eq 0
}
if ($dockerOk) {
    Add-Result -Tool 'Docker Desktop' -Status 'OK'
}
elseif (Test-CommandExists 'docker') {
    Add-Result -Tool 'Docker Desktop' -Status 'NOT RUNNING' `
        -Detail 'Installed but the daemon is not responding. Start Docker Desktop and wait for the tray icon to settle.'
}
else {
    Install-Winget -Id 'Docker.DockerDesktop' -DisplayName 'Docker Desktop'
    Add-Result -Tool 'Docker Desktop' -Status 'NEEDS MANUAL SETUP' `
        -Detail 'First launch requires accepting the WSL2 backend and license terms by hand, then a restart.'
}

# --- kubectl --------------------------------------------------------------------------------
# Not installed standalone if Docker Desktop already provides one on PATH (it bundles its own
# under Program Files\Docker\Docker\resources\bin) — this only fills the gap for a machine
# running a different container backend, or where Docker Desktop's kubectl isn't on PATH yet.
Write-Host '==> Checking kubectl' -ForegroundColor Cyan
if (Test-CommandExists 'kubectl') {
    Add-Result -Tool 'kubectl' -Status 'OK'
}
else {
    Install-Winget -Id 'Kubernetes.kubectl' -DisplayName 'kubectl'
}

# --- kind -------------------------------------------------------------------------------------
Write-Host '==> Checking kind' -ForegroundColor Cyan
if (Test-CommandExists 'kind') {
    Add-Result -Tool 'kind' -Status 'OK'
}
else {
    Install-Winget -Id 'Kubernetes.kind' -DisplayName 'kind'
}

# --- k9s --------------------------------------------------------------------------------------
# Optional in the sense that nothing in dev.ps1/deploy.ps1 needs it, but it is the recommended
# way to look inside the kind cluster (see CLAUDE.md's Kubernetes section), so it belongs here.
Write-Host '==> Checking k9s' -ForegroundColor Cyan
if (Test-CommandExists 'k9s') {
    Add-Result -Tool 'k9s' -Status 'OK'
}
else {
    Install-Winget -Id 'Derailed.k9s' -DisplayName 'k9s'
}

# --- npm platform override sanity check ------------------------------------------------------
# Not a tool to install, but a specific, hard-to-diagnose trap this project already hit on this
# machine: a global ~/.npmrc pinning `os=` (or `cpu=`) to something other than this machine's
# real platform makes npm skip every platform-specific optional dependency silently — no error,
# just a native binding missing three layers deep the first time something needs it (rolldown,
# in this project's case). Checked here since it would otherwise waste as much time on a new
# machine as it did on this one.
Write-Host '==> Checking npm config for a platform override' -ForegroundColor Cyan
$npmrcPath = Join-Path $HOME '.npmrc'
if (Test-Path $npmrcPath) {
    $npmrcContent = Get-Content $npmrcPath -Raw
    if ($npmrcContent -match '(?m)^\s*os\s*=\s*(\S+)') {
        $pinnedOs = $matches[1]
        if ($pinnedOs -ne 'win32') {
            Add-Result -Tool 'npm config (~/.npmrc)' -Status 'MISCONFIGURED' `
                -Detail "os=$pinnedOs pinned, but this machine is win32 - remove that line or every platform-specific optional dependency (native bindings) silently fails to install."
        }
    }
}

# --- Summary ------------------------------------------------------------------------------
Write-Host ''
Write-Host '============================================' -ForegroundColor Cyan
Write-Host '  Prerequisite check summary' -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan
foreach ($r in $results) {
    $color = switch ($r.Status) {
        'OK' { 'Green' }
        'Installed' { 'Green' }
        'FAILED' { 'Red' }
        'TOO OLD' { 'Yellow' }
        'MISCONFIGURED' { 'Yellow' }
        'NOT RUNNING' { 'Yellow' }
        'NEEDS MANUAL SETUP' { 'Yellow' }
        default { 'Gray' }
    }
    $line = "  {0,-24} {1}" -f $r.Tool, $r.Status
    Write-Host $line -ForegroundColor $color
    if ($r.Detail) { Write-Host "                           $($r.Detail)" -ForegroundColor DarkGray }
}
Write-Host ''
Write-Host 'If anything was just installed, open a new terminal window before running' -ForegroundColor DarkGray
Write-Host 'start-dev / start-k8s - PATH changes do not reach windows already open.' -ForegroundColor DarkGray

$hadProblem = @($results | Where-Object { $_.Status -notin @('OK', 'Installed') }).Count -gt 0
if ($hadProblem) { exit 1 } else { exit 0 }
