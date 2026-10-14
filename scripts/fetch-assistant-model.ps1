<#
.SYNOPSIS
    Downloads the assistant's sentence-embedding model (ADR 0036) into models/assistant.

.DESCRIPTION
    bge-small-en-v1.5, quantised to ONNX (MIT licence), pinned to one Hugging Face commit and
    checked by SHA-256, so every machine, CI run and image embeds with byte-identical weights.
    Idempotent: a file already present with the right hash is kept. The Api project copies the
    folder beside its binaries as assistant-model/ when it exists.
#>
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'models/assistant')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$commit = 'ea104dacec62c0de699686887e3f920caeb4f3e3'
$base = "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/$commit"
$files = @(
    @{ Name = 'model.onnx'; Source = "$base/onnx/model_quantized.onnx"; Sha256 = '6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4' },
    @{ Name = 'vocab.txt'; Source = "$base/vocab.txt"; Sha256 = '07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3' }
)

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

foreach ($file in $files) {
    $path = Join-Path $Destination $file.Name
    if ((Test-Path $path) -and ((Get-FileHash -Algorithm SHA256 $path).Hash -eq $file.Sha256)) {
        Write-Host "ok        $($file.Name)"
        continue
    }

    Write-Host "download  $($file.Name)"
    $partial = "$path.partial"
    Invoke-WebRequest -Uri $file.Source -OutFile $partial -UseBasicParsing
    $actual = (Get-FileHash -Algorithm SHA256 $partial).Hash
    if ($actual -ne $file.Sha256) {
        Remove-Item $partial -Force
        throw "SHA-256 mismatch for $($file.Name): expected $($file.Sha256), got $actual"
    }

    Move-Item -Force $partial $path
}
