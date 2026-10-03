#requires -Version 5.1
<#
.SYNOPSIS
  Opens the kind cluster's observability UIs: Grafana, Prometheus and OpenSearch Dashboards.
.DESCRIPTION
  None of the three is on the ingress - they are opt-in rehearsal tools, reached by
  port-forward (see the kubernetes skill). This runs the three forwards in THIS window, each as
  a background job that reconnects on its own, and opens Grafana once it answers.

  The reconnect is the point: a port-forward is bound to one pod, and every
  deploy.ps1 -WithObservability restarts Grafana (and Prometheus whenever its config changes), so
  a plain `kubectl port-forward` dies silently on the next deploy and the browser just stops
  loading. Close the window, or press Ctrl+C, to stop all three.

  Requires the cluster deployed WITH observability: deploy/start-cluster.ps1 -WithObservability,
  or the control panel's "Start Kubernetes + observability".
.PARAMETER NoBrowser
  Start the forwards without opening a browser.
#>
[CmdletBinding()]
param(
    [switch]$NoBrowser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$context = 'kind-aiframework'
$namespace = 'aiframework'

# Local port -> service and its port. Grafana first: it is what the browser opens.
$forwards = @(
    @{ Name = 'Grafana'; Service = 'grafana'; Local = 3000; Remote = 3000; Url = 'http://localhost:3000' },
    @{ Name = 'Prometheus'; Service = 'prometheus'; Local = 9090; Remote = 9090; Url = 'http://localhost:9090/alerts' },
    @{ Name = 'OpenSearch Dashboards'; Service = 'opensearch-dashboards'; Local = 5601; Remote = 5601; Url = 'http://localhost:5601' }
)

function Test-LocalPort([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $connect = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        return $connect.AsyncWaitHandle.WaitOne(300) -and $client.Connected
    }
    finally {
        $client.Close()
    }
}

# Fail with the fix named, rather than with three port-forwards that each die on "not found".
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'SilentlyContinue'
$grafana = kubectl --context $context -n $namespace get deployment grafana -o name 2>$null
$ErrorActionPreference = $prevEap
if (-not $grafana) {
    Write-Host "No Grafana in the '$namespace' namespace of '$context'." -ForegroundColor Red
    Write-Host 'Deploy the cluster with observability first:' -ForegroundColor Yellow
    Write-Host '  ./deploy/start-cluster.ps1 -WithObservability' -ForegroundColor Yellow
    Write-Host '  (or the control panel''s "Start Kubernetes + observability")' -ForegroundColor Yellow
    exit 1
}

$jobs = @()
foreach ($forward in $forwards) {
    if (Test-LocalPort $forward.Local) {
        Write-Host ("{0,-22} port {1} is already in use - skipped (another forward, or something else)" -f $forward.Name, $forward.Local) -ForegroundColor Yellow
        continue
    }

    $jobs += Start-Job -Name $forward.Service -ArgumentList $context, $namespace, $forward.Service, $forward.Local, $forward.Remote -ScriptBlock {
        param($context, $namespace, $service, $local, $remote)
        while ($true) {
            # Returns when the pod behind it goes away; the loop reattaches to its replacement.
            kubectl --context $context -n $namespace port-forward "svc/$service" "${local}:${remote}" 2>&1 | Out-Null
            Start-Sleep -Seconds 2
        }
    }
    Write-Host ("{0,-22} {1}" -f $forward.Name, $forward.Url) -ForegroundColor Green
}

if ($jobs.Count -eq 0) {
    Write-Host 'Nothing to forward - every port was already taken.' -ForegroundColor Yellow
    exit 0
}

if (-not $NoBrowser) {
    # Grafana needs a moment after the forward attaches; open it only once it answers.
    $grafanaUrl = $forwards[0].Url
    for ($i = 0; $i -lt 30; $i++) {
        try {
            Invoke-WebRequest -Uri "$grafanaUrl/api/health" -UseBasicParsing -TimeoutSec 2 | Out-Null
            Start-Process $grafanaUrl
            break
        }
        catch {
            # Not up yet. Windows PowerShell throws WebException here and PowerShell 7
            # HttpRequestException, so this does not name a type.
            Start-Sleep -Seconds 1
        }
    }
}

Write-Host ''
Write-Host 'Grafana: anonymous, read-only; the AIFramework dashboard is the home page.' -ForegroundColor DarkGray
Write-Host 'Forwards reconnect on their own after a redeploy. Close this window (or Ctrl+C) to stop them.' -ForegroundColor DarkGray

try {
    Wait-Job -Job $jobs | Out-Null
}
finally {
    $jobs | Stop-Job -PassThru | Remove-Job -Force
}
