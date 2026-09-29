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
    [switch]$SkipBuild,
    # Deploys k8s/overlays/local-observability instead of k8s/overlays/local — the OTel
    # Collector, OpenSearch, and OpenSearch Dashboards, opt-in via a Kustomize Component. See
    # k8s/components/observability/kustomization.yaml for why this needs a separate overlay
    # rather than a flag inside the plain one.
    [switch]$WithObservability
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cluster = 'aiframework'
$namespace = 'aiframework'

# Every kubectl call below is pinned to this context rather than to whatever
# `kubectl config current-context` happens to be. A script whose whole point is that it can be
# run without thinking must not depend on ambient state: without the pin, running it while the
# current context points at some other cluster would create the namespace there, apply the
# Secrets there, and restart deployments there. `kind create cluster --name X` names its
# context `kind-X`. The one call that goes unpinned is `kubectl kustomize`, which renders local
# files and contacts no cluster at all.
$context = "kind-$cluster"

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

# PowerShell does not treat a native command's nonzero exit as terminating, even under
# $ErrorActionPreference = 'Stop'. A scriptblock that runs several kubectl calls and only lets
# Invoke-Step check $LASTEXITCODE once, at the end, can swallow a failure in an earlier call if
# a later one happens to succeed (or itself always exits 0, like `rollout status` against a
# revision that never changed). Every native command inside a multi-command step must be
# checked immediately after it runs, with this.
function Assert-LastExitCode {
    param([string]$Command)
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

if ($CreateCluster) {
    Invoke-Step 'Creating the kind cluster' {
        kind create cluster --config (Join-Path $PSScriptRoot 'kind-cluster.yaml')
    }
    Invoke-Step 'Installing ingress-nginx' {
        kubectl --context $context apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/main/deploy/static/provider/kind/deploy.yaml
    }
    Invoke-Step 'Waiting for ingress-nginx' {
        # Right after `kind create cluster`, the controller pod may not be scheduled yet.
        # `kubectl wait` treats zero currently-matching resources as an immediate failure
        # rather than polling for the pod to appear, so retry until it does (observed: this
        # is a real race on a just-booted control plane, not a hypothetical one).
        # The inner --timeout is deliberately short: it bounds one attempt, not the whole
        # retry loop. Matching it to the outer 300s deadline would let a genuine permanent
        # failure take up to twice as long as $deadline implies before surfacing.
        $deadline = (Get-Date).AddSeconds(300)
        do {
            kubectl --context $context -n ingress-nginx wait --for=condition=ready pod `
                -l app.kubernetes.io/component=controller --timeout=15s
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $deadline)
    }
    Invoke-Step 'Installing metrics-server' {
        # What the HorizontalPodAutoscalers in k8s/base read. Without it every HPA reports
        # "unknown" for its metric and never scales - and reports it quietly, as a condition on
        # the HPA rather than as a failure of this script, which is why it is installed here
        # rather than left as a manual prerequisite.
        kubectl --context $context apply -f https://github.com/kubernetes-sigs/metrics-server/releases/latest/download/components.yaml
        Assert-LastExitCode 'kubectl apply (metrics-server)'

        # kind's kubelets serve their metrics endpoint with a self-signed certificate that
        # metrics-server does not trust, so out of the box it never becomes ready and every HPA
        # stays on "unknown" forever. --kubelet-insecure-tls is the documented answer for local
        # clusters; it is not a pattern to copy into an overlay that targets a real environment.
        #
        # From a file, never inline with -p. This script supports Windows PowerShell 5.1 (#requires
        # -Version 5.1), and local-run/control-panel.bat reaches it through start-cluster.ps1 under
        # powershell.exe. 5.1 strips the embedded double quotes from an argument passed to a native
        # program: kubectl received [{op:add,...}] and rejected it as "The request is invalid",
        # failing every -CreateCluster. PowerShell 7.3+ passes the quotes through, which is why it
        # worked there. A file is read by kubectl itself, so no shell quoting rule reaches the JSON.
        $patchFile = Join-Path ([System.IO.Path]::GetTempPath()) 'aiframework-metrics-server-patch.json'
        Set-Content -Path $patchFile -Encoding ascii -Value `
            '[{"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--kubelet-insecure-tls"}]'
        try {
            kubectl --context $context -n kube-system patch deployment metrics-server --type=json `
                --patch-file $patchFile
            Assert-LastExitCode 'kubectl patch (metrics-server)'
        }
        finally {
            Remove-Item -Path $patchFile -ErrorAction SilentlyContinue
        }

        kubectl --context $context -n kube-system rollout status deployment/metrics-server --timeout=180s
        Assert-LastExitCode 'kubectl rollout status (metrics-server)'
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
    Invoke-Step 'Building the worker image' {
        docker build -f (Join-Path $repoRoot 'Dockerfile.api') --target worker `
            -t aiframework-worker:local $repoRoot
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
        aiframework-worker:local aiframework-web:local --name $cluster
}

$overlay = Join-Path $repoRoot $(
    if ($WithObservability) { 'k8s/overlays/local-observability' } else { 'k8s/overlays/local' }
)

# Kustomize has no hook mechanism, so the migrate-before-rollout ordering that Helm would
# express as a pre-upgrade hook has to be done here instead — the acknowledged cost of choosing
# Kustomize over Helm for this deployment. A single `kubectl apply -k` applies every resource
# from the base and overlay kustomizations at once, which creates the api Deployment (2
# replicas) alongside Postgres and the migrate Job in the same call, with nothing to stop those
# api pods from receiving traffic against an unmigrated schema: readiness does not catch this,
# because `/health/ready` only checks that Postgres is reachable, not that the schema is
# current. So instead of one `apply -k`, the overlay is rendered once into its multi-document
# YAML stream, split by each document's top-level `kind`, and applied in three phases with the
# Postgres and migration waits between them. Do not "simplify" this back into one `apply -k` —
# that is exactly the ordering bug this block exists to avoid.
Invoke-Step 'Rendering the overlay' {
    $script:renderedLines = kubectl kustomize $overlay
    Assert-LastExitCode 'kubectl kustomize'
}
$rendered = $renderedLines -join "`n"
$documents = [regex]::Split($rendered, '(?m)^---\r?$') | Where-Object { $_.Trim() -ne '' }

$passA = New-Object System.Collections.Generic.List[string]  # everything but Job/Deployment/Ingress
$passB = New-Object System.Collections.Generic.List[string]  # every Job: migrate, and -WithObservability's opensearch-ism-policy
$passC = New-Object System.Collections.Generic.List[string]  # Deployments and the Ingress

# Every Job's name, for the delete-before-apply below — not just "migrate" by itself.
# -WithObservability adds a second one (k8s/components/observability/ism-policy-job.yaml), and
# the same reason applies to it: a completed Job has immutable fields, so a plain re-apply on a
# second deploy fails, migrate or not.
$jobNames = New-Object System.Collections.Generic.List[string]

foreach ($doc in $documents) {
    $kindMatch = [regex]::Match($doc, '(?m)^kind:\s*(\S+)')
    if (-not $kindMatch.Success) { throw 'Could not find a top-level kind: in a rendered document.' }
    switch ($kindMatch.Groups[1].Value) {
        'Job' {
            $passB.Add($doc)
            # Exactly two spaces: kubectl kustomize's output indents every metadata.* field
            # this way, and "namespace:" cannot match here — "name:" (colon immediately after)
            # is not a substring of "namespace:", whose fifth character is "s", not ":".
            $nameMatch = [regex]::Match($doc, '(?m)^  name:\s*(\S+)$')
            if (-not $nameMatch.Success) { throw 'Could not find metadata.name in a rendered Job document.' }
            $jobNames.Add($nameMatch.Groups[1].Value)
        }
        'Deployment' { $passC.Add($doc) }
        'Ingress' { $passC.Add($doc) }
        default { $passA.Add($doc) }
    }
}

# 1. Namespace, config, secrets, services, and Postgres — everything migrations and the
#    application both depend on, but neither the migrate Job nor a Deployment itself.
Invoke-Step 'Phase A: namespace, config, secrets, services, and postgres' {
    ($passA -join "`n---`n") | kubectl --context $context apply -f -
    Assert-LastExitCode 'kubectl apply (phase A)'
}

# 2. Postgres must answer before migrations can run. Addressed as a named object rather than
#    through a label selector on purpose: phase A created the StatefulSet microseconds ago and
#    its controller may not have created the pod yet, and `kubectl wait -l app=postgres` against
#    zero currently-matching resources errors out immediately instead of polling — the same race
#    the ingress-nginx wait above works around with a retry loop. `rollout status` addresses
#    statefulset/postgres itself, so it polls, and for a one-replica StatefulSet "rolled out"
#    already means the pod is ready. Do not swap this back to a label selector.
Invoke-Step 'Waiting for postgres' {
    kubectl --context $context -n $namespace rollout status statefulset/postgres --timeout=180s
}

# 2b. The broker too: both hosts refuse to start without it (ADR 0026), so letting phase C roll out
#     first would only buy CrashLoopBackOff and its growing restart delays. Named, not labelled, for
#     the reason the postgres wait above gives.
Invoke-Step 'Waiting for rabbitmq' {
    kubectl --context $context -n $namespace rollout status statefulset/rabbitmq --timeout=180s
}

# 3. Every Job, deleted then reapplied — a completed Job has immutable fields, so a plain
#    re-apply fails on the second deployment, for migrate and for -WithObservability's
#    opensearch-ism-policy alike. Only migrate is actually waited on below: nothing in Phase C
#    depends on the ISM policy existing within any particular window, only eventually (its own
#    Job has a wait-for-OpenSearch retry loop of its own — see ism-policy-job.yaml), so blocking
#    the deploy on it would only make this slower for no correctness gained.
Invoke-Step 'Phase B: running migrations' {
    foreach ($jobName in $jobNames) {
        kubectl --context $context -n $namespace delete job $jobName --ignore-not-found
        Assert-LastExitCode "kubectl delete job $jobName"
    }
    ($passB -join "`n---`n") | kubectl --context $context apply -f -
    Assert-LastExitCode 'kubectl apply (phase B)'
    kubectl --context $context -n $namespace wait --for=condition=complete job/migrate --timeout=180s
    if ($LASTEXITCODE -ne 0) {
        # `wait --for=condition=complete` does not short-circuit when the Job fails: a migration
        # that dies on its first pod still burns the whole 180s and then reports a timeout,
        # which names the symptom and not the cause. Print what the migrator actually said
        # before throwing, so the failure is diagnosable from the deploy output alone.
        kubectl --context $context -n $namespace logs job/migrate --tail=50
        throw 'kubectl wait job/migrate failed; the migrate Job log is above.'
    }
}

# 4. Only now do the api and web Deployments, and the Ingress, exist — onto the migrated schema.
Invoke-Step 'Phase C: applying deployments and ingress' {
    ($passC -join "`n---`n") | kubectl --context $context apply -f -
    Assert-LastExitCode 'kubectl apply (phase C)'
}

# On a redeploy, where only the `:local` image contents changed underneath an unchanged pod
# template, phase C's apply produces no diff for kubectl to act on — this restart is what
# actually rolls the new image out. `rollout restart` is never literally a no-op: it stamps a
# fresh annotation onto the pod template, which supersedes whatever ReplicaSet exists. On a
# first deploy that means it supersedes the one phase C created seconds ago, and the practical
# cost is near nil only because no pod of that ReplicaSet has become ready yet.
Invoke-Step 'Rolling out the application' {
    kubectl --context $context -n $namespace rollout restart deployment/api deployment/worker deployment/web
    Assert-LastExitCode 'kubectl rollout restart'
    kubectl --context $context -n $namespace rollout status deployment/api --timeout=300s
    Assert-LastExitCode 'kubectl rollout status (api)'
    kubectl --context $context -n $namespace rollout status deployment/worker --timeout=300s
    Assert-LastExitCode 'kubectl rollout status (worker)'
    kubectl --context $context -n $namespace rollout status deployment/web --timeout=300s
    Assert-LastExitCode 'kubectl rollout status (web)'

    # otel-collector's pod template carries no hash of its ConfigMap's content, exactly like
    # api/web above, so an edit to k8s/components/observability/otel-collector.yaml's embedded
    # config is applied to the ConfigMap object by phase C but never reaches the running
    # collector process without this: `kubectl apply` alone sees an unchanged Deployment spec
    # and triggers nothing, and the collector does not hot-reload its config file on a change to
    # the mounted volume. Silent otherwise — the collector keeps running on stale config with no
    # error anywhere.
    if ($WithObservability) {
        kubectl --context $context -n $namespace rollout restart deployment/otel-collector
        Assert-LastExitCode 'kubectl rollout restart (otel-collector)'
        kubectl --context $context -n $namespace rollout status deployment/otel-collector --timeout=300s
        Assert-LastExitCode 'kubectl rollout status (otel-collector)'
    }
}

$kindConfig = Get-Content (Join-Path $PSScriptRoot 'kind-cluster.yaml') -Raw
$httpsHostPort = if ($kindConfig -match '(?ms)containerPort:\s*443\s*\r?\n\s*hostPort:\s*(\d+)') {
    $matches[1]
}
else {
    443
}
$readyUrl = if ($httpsHostPort -eq 443) {
    'https://aiframework.localtest.me'
}
else {
    "https://aiframework.localtest.me:$httpsHostPort"
}

Write-Host ''
Write-Host "Ready: $readyUrl" -ForegroundColor Green
Write-Host 'The certificate is self-signed, so the browser will warn once.' -ForegroundColor DarkGray

if ($WithObservability) {
    # No Ingress for this — it is an opt-in rehearsal tool, not part of the application surface
    # the TLS ingress fronts, so port-forward is the plain way in rather than adding a third host
    # or path to k8s/base/ingress.yaml for it.
    Write-Host ''
    Write-Host 'OpenSearch Dashboards: kubectl --context ' -NoNewline -ForegroundColor DarkGray
    Write-Host "$context -n $namespace port-forward svc/opensearch-dashboards 5601:5601" -ForegroundColor DarkGray
    Write-Host '  then open http://localhost:5601' -ForegroundColor DarkGray
}
