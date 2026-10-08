<#
.SYNOPSIS
    Generates a throwaway PKI for external-system development into .certs/ (git-ignored).
.DESCRIPTION
    Writes ca.pem, client.pfx/.pass and server.pfx/.pass using tests/PartnerSimulator's TestPki,
    so dev, CI and tests share one generator. Never commit the output. ADR 0031.
.PARAMETER Force
    Overwrite an existing .certs/ directory.
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root '.certs'

if ((Test-Path $out) -and -not $Force) {
    Write-Host ".certs/ already exists. Re-run with -Force to replace it."
    exit 0
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }

dotnet run --project (Join-Path $root 'tests/PartnerSimulator') -- generate-certs $out
if ($LASTEXITCODE -ne 0) { throw "Certificate generation failed." }
