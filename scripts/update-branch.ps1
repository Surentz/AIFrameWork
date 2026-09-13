#requires -Version 5.1
<#
.SYNOPSIS
  Pulls the latest changes for whatever branch is currently checked out.
.DESCRIPTION
  A thin wrapper around `git fetch` + `git pull --ff-only`, not a rebase or merge helper -
  intentionally the least surprising thing it could do. Refuses to run over uncommitted changes
  (git pull would refuse too, but with a less readable message) and refuses to fast-forward a
  branch that has diverged from its upstream rather than guessing which side should win.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $branch -or $branch -eq 'HEAD') {
        throw 'Not currently on a branch (detached HEAD?) - nothing to pull.'
    }

    $dirty = git status --porcelain
    if ($dirty) {
        throw "Working tree has uncommitted changes on '$branch'. Commit or stash them first, " +
              "then re-run this."
    }

    $upstream = git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $upstream) {
        throw "'$branch' has no upstream tracking branch, so there is nothing to pull from. " +
              "Push it once with `git push -u origin $branch` if you want one."
    }

    Write-Host "==> Fetching" -ForegroundColor Cyan
    git fetch
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed with exit code $LASTEXITCODE." }

    Write-Host "==> Fast-forwarding '$branch' to '$upstream'" -ForegroundColor Cyan
    git pull --ff-only
    if ($LASTEXITCODE -ne 0) {
        throw "git pull --ff-only failed - '$branch' has likely diverged from '$upstream' " +
              "(local commits not on the remote, or vice versa). Resolve that by hand " +
              "(rebase or merge), this script won't guess."
    }

    Write-Host ''
    Write-Host "'$branch' is up to date with '$upstream'." -ForegroundColor Green
}
finally {
    Pop-Location
}
