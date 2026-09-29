#requires -Version 5.1
<#
.SYNOPSIS
  Starts the local kind cluster, creating it first if it does not already exist.
.DESCRIPTION
  deploy.ps1 needs to be told up front whether -CreateCluster is correct — passing it against
  an existing cluster fails, since kind refuses to create one whose name is already taken; and
  omitting it against a name that does not exist fails just as fast in the kubectl calls that
  follow. This is the single entry point that checks first and calls deploy.ps1 either way, so
  the .bat launcher does not have to encode that logic itself.

  -WithObservability passes straight through to deploy.ps1 (OpenSearch, Prometheus, Grafana and
  the collector - see the kubernetes skill). It must be passed on EVERY redeploy of a cluster
  that has it: a plain deploy applies the plain overlay, whose app-config has no OTLP keys, so the
  apps stop exporting while the observability pods keep running and quietly show nothing new.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$WithObservability
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$cluster = 'aiframework'

# `kind get clusters` writes "No kind clusters found." to stderr (and a non-zero exit) when
# there are none — an entirely expected first-run case here, not a real error. Under
# $ErrorActionPreference = 'Stop', Windows PowerShell escalates that stderr line into a
# terminating error even though it is redirected to $null; the redirection only stops it from
# being *printed*, not from being *raised*. Silencing ErrorActionPreference for just this one
# call is what actually suppresses it.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'SilentlyContinue'
$existing = kind get clusters 2>$null
$ErrorActionPreference = $prevEap

$deployArgs = @{}
if ($SkipBuild) { $deployArgs['SkipBuild'] = $true }
if ($WithObservability) { $deployArgs['WithObservability'] = $true }

if ($existing -contains $cluster) {
    Write-Host "==> Cluster '$cluster' already exists - redeploying onto it" -ForegroundColor Cyan
}
else {
    Write-Host "==> Cluster '$cluster' does not exist - creating it" -ForegroundColor Cyan
    $deployArgs['CreateCluster'] = $true
}

& (Join-Path $PSScriptRoot 'deploy.ps1') @deployArgs
