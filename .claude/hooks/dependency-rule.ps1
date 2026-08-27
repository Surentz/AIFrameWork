#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\payload.ps1')

$denyMessage = $null

try {
    $payload = Read-HookPayload
    $path = Get-TargetPath -Payload $payload

    if ($null -ne $path -and $path -match '\.cs$') {
        $normalized = ConvertTo-ForwardSlash -Path $path

        $layer = $null
        if ($normalized -match '/src/(Domain|Application|Infrastructure|Api)/') {
            $layer = $Matches[1]
        }

        if ($null -ne $layer) {
            $config = Get-HookConfig -HookDir $PSScriptRoot
            $root = Get-Prop -Object $config -Name 'rootNamespace'
            if ([string]::IsNullOrWhiteSpace($root)) { $root = 'AiFramework' }

            $banned = @{
                'Domain' = @(
                    "$root.Application", "$root.Infrastructure", "$root.Api",
                    'Microsoft.EntityFrameworkCore', 'Microsoft.AspNetCore',
                    'Microsoft.Extensions.DependencyInjection', 'System.Data',
                    'System.ComponentModel.DataAnnotations'
                )
                'Application' = @(
                    "$root.Infrastructure", "$root.Api",
                    'Microsoft.EntityFrameworkCore', 'Microsoft.AspNetCore'
                )
                'Infrastructure' = @("$root.Api")
                'Api' = @()
            }

            $text = Get-WrittenText -Payload $payload

            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $violations = New-Object System.Collections.ArrayList
                foreach ($namespaceName in $banned[$layer]) {
                    $escaped = [regex]::Escape($namespaceName)
                    # matches "using X;", "global using X;", "using static X.Y;"
                    if ($text -match "(?m)^\s*(global\s+)?using\s+(static\s+)?$escaped\b") {
                        [void]$violations.Add($namespaceName)
                    }
                }

                if ($violations.Count -gt 0) {
                    $list = ($violations | ForEach-Object { "  - $_" }) -join "`n"
                    $denyMessage = @"
BLOCKED: Clean Architecture dependency rule violation in the $layer layer.

File: $path
Banned namespace(s) referenced:
$list

The $layer layer must not reference these. Depend on an abstraction instead:
put the interface in Application and the implementation in Infrastructure.

See src/$layer/CLAUDE.md, and section 6.1 of
docs/superpowers/specs/2026-08-27-claude-framework-design.md
"@
                }
            }
        }
    }
} catch {
    $denyMessage = $null   # fail open: a bug in this hook must never wedge the session
}

if ($null -ne $denyMessage) {
    [Console]::Error.WriteLine($denyMessage)
    exit 2
}
exit 0
