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
    # left a trivial bypass - reissue the blocked edit as a Write.
    if ($toolName -eq 'Edit' -or $toolName -eq 'MultiEdit' -or $toolName -eq 'Write') {
        $path = Get-TargetPath -Payload $payload

        if ($null -ne $path) {
            $normalized = ConvertTo-ForwardSlash -Path $path

            # What is protected is a migration ALREADY IN GIT HISTORY, not merely one
            # already on disk. Two reasons this is the right line:
            #
            # - It matches the documented rule, which is "never hand-edit an APPLIED
            #   migration" (CLAUDE.md). A migration you just generated and have not
            #   committed has been applied nowhere and shared with nobody, so adjusting
            #   its Up() before the first commit is ordinary authoring - the earlier
            #   Test-Path condition made that legitimate case impossible by any
            #   sanctioned route, which is a guard that teaches people to route around it.
            # - Testing HEAD rather than the disk also closes a hole Test-Path left wide
            #   open: delete the file first and a Write recreating it sailed through,
            #   because the target no longer existed. HEAD still has it.
            #
            # Committed-ness stands in for applied-ness deliberately. The alternative -
            # querying __EFMigrationsHistory - would make this hook need a reachable
            # database, so it would fail open exactly when Docker is down.
            $inHistory = $false

            if ($normalized -match '/Migrations/[^/]+\.cs$') {
                $gitDirectory = Split-Path -Parent $path

                if ([string]::IsNullOrEmpty($gitDirectory)) {
                    $gitDirectory = '.'
                }

                # ls-tree, and its pathspec relative to -C's directory, for two reasons
                # that both cost a debugging round to learn:
                #
                # - No repo-relative path has to be computed. The obvious way to build one,
                #   [System.IO.Path]::GetRelativePath, does NOT exist under Windows
                #   PowerShell 5.1 (.NET Framework). It threw, the outer catch swallowed it,
                #   and the hook failed open on every single input. Do not reintroduce it.
                # - ls-tree prints nothing and exits 0 for a path that is not in HEAD, where
                #   "cat-file -e" writes to stderr - and PowerShell 5.1 turns redirected
                #   native stderr into a terminating error under ErrorActionPreference =
                #   'Stop', which sent every brand-new migration down the fail-closed path.
                #
                # So: output means tracked, no output means not in HEAD.
                $leafName = Split-Path -Leaf $path

                try {
                    $tracked = & git -C $gitDirectory ls-tree HEAD --name-only -- $leafName
                    $inHistory = -not [string]::IsNullOrWhiteSpace(($tracked -join ''))
                }
                catch {
                    # git missing, or not a checkout. Fail closed: an unverifiable
                    # migration edit is refused rather than waved through.
                    $inHistory = $true
                }
            }

            if ($inHistory) {
                $leaf = Split-Path -Leaf $normalized
                $denyMessage = @"
BLOCKED: refusing to modify an EF Core migration that is already committed.

File: $path

Migrations are an append-only history. Editing one that has been applied puts
the database out of sync with the model snapshot, and every other environment
keeps the old version.

Create a new migration instead:
  dotnet ef migrations add <DescriptiveName> --project src/Infrastructure --startup-project src/Infrastructure

A migration you have generated but NOT yet committed is not covered by this
guard - adjust its Up()/Down() freely before the first commit. Once $leaf is in
git history, correct it with a new migration rather than by editing it. Deleting
the file and recreating it does not get you past this: the check is on HEAD.

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
