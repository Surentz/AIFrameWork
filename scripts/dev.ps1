#requires -Version 5.1
<#
.SYNOPSIS
  Starts the local development loop: dev Postgres, the API, and the Vite dev server.
.DESCRIPTION
  The counterpart to deploy/deploy.ps1, which stands up the Kubernetes rehearsal cluster.
  This one is the inner loop — seconds to reload rather than minutes to redeploy.

  The API and the dev server each run in their own window rather than as background jobs.
  Both are long-running foreground processes with their own logs, and a separate window per
  process keeps those logs readable and makes Ctrl-C mean "stop this one" instead of
  "stop something".
#>
[CmdletBinding()]
param(
    [switch]$SkipMigrations
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# docker-compose.yml publishes Postgres on DEV_PG_PORT, defaulting to 55433. The connection
# string below has to agree with it or the migration step silently targets the wrong database.
$pgPort = if ($env:DEV_PG_PORT) { $env:DEV_PG_PORT } else { '55433' }

# Fixed, not configurable: 5234 comes from src/Api/Properties/launchSettings.json and 5173 from
# frontend/vite.config.ts. (API_PORT moves where Vite *proxies to*, not where the API listens,
# so it is deliberately not consulted here.)
$apiPort = 5234
$webPort = 5173

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

function Assert-PortFree {
    param([int]$Port, [string]$Purpose)
    $inUse = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($inUse) {
        # Plain ASCII deliberately: the Windows PowerShell 5.1 console mangles non-ASCII
        # punctuation (an em dash arrives as garbage), and a guard whose message is unreadable
        # is worse than no guard.
        throw "Port $Port ($Purpose) is already in use. " +
              'Either this script is already running, or `npm run e2e` has its own API up - ' +
              'it binds 5234 too. Stop that first, or set API_PORT for the e2e run.'
    }
}

# Everything below needs the daemon, and Docker's own error for a stopped daemon is a wall of
# named-pipe text that says nothing useful to someone who just wants to start the app.
Invoke-Step 'Checking Docker is running' {
    docker version --format '{{.Server.Version}}' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker is not running. Start Docker Desktop, wait for the tray icon to settle, then re-run.'
    }
}

Invoke-Step 'Checking the ports are free' {
    Assert-PortFree -Port $apiPort -Purpose 'API'
    Assert-PortFree -Port $webPort -Purpose 'Vite dev server'
    $global:LASTEXITCODE = 0
}

# --wait blocks on the healthcheck in docker-compose.yml, so Postgres is genuinely accepting
# connections when this returns — no polling of our own required.
Invoke-Step 'Starting the dev database' {
    docker compose --project-directory $repoRoot up -d --wait
}

if ($SkipMigrations) {
    Write-Host '==> Skipping migrations (-SkipMigrations)' -ForegroundColor DarkGray
}
else {
    # ConnectionStrings__Default must be set explicitly. DesignTimeDbContextFactory falls back to
    # "Host=localhost;Database=design_time_only" when it is absent — a placeholder that exists for
    # the `dotnet ef` verbs which never dial out — so a bare `database update` would appear to run
    # and then fail against a database nobody created. This is the trap the root CLAUDE.md calls
    # "the gotcha that will cost you an afternoon"; absorbing it is half the reason this step is here.
    Invoke-Step 'Applying migrations' {
        $env:ConnectionStrings__Default =
            "Host=localhost;Port=$pgPort;Database=aiframework;Username=postgres;Password=postgres"
        try {
            dotnet ef database update `
                --project (Join-Path $repoRoot 'src/Infrastructure') `
                --startup-project (Join-Path $repoRoot 'src/Infrastructure')
        }
        finally {
            Remove-Item Env:\ConnectionStrings__Default -ErrorAction SilentlyContinue
        }
    }
}

# -NoExit keeps the window open if the process dies, so a startup failure is readable instead of
# a window that blinks out of existence.
Invoke-Step 'Launching the API' {
    Start-Process powershell -WorkingDirectory $repoRoot -ArgumentList @(
        '-NoExit', '-Command', 'dotnet run --project src/Api'
    )
    $global:LASTEXITCODE = 0
}

# Not gated on the API being healthy. Vite serves the app regardless and only its /api calls
# depend on the backend, which is up within seconds — polling /health here would be complexity
# for a delay nobody notices.
Invoke-Step 'Launching the frontend' {
    Start-Process powershell -WorkingDirectory $repoRoot -ArgumentList @(
        '-NoExit', '-Command', 'npm start --prefix frontend'
    )
    $global:LASTEXITCODE = 0
}

Write-Host ''
Write-Host "  App              http://localhost:$webPort" -ForegroundColor Green
Write-Host "  API reference    http://localhost:$apiPort/scalar/v1" -ForegroundColor Green
Write-Host "  Postgres         localhost:$pgPort" -ForegroundColor Green
Write-Host ''
Write-Host '  Both tabs open by themselves. Ctrl-C in a window stops that process;' -ForegroundColor DarkGray
Write-Host '  `docker compose down` stops the database.' -ForegroundColor DarkGray
