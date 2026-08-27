#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\payload.ps1')

$denyMessage = $null

try {
    $payload = Read-HookPayload
    $toolName = Get-Prop -Object $payload -Name 'tool_name'

    # Only mutation of an existing file is blocked. Write creates new migrations,
    # which is exactly what "dotnet ef migrations add" produces.
    if ($toolName -eq 'Edit' -or $toolName -eq 'MultiEdit') {
        $path = Get-TargetPath -Payload $payload

        if ($null -ne $path) {
            $normalized = ConvertTo-ForwardSlash -Path $path

            if ($normalized -match '/Migrations/[^/]+\.cs$') {
                $leaf = Split-Path -Leaf $normalized
                $denyMessage = @"
BLOCKED: refusing to edit an existing EF Core migration.

File: $path

Migrations are an append-only history. Editing one that has been applied puts
the database out of sync with the model snapshot, and every other environment
keeps the old version.

Create a new migration instead:
  dotnet ef migrations add <DescriptiveName> --project src/Infrastructure --startup-project src/Api

If this migration has definitely never been applied anywhere, remove it with
"dotnet ef migrations remove" and regenerate it rather than hand-editing $leaf.

See section 6.7 of docs/superpowers/specs/2026-08-27-claude-framework-design.md
"@
            }
        }
    }
} catch {
    $denyMessage = $null
}

if ($null -ne $denyMessage) {
    [Console]::Error.WriteLine($denyMessage)
    exit 2
}
exit 0
