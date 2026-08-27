#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$TestsDir = $PSScriptRoot
$HookDir  = Split-Path -Parent $TestsDir
$Fixtures = Join-Path $TestsDir 'fixtures'

$script:Passed = 0
$script:Failed = 0

function Invoke-Hook {
    param(
        [Parameter(Mandatory)][string]$Script,
        [Parameter(Mandatory)][string]$Fixture
    )
    $hookPath    = Join-Path $HookDir $Script
    $fixturePath = Join-Path $Fixtures $Fixture
    if (-not (Test-Path -LiteralPath $hookPath))    { return -100 }
    if (-not (Test-Path -LiteralPath $fixturePath)) { return -101 }

    $json = Get-Content -LiteralPath $fixturePath -Raw
    $json | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hookPath | Out-Null
    return $LASTEXITCODE
}

function Assert-Exit {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Script,
        [Parameter(Mandatory)][string]$Fixture,
        [Parameter(Mandatory)][int]$Expected
    )
    $actual = Invoke-Hook -Script $Script -Fixture $Fixture
    if ($actual -eq $Expected) {
        Write-Host "  PASS  $Name"
        $script:Passed++
    } else {
        Write-Host "  FAIL  $Name -- expected exit $Expected, got $actual" -ForegroundColor Red
        $script:Failed++
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Condition
    )
    if ($Condition) {
        Write-Host "  PASS  $Name"
        $script:Passed++
    } else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        $script:Failed++
    }
}

Write-Host ''
Write-Host 'payload.ps1 library'
. (Join-Path $HookDir 'lib\payload.ps1')

$sample = '{"tool_name":"Write","tool_input":{"file_path":"C:\\r\\src\\Domain\\A.cs","content":"using System;"}}' | ConvertFrom-Json
Assert-True -Name 'Get-Prop returns a present property'      -Condition ((Get-Prop -Object $sample -Name 'tool_name') -eq 'Write')
Assert-True -Name 'Get-Prop returns null for a missing one'  -Condition ($null -eq (Get-Prop -Object $sample -Name 'nope'))
Assert-True -Name 'Get-Prop tolerates a null object'         -Condition ($null -eq (Get-Prop -Object $null -Name 'x'))
Assert-True -Name 'Get-TargetPath reads file_path'           -Condition ((Get-TargetPath -Payload $sample) -like '*Domain*A.cs')
Assert-True -Name 'Get-WrittenText reads Write content'      -Condition ((Get-WrittenText -Payload $sample) -eq 'using System;')

$edit = '{"tool_name":"Edit","tool_input":{"file_path":"a.cs","old_string":"x","new_string":"using Foo;"}}' | ConvertFrom-Json
Assert-True -Name 'Get-WrittenText reads Edit new_string'    -Condition ((Get-WrittenText -Payload $edit) -eq 'using Foo;')

$multi = '{"tool_name":"MultiEdit","tool_input":{"file_path":"a.cs","edits":[{"new_string":"one"},{"new_string":"two"}]}}' | ConvertFrom-Json
Assert-True -Name 'Get-WrittenText joins MultiEdit edits'    -Condition ((Get-WrittenText -Payload $multi) -match 'one' -and (Get-WrittenText -Payload $multi) -match 'two')

Write-Host ''
Write-Host 'dependency-rule.ps1'
Assert-Exit -Name 'Domain + EF Core is blocked'            -Script 'dependency-rule.ps1' -Fixture 'domain-ef-violation.json'                 -Expected 2
Assert-Exit -Name 'Domain + DataAnnotations is blocked'    -Script 'dependency-rule.ps1' -Fixture 'domain-annotations-violation.json'        -Expected 2
Assert-Exit -Name 'Domain with only System is allowed'     -Script 'dependency-rule.ps1' -Fixture 'domain-clean.json'                        -Expected 0
Assert-Exit -Name 'Application + Infrastructure blocked'   -Script 'dependency-rule.ps1' -Fixture 'application-infrastructure-violation.json' -Expected 2
Assert-Exit -Name 'Api may reference every layer'          -Script 'dependency-rule.ps1' -Fixture 'api-all-layers.json'                      -Expected 0
Assert-Exit -Name 'Malformed payload fails open'           -Script 'dependency-rule.ps1' -Fixture 'malformed.json'                           -Expected 0

Write-Host ''
Write-Host 'no-secrets.ps1'
Assert-Exit -Name 'Populated Password= is blocked'      -Script 'no-secrets.ps1' -Fixture 'appsettings-password-secret.json'      -Expected 2
Assert-Exit -Name 'Password=${VAR} is allowed'          -Script 'no-secrets.ps1' -Fixture 'appsettings-password-placeholder.json' -Expected 0
Assert-Exit -Name 'Populated ApiKey is blocked'         -Script 'no-secrets.ps1' -Fixture 'appsettings-apikey-secret.json'        -Expected 2
Assert-Exit -Name 'Empty and REPLACE_ME are allowed'    -Script 'no-secrets.ps1' -Fixture 'appsettings-apikey-empty.json'         -Expected 0
Assert-Exit -Name 'Non-appsettings files are ignored'   -Script 'no-secrets.ps1' -Fixture 'other-json-with-password.json'         -Expected 0
Assert-Exit -Name 'Malformed payload fails open'        -Script 'no-secrets.ps1' -Fixture 'malformed.json'                        -Expected 0

Write-Host ''
Write-Host 'protect-migrations.ps1'
Assert-Exit -Name 'Editing an existing migration is blocked' -Script 'protect-migrations.ps1' -Fixture 'migration-edit.json'      -Expected 2
Assert-Exit -Name 'Creating a new migration is allowed'      -Script 'protect-migrations.ps1' -Fixture 'migration-write-new.json' -Expected 0
Assert-Exit -Name 'Ordinary .cs edits are untouched'         -Script 'protect-migrations.ps1' -Fixture 'ordinary-cs-edit.json'    -Expected 0
Assert-Exit -Name 'Malformed payload fails open'             -Script 'protect-migrations.ps1' -Fixture 'malformed.json'           -Expected 0

Write-Host ''
Write-Host ("Passed: {0}   Failed: {1}" -f $script:Passed, $script:Failed)
if ($script:Failed -gt 0) { exit 1 }
exit 0
