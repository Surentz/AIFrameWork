#requires -Version 5.1
# Shared plumbing for Claude Code hooks. Contains no policy - policy lives in each hook.

function Get-Prop {
    param($Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

function Read-HookPayload {
    # Claude Code writes the hook payload as JSON on stdin.
    # Returns $null when stdin is empty or unparseable, so callers fail open.
    try {
        $raw = [Console]::In.ReadToEnd()
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return ($raw | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Get-HookConfig {
    param([Parameter(Mandatory)][string]$HookDir)
    $path = Join-Path $HookDir 'hooks.config.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Get-TargetPath {
    param($Payload)
    $toolInput = Get-Prop -Object $Payload -Name 'tool_input'
    $path = Get-Prop -Object $toolInput -Name 'file_path'
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    return $path
}

function Get-WrittenText {
    # Every piece of text the pending tool call would put on disk:
    #   Write -> content, Edit -> new_string, MultiEdit -> edits[].new_string
    param($Payload)
    $toolInput = Get-Prop -Object $Payload -Name 'tool_input'
    if ($null -eq $toolInput) { return '' }

    $parts = New-Object System.Collections.ArrayList

    $content = Get-Prop -Object $toolInput -Name 'content'
    if (-not [string]::IsNullOrEmpty($content)) { [void]$parts.Add($content) }

    $newString = Get-Prop -Object $toolInput -Name 'new_string'
    if (-not [string]::IsNullOrEmpty($newString)) { [void]$parts.Add($newString) }

    $edits = Get-Prop -Object $toolInput -Name 'edits'
    if ($null -ne $edits) {
        foreach ($edit in $edits) {
            $value = Get-Prop -Object $edit -Name 'new_string'
            if (-not [string]::IsNullOrEmpty($value)) { [void]$parts.Add($value) }
        }
    }

    if ($parts.Count -eq 0) { return '' }
    return ($parts -join "`n")
}

function Get-RepoRoot {
    param($Payload, [Parameter(Mandatory)][string]$HookDir)
    $cwd = Get-Prop -Object $Payload -Name 'cwd'
    if (-not [string]::IsNullOrWhiteSpace($cwd)) { return $cwd }
    # .claude/hooks -> .claude -> repo root
    return (Split-Path -Parent (Split-Path -Parent $HookDir))
}

function ConvertTo-ForwardSlash {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    return ($Path -replace '\\', '/')
}
