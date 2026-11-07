#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][Guid]$RequestId)
# Read-only discovery. No guest code execution, file retrieval, credentials or machine changes.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destination=Join-Path $root 'artifacts/sandbox-access.json'
$result=[ordered]@{FormatVersion=1;RequestId=$RequestId.ToString();ObservedUtc=[DateTimeOffset]::UtcNow.ToString('O');State='Failed';Systems=@();ErrorType=$null}
try {
    $raw=(& "$env:WINDIR/System32/hcsdiag.exe" list -raw)-join "`n"
    if($LASTEXITCODE -ne 0 -or $raw.Length -gt 1048576) {throw 'Running Sandbox inventory is unavailable.'}
    $inventory=$raw|ConvertFrom-Json
    $systems=if($inventory.PSObject.Properties.Name -contains 'value') {@($inventory.value)} else {@($inventory)}
    $result.Systems=@($systems|Where-Object {$_.Owner -eq 'WindowsSandbox'}|ForEach-Object {
        if($_.Id -notmatch '^[0-9a-fA-F-]{36}$') {throw 'Unexpected compute-system identity.'}
        [ordered]@{Id=$_.Id;Owner=$_.Owner;State=$_.State}
    })
    $result.State='Complete'
} catch {$result.ErrorType=$_.Exception.GetType().Name}
$temporary=$destination+'.tmp'
try {$result|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $temporary -Encoding utf8;Move-Item -LiteralPath $temporary -Destination $destination -Force}
finally {if(Test-Path -LiteralPath $temporary) {Remove-Item -LiteralPath $temporary -Force}}
if($result.State -ne 'Complete') {exit 1}
