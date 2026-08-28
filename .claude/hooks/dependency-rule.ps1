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

        $config = Get-HookConfig -HookDir $PSScriptRoot

        $root = Get-Prop -Object $config -Name 'rootNamespace'
        if ([string]::IsNullOrWhiteSpace($root)) { $root = 'AiFramework' }

        # Layer folder names come from hooks.config.json so renaming a layer is a
        # one-line change. Falls back to the canonical four when the config is
        # missing or unreadable - an unreadable config must never silently disarm
        # the rule.
        $layerNames = Get-Prop -Object $config -Name 'layers'
        if ($null -eq $layerNames -or @($layerNames).Count -eq 0) {
            $layerNames = @('Domain', 'Application', 'Infrastructure', 'Api')
        }
        $alternation = (@($layerNames) | ForEach-Object { [regex]::Escape($_) }) -join '|'

        # The optional "(?:[\w.]+\.)?" prefix makes both the bare layout
        # (src/Domain/) and the conventional "dotnet new classlib -o
        # src/AiFramework.Domain" layout (src/AiFramework.Domain/) match. The
        # trailing "/" keeps the layer name a COMPLETE final segment, so
        # src/DomainServices/ is correctly not treated as the Domain layer.
        $layer = $null
        if ($normalized -match "/src/(?:[\w.]+\.)?($alternation)/") {
            $layer = $Matches[1]
        }

        if ($null -ne $layer) {
            # Known gaps (accepted):
            #  - R9: enforcement is using-directive based, per spec 6.1. A fully-
            #    qualified reference with no using at all (e.g. writing
            #    "Microsoft.EntityFrameworkCore.DbSet<T>" inline in Domain, never
            #    adding a "using" line) is not detected by this hook.
            #  - .csproj gap: this hook gates on "\.cs$", so a <ProjectReference>
            #    from Domain.csproj to Infrastructure.csproj - the coarsest possible
            #    violation of the rule - is entirely invisible to it. Project-level
            #    references are carried by review and by the dotnet-reviewer agent.
            # Whole-file and whole-project review is left to the dotnet-reviewer
            # step, not this fast pre-write guard.
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

            # A layer declared in hooks.config.json that has no banned list here
            # simply has nothing to enforce; do not throw under StrictMode.
            $bannedForLayer = @()
            if ($banned.ContainsKey($layer)) { $bannedForLayer = $banned[$layer] }

            $text = Get-WrittenText -Payload $payload

            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $violations = New-Object System.Collections.ArrayList
                foreach ($namespaceName in $bannedForLayer) {
                    $escaped = [regex]::Escape($namespaceName)
                    # matches "using X;", "global using X;", "using static X.Y;"
                    # Known limit (R8, accepted): this is a line-start regex, not a C#
                    # parser, so a "using X;" line inside a /* */ block comment is still
                    # flagged even though it is dead code. Distinguishing that needs real
                    # parsing; over-blocking a rare, harmless case is the safe direction.
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
