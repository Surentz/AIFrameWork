#requires -Version 5.1
<#
.SYNOPSIS
  Stops the local dev loop: the API, the Vite dev server, and the dev Postgres container.
.DESCRIPTION
  Counterpart to dev.ps1. dev.ps1 launches the API and frontend as detached processes in their
  own windows (see its own comment on why), so there is no job object here to stop cleanly by
  reference — instead this finds whatever is actually listening on the two fixed ports and
  kills those processes directly. The now-orphaned console windows stay open (their process
  exited under them, `-NoExit` keeps the window itself alive) but are harmless; close them by
  hand or leave them.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# Fixed, not configurable — same ports dev.ps1 uses, for the same reasons (launchSettings.json /
# vite.config.ts).
$apiPort = 5234
$webPort = 5173

function Stop-PortOwner {
    param([int]$Port, [string]$Name)
    $conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if (-not $conns) {
        Write-Host "==> $Name not running (nothing listening on $Port)" -ForegroundColor DarkGray
        return
    }
    foreach ($ownerId in ($conns.OwningProcess | Select-Object -Unique)) {
        $proc = Get-Process -Id $ownerId -ErrorAction SilentlyContinue
        $procName = if ($proc) { $proc.ProcessName } else { 'unknown' }
        Write-Host "==> Stopping $Name (PID $ownerId, $procName)" -ForegroundColor Cyan
        Stop-Process -Id $ownerId -Force -ErrorAction SilentlyContinue
    }
}

Stop-PortOwner -Port $apiPort -Name 'API'
Stop-PortOwner -Port $webPort -Name 'Vite dev server'

Write-Host '==> Stopping the dev database (and Seq, if it was started)' -ForegroundColor Cyan
# --profile observability is passed UNCONDITIONALLY, not just when dev.ps1 -WithSeq was used.
# Confirmed empirically: `docker compose down` with no --profile flag only tears down services
# in the active (here, empty/default) profile set for THIS invocation — it does not stop a
# container that a previous `up --profile observability` left running, even though `down` has no
# -d/detach concept of "current" beyond that. Passing the profile here is what makes this
# reliably clean up Seq regardless of which switch started it; harmless when Seq was never
# started at all, since compose then simply has nothing in that profile to stop.
docker compose --project-directory $repoRoot --profile observability down
if ($LASTEXITCODE -ne 0) { throw "docker compose down failed with exit code $LASTEXITCODE." }

Write-Host ''
Write-Host 'Dev loop stopped.' -ForegroundColor Green
Write-Host 'Any leftover API/frontend console windows can be closed by hand.' -ForegroundColor DarkGray
