#requires -Version 5.1
<#
.SYNOPSIS
  Starts the job worker on its own, against the dev database.
.DESCRIPTION
  scripts\dev.ps1 already launches this alongside the API and Vite, so this is not how you start
  the stack. It is for the case that comes up on its own: restarting JUST the worker, without
  disturbing an API and a Vite server that are working fine.

  That case is routine rather than exotic — `dotnet run --project src/Worker -- codegen write`
  requires a worker restart to pick the new adapters up, and so does any change to a job handler.

  Runs in the foreground deliberately, unlike dev.ps1's three spawned windows: one process is
  easier to watch here than to hunt for, and Ctrl+C is then the obvious way to stop it.
#>
[CmdletBinding()]
param(
    # Skips the "is Postgres up" step, for a database that is already running somewhere this
    # script cannot see (a port-forward, a second compose project).
    [switch]$SkipDatabase
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# Fixed, from src/Worker/Properties/launchSettings.json — one above the API's 5234 so both can
# run at once. Nothing routes traffic here; the port exists for the health probes.
$workerPort = 5235

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

if (-not $SkipDatabase) {
    # Docker's own error for a stopped daemon is a wall of named-pipe text that says nothing
    # useful to someone who just wants to start the worker. Same guard dev.ps1 opens with.
    Invoke-Step 'Checking Docker is running' {
        docker version --format '{{.Server.Version}}' | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Docker is not running. Start Docker Desktop, wait for the tray icon to settle, then re-run.'
        }
    }

    # --wait blocks on docker-compose.yml's healthcheck, so Postgres is genuinely accepting
    # connections when this returns. Idempotent: harmless when dev.ps1 already started it.
    Invoke-Step 'Starting the dev database' {
        docker compose --project-directory $repoRoot up -d --wait
    }
}

# A worker that cannot bind its port fails with a message about Kestrel rather than about the
# worker already running, which is the usual cause. Say so here instead.
$inUse = Get-NetTCPConnection -LocalPort $workerPort -State Listen -ErrorAction SilentlyContinue
if ($inUse) {
    # Plain ASCII deliberately: the Windows PowerShell 5.1 console mangles non-ASCII punctuation,
    # and a guard whose message is unreadable is worse than no guard.
    throw "Port $workerPort (job worker) is already in use. " +
          'The worker is probably already running - either from scripts\dev.ps1 or from an ' +
          'earlier run of this script. Stop it first, or use scripts\stop-dev.ps1.'
}

Write-Host ''
Write-Host 'Starting the job worker. It listens on the lanes in Jobs__Queues' -ForegroundColor Cyan
Write-Host '(light,heavy by default) and serves /health on http://localhost:5235.' -ForegroundColor Cyan
Write-Host 'Press Ctrl+C to stop it.' -ForegroundColor DarkGray
Write-Host ''

# Foreground, so its logs are right here. ASPNETCORE_ENVIRONMENT comes from the project's
# launchSettings.json; without Development, appsettings.Development.json never loads and the
# startup guard throws on an empty connection string.
dotnet run --project (Join-Path $repoRoot 'src/Worker')
