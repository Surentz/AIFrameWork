#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\payload.ps1')

function Test-IsPlaceholder {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $true }
    $trimmed = $Value.Trim()
    if ($trimmed -match '^\$\{.*\}$') { return $true }   # ${DB_PASSWORD}
    if ($trimmed -match '^#\{.*\}$')  { return $true }   # #{OctopusVariable}
    if ($trimmed -match '^%.*%$')     { return $true }   # %ENV_VAR%
    # Bracket-wrapping is the standard placeholder convention (e.g. <your-key-here>),
    # so it is treated as a placeholder unconditionally. Trade-off (R10, accepted):
    # a real secret hand-wrapped in angle brackets, e.g. "<sk-live-...>", also passes.
    if ($trimmed -match '^<.*>$')     { return $true }   # <your-key-here>
    if ($trimmed -match '^(?i)(REPLACE_ME|REPLACEME|CHANGEME|CHANGE_ME|TODO|PLACEHOLDER|SECRET|X{3,}|\*{3,}|\.{3})$') { return $true }
    return $false
}

$denyMessage = $null

try {
    $payload = Read-HookPayload
    $path = Get-TargetPath -Payload $payload

    if ($null -ne $path) {
        $leaf = Split-Path -Leaf (ConvertTo-ForwardSlash -Path $path)

        if ($leaf -match '^(?i)appsettings.*\.json$') {
            $text = Get-WrittenText -Payload $payload

            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $findings = New-Object System.Collections.ArrayList

                # Connection-string style: Password=..., Pwd=..., AccountKey=...
                foreach ($keyword in @('Password', 'Pwd', 'AccountKey')) {
                    $pattern = "(?i)\b$keyword\s*=\s*([^;""\\]*)"
                    foreach ($match in [regex]::Matches($text, $pattern)) {
                        $value = $match.Groups[1].Value
                        if (-not (Test-IsPlaceholder -Value $value)) {
                            [void]$findings.Add("$keyword= (connection string)")
                        }
                    }
                }

                # JSON key style: "ApiKey": "...."
                # Password|Pwd|AccountKey|ConnectionString added under ruling R7: the
                # connection-string detector above only matches "Key=value;" syntax, which
                # never appears in a bare JSON key/value pair such as "Password": "hunter2".
                # Without these here, that shape - the most common one in appsettings.json -
                # had zero coverage. Purpose (stop real secrets reaching committed config)
                # beats the brief's original enumeration.
                $jsonKeys = 'ApiKey|ClientSecret|Secret|Token|SigningKey|PrivateKey|Password|Pwd|AccountKey|ConnectionString'
                foreach ($match in [regex]::Matches($text, "(?i)""($jsonKeys)""\s*:\s*""([^""]*)""")) {
                    $value = $match.Groups[2].Value
                    if (-not (Test-IsPlaceholder -Value $value)) {
                        [void]$findings.Add(('"{0}"' -f $match.Groups[1].Value))
                    }
                }

                if ($text -match '(?i)SharedAccessSignature\s*=') {
                    [void]$findings.Add('SharedAccessSignature=')
                }

                if ($findings.Count -gt 0) {
                    $list = (($findings | Select-Object -Unique) | ForEach-Object { "  - $_" }) -join "`n"
                    $denyMessage = @"
BLOCKED: a real secret is about to be written into $leaf.

File: $path
Detected:
$list

Configuration files are committed; secrets must not be. Use one of:
  dotnet user-secrets set "<Key>" "<value>"     (local development)
  environment variables                        (deployed environments)

Placeholders are fine and pass this check: "", `${ENV_VAR}`, <your-key>, REPLACE_ME.

See section 6.6 of docs/superpowers/specs/2026-08-27-claude-framework-design.md
"@
                }
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
