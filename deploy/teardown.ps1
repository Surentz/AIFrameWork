#requires -Version 5.1
<#
.SYNOPSIS
  Tears down the local kind cluster entirely.
.DESCRIPTION
  Counterpart to deploy.ps1 -CreateCluster / start-cluster.ps1. Deletes the kind cluster and
  everything running in it — this is a disposable rehearsal cluster, not a place to keep data,
  so Postgres's contents go with it. Re-run start-cluster.ps1 (or deploy.ps1 -CreateCluster) to
  rebuild from scratch, migrations and all.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$cluster = 'aiframework'

# `kind get clusters` writes "No kind clusters found." to stderr (and a non-zero exit) when
# there are none — an entirely expected case here (nothing to tear down), not a real error.
# Under $ErrorActionPreference = 'Stop', Windows PowerShell escalates that stderr line into a
# terminating error even though it is redirected to $null; the redirection only stops it from
# being *printed*, not from being *raised*. Silencing ErrorActionPreference for just this one
# call is what actually suppresses it. See start-cluster.ps1, which hits the identical case.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'SilentlyContinue'
$existing = kind get clusters 2>$null
$ErrorActionPreference = $prevEap
if ($existing -notcontains $cluster) {
    Write-Host "No kind cluster named '$cluster' exists. Nothing to do." -ForegroundColor DarkGray
    exit 0
}

Write-Host "==> Deleting kind cluster '$cluster'" -ForegroundColor Cyan
kind delete cluster --name $cluster
if ($LASTEXITCODE -ne 0) { throw "kind delete cluster failed with exit code $LASTEXITCODE." }

Write-Host ''
Write-Host 'Cluster deleted.' -ForegroundColor Green
