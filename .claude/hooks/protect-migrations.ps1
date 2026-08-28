#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\payload.ps1')

$denyMessage = $null

try {
    $payload = Read-HookPayload
    $toolName = Get-Prop -Object $payload -Name 'tool_name'

    # Write is included deliberately: it overwrites an existing file just as
    # readily as it creates a new one, so restricting this guard to Edit/MultiEdit
    # left a trivial bypass - reissue the blocked edit as a Write. What separates
    # a legitimate "dotnet ef migrations add" from a mutation is not the tool but
    # whether the target already exists, so that is what is tested below.
    if ($toolName -eq 'Edit' -or $toolName -eq 'MultiEdit' -or $toolName -eq 'Write') {
        $path = Get-TargetPath -Payload $payload

        if ($null -ne $path) {
            $normalized = ConvertTo-ForwardSlash -Path $path

            # Only an ALREADY EXISTING migration is protected. Creating a brand new
            # migration file - what "dotnet ef migrations add" produces - stays allowed.
            if ($normalized -match '/Migrations/[^/]+\.cs$' -and (Test-Path -LiteralPath $path)) {
                $leaf = Split-Path -Leaf $normalized
                $denyMessage = @"
BLOCKED: refusing to modify an existing EF Core migration.

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
