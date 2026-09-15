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
    [switch]$SkipMigrations,
    # Starts Seq (docker-compose.yml's "observability" profile) alongside Postgres, and points
    # the launched API at it via Observability__Otlp__Enabled / Observability__Otlp__Endpoint.
    # Off by default: appsettings.json already defaults Otlp:Enabled to false, so a plain
    # dev.ps1 run costs nothing extra and never tries to export to a collector that isn't there.
    [switch]$WithSeq
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# docker-compose.yml publishes Postgres on DEV_PG_PORT, defaulting to 55433. The connection
# string below has to agree with it or the migration step silently targets the wrong database.
$pgPort = if ($env:DEV_PG_PORT) { $env:DEV_PG_PORT } else { '55433' }

# Same default docker-compose.yml's SEQ_PORT falls back to. Only read when -WithSeq is set.
$seqPort = if ($env:SEQ_PORT) { $env:SEQ_PORT } else { '55341' }

# Fixed, not configurable: 5234 comes from src/Api/Properties/launchSettings.json and 5173 from
# frontend/vite.config.ts. (API_PORT moves where Vite *proxies to*, not where the API listens,
# so it is deliberately not consulted here.)
$apiPort = 5234
# From src/Worker/Properties/launchSettings.json. Checked below like the other two: the worker
# binds it for its health probes, and a port clash otherwise surfaces as a Kestrel error in a
# spawned window nobody is looking at, with the rest of the stack starting normally around it.
$workerPort = 5235
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
    Assert-PortFree -Port $workerPort -Purpose 'job worker'
    Assert-PortFree -Port $webPort -Purpose 'Vite dev server'
    $global:LASTEXITCODE = 0
}

# --wait blocks on the healthcheck in docker-compose.yml, so Postgres (and Seq, with -WithSeq)
# is genuinely accepting connections when this returns — no polling of our own required.
Invoke-Step 'Starting the dev database' {
    if ($WithSeq) {
        docker compose --project-directory $repoRoot --profile observability up -d --wait
    }
    else {
        docker compose --project-directory $repoRoot up -d --wait
    }
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
#
# -WithSeq sets Observability__Otlp__Enabled/__Endpoint on THIS process's environment before
# Start-Process spawns the API's window — Start-Process inherits the parent environment at spawn
# time, the same mechanism every double-underscore setting in this repo already relies on
# (ConnectionStrings__Default above, Cache__Enabled in k8s/overlays/local/config.yaml). Set here
# rather than baked into appsettings.Development.json, deliberately: that file is committed and
# read by every developer, and flipping Otlp:Enabled on by default there would mean a plain
# `dotnet run` — no Seq, no -WithSeq — quietly attempting exports to a collector that was never
# started. The env var is removed again immediately after spawning, same as ConnectionStrings__
# Default's own cleanup above, so it does not leak into commands run later in this same window.
Invoke-Step 'Launching the API' {
    if ($WithSeq) {
        $env:Observability__Otlp__Enabled = 'true'
        $env:Observability__Otlp__Endpoint = "http://localhost:$seqPort/ingest/otlp"
    }
    try {
        Start-Process powershell -WorkingDirectory $repoRoot -ArgumentList @(
            '-NoExit', '-Command', 'dotnet run --project src/Api'
        )
    }
    finally {
        if ($WithSeq) {
            Remove-Item Env:\Observability__Otlp__Enabled -ErrorAction SilentlyContinue
            Remove-Item Env:\Observability__Otlp__Endpoint -ErrorAction SilentlyContinue
        }
    }
    $global:LASTEXITCODE = 0
}

# The job worker, in a window of its own. The compose loop runs the same host split the cluster
# does (ADR 0016) rather than a convenient approximation: the API here listens on no job queue, so
# without this window an enqueued job simply sits in Postgres and nothing says so.
#
# It inherits the same environment as the API above — including -WithSeq's OTLP settings, which is
# why this block sits inside the same try/finally-guarded region rather than after the cleanup:
# a worker exporting to a different place than the API would defeat the point of having one log
# store to correlate a job against the request that enqueued it.
Invoke-Step 'Launching the job worker' {
    if ($WithSeq) {
        $env:Observability__Otlp__Enabled = 'true'
        $env:Observability__Otlp__Endpoint = "http://localhost:$seqPort/ingest/otlp"
    }
    try {
        Start-Process powershell -WorkingDirectory $repoRoot -ArgumentList @(
            '-NoExit', '-Command', 'dotnet run --project src/Worker'
        )
    }
    finally {
        if ($WithSeq) {
            Remove-Item Env:\Observability__Otlp__Enabled -ErrorAction SilentlyContinue
            Remove-Item Env:\Observability__Otlp__Endpoint -ErrorAction SilentlyContinue
        }
    }
    $global:LASTEXITCODE = 0
}

# A fixed pause, not a health-check poll: polling /health/ready would need retry/timeout logic
# for a problem this small. In practice the API is answering within a few seconds, and the
# frontend dependency check below (when it has to run `npm install`) usually eats this delay on
# its own anyway — this only bites on an already-installed frontend, which is exactly when
# nothing else here would otherwise slow the frontend down. Observed: without this, Vite's proxy
# logs one ECONNREFUSED for /api/... before the API finishes starting, which is harmless (Vite
# retries on the next real request) but reads like a failure the first time you see it.
Write-Host '==> Giving the API a moment to finish starting' -ForegroundColor Cyan
Start-Sleep -Seconds 5

# A missing node_modules doesn't fail loudly: `npm start` still launches, and only the
# `vite` binary it shells out to is missing, so the error surfaces inside the new window
# ("'vite' is not recognized...") well after this script has already reported success. Checking
# here instead means a first run on a fresh clone (or a machine where npm install was never run)
# just works.
Invoke-Step 'Checking frontend dependencies' {
    $frontendDir = Join-Path $repoRoot 'frontend'
    if (Test-Path (Join-Path $frontendDir 'node_modules')) {
        $global:LASTEXITCODE = 0
        return
    }
    Write-Host '    node_modules missing, running npm install...' -ForegroundColor DarkGray
    # `--prefix` only changes where npm installs to, not where it reads package.json from —
    # that still comes from the process's current directory, which is whatever launched this
    # script (control-panel.bat's own folder, when run that way) and is not necessarily
    # $repoRoot. Push-Location first so `npm install` reads the right package.json.
    Push-Location $frontendDir
    try {
        npm install
    }
    finally {
        Pop-Location
    }
    # $LASTEXITCODE now reflects npm install itself; Invoke-Step checks it right after this
    # scriptblock returns, so a failed install still throws instead of proceeding to launch
    # Vite against a half-installed node_modules.
}

# Still not gated on an actual health check — Vite serves the app regardless, and the fixed
# pause above is enough in practice — just no longer assuming the race is imperceptible.
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
if ($WithSeq) {
    Write-Host "  Seq              http://localhost:$seqPort" -ForegroundColor Green
}
Write-Host ''
Write-Host '  Both tabs open by themselves. Ctrl-C in a window stops that process;' -ForegroundColor DarkGray
Write-Host '  scripts\stop-dev.ps1 stops the database (and Seq, if it was started).' -ForegroundColor DarkGray
