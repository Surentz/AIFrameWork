#requires -Version 5.1
<#
.SYNOPSIS
  Runs the Playwright e2e suite against the local kind cluster.
.DESCRIPTION
  An additional gate, not the everyday loop: the cluster runs durable Wolverine, caching on, two
  API replicas behind cookie affinity, and the real rate limit, none of which the compose stack
  exercises.

  It assumes the cluster is already deployed and fails loudly if it is not, rather than silently
  triggering three docker builds and a rollout. Run ./deploy/start-cluster.ps1 for that.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PlaywrightArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$frontend = Join-Path $repoRoot 'frontend'

# The same mapping deploy.ps1 parses to print its "Ready:" URL. Reading it rather than hard-coding
# 8443 keeps the two in step if host 443 ever frees up.
$kindConfig = Get-Content (Join-Path $PSScriptRoot 'kind-cluster.yaml') -Raw
$httpsHostPort = if ($kindConfig -match '(?ms)containerPort:\s*443\s*\r?\n\s*hostPort:\s*(\d+)') {
    $matches[1]
}
else {
    443
}
$baseUrl = if ($httpsHostPort -eq 443) {
    'https://aiframework.localtest.me'
}
else {
    "https://aiframework.localtest.me:$httpsHostPort"
}

# Windows PowerShell 5.1 has no -SkipCertificateCheck, and the ingress serves a self-signed
# certificate (k8s/overlays/local/tls.yaml). Relaxed for this process only.
if (-not ('E2ECertPolicy' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Net;
public static class E2ECertPolicy {
    public static void Trust() {
        ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
    }
}
'@
}
[E2ECertPolicy]::Trust()

# NOT /health. The ingress routes / to the web pod, and frontend/nginx.conf ends in
# `try_files $uri $uri/ /index.html`, so /health answers 200 with the SPA whether or not the API
# is alive - a readiness gate on it is a guaranteed false positive. /api/auth/me is routed to the
# API and answers 401 when anonymous, which proves both that the API is up and that ingress
# routing works.
Write-Host "==> Checking the cluster at $baseUrl" -ForegroundColor Cyan
$status = $null
try {
    $response = Invoke-WebRequest -Uri "$baseUrl/api/auth/me" -UseBasicParsing -TimeoutSec 15
    $status = [int]$response.StatusCode
}
catch [System.Net.WebException] {
    # A 401 is the expected answer and Windows PowerShell raises it as an exception.
    if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
}

if ($status -ne 401) {
    $seen = if ($status) { "HTTP $status" } else { 'no response' }
    throw "The cluster did not answer 401 at $baseUrl/api/auth/me (got: $seen). " +
          'Deploy it first with ./deploy/start-cluster.ps1, then re-run this.'
}

Write-Host '==> Running the e2e suite against kind' -ForegroundColor Cyan
Push-Location $frontend
try {
    # Through run.ts, the same entry point every other e2e run uses, so the target handling and
    # the @local-only exclusion live in exactly one place.
    node e2e/setup/run.ts --target kind @PlaywrightArgs
    if ($LASTEXITCODE -ne 0) { throw "Playwright exited with code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'e2e against Kubernetes passed.' -ForegroundColor Green
