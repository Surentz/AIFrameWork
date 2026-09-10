#requires -Version 5.1
<#
.SYNOPSIS
  Builds the images and deploys the application to a local kind cluster.
.DESCRIPTION
  Kustomize has no hook mechanism, so the ordering that Helm would express as a
  pre-upgrade hook is explicit here: migrations must finish before any API pod starts.
#>
[CmdletBinding()]
param(
    [switch]$CreateCluster,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cluster = 'aiframework'
$namespace = 'aiframework'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

if ($CreateCluster) {
    Invoke-Step 'Creating the kind cluster' {
        kind create cluster --config (Join-Path $PSScriptRoot 'kind-cluster.yaml')
    }
    Invoke-Step 'Installing ingress-nginx' {
        kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/main/deploy/static/provider/kind/deploy.yaml
    }
    Invoke-Step 'Waiting for ingress-nginx' {
        # Right after `kind create cluster`, the controller pod may not be scheduled yet.
        # `kubectl wait` treats zero currently-matching resources as an immediate failure
        # rather than polling for the pod to appear, so retry until it does (observed: this
        # is a real race on a just-booted control plane, not a hypothetical one).
        $deadline = (Get-Date).AddSeconds(300)
        do {
            kubectl -n ingress-nginx wait --for=condition=ready pod `
                -l app.kubernetes.io/component=controller --timeout=300s
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $deadline)
    }
}

if (-not $SkipBuild) {
    Invoke-Step 'Building the api image' {
        docker build -f (Join-Path $repoRoot 'Dockerfile.api') --target runtime `
            -t aiframework-api:local $repoRoot
    }
    Invoke-Step 'Building the migrator image' {
        docker build -f (Join-Path $repoRoot 'Dockerfile.api') --target migrator `
            -t aiframework-migrator:local $repoRoot
    }
    Invoke-Step 'Building the web image' {
        docker build -f (Join-Path $repoRoot 'Dockerfile.web') `
            -t aiframework-web:local $repoRoot
    }
}

# kind has no registry, so side-loading is the only way images reach its nodes. This runs on
# every invocation, not just after a build: -SkipBuild -CreateCluster is a valid combination
# (a fresh cluster from already-built images), and a cluster with no images side-loaded would
# leave every pod stuck in ErrImagePull. Loading images that are already present is idempotent
# and costs only a few seconds, which is cheap insurance against that dead-cluster case.
Invoke-Step 'Loading images into kind' {
    kind load docker-image aiframework-api:local aiframework-migrator:local `
        aiframework-web:local --name $cluster
}

$overlay = Join-Path $repoRoot 'k8s/overlays/local'

# 1. Namespace, config, secrets, and Postgres.
Invoke-Step 'Applying the base stack' { kubectl apply -k $overlay }

# 2. Postgres must answer before migrations can run.
Invoke-Step 'Waiting for postgres' {
    kubectl -n $namespace wait --for=condition=ready pod -l app=postgres --timeout=180s
}

# 3. Migrations, to completion. Deleted first: a completed Job has immutable fields, so a
#    plain re-apply fails on the second deployment.
Invoke-Step 'Running migrations' {
    kubectl -n $namespace delete job migrate --ignore-not-found
    kubectl apply -k $overlay
    kubectl -n $namespace wait --for=condition=complete job/migrate --timeout=180s
}

# 4. Roll the application out onto the migrated schema.
Invoke-Step 'Rolling out the application' {
    kubectl -n $namespace rollout restart deployment/api deployment/web
    kubectl -n $namespace rollout status deployment/api --timeout=300s
    kubectl -n $namespace rollout status deployment/web --timeout=300s
}

Write-Host ''
Write-Host 'Ready: https://aiframework.localtest.me' -ForegroundColor Green
Write-Host 'The certificate is self-signed, so the browser will warn once.' -ForegroundColor DarkGray
