#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory=$true)][Guid]$GuestId)
# Read-only host diagnostics. Does not modify networking, access credentials,
# mount host paths, or retrieve third-party files from the guest.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output=Join-Path $root 'artifacts\sandbox-isolation-inspection.json'
$state=[ordered]@{FormatVersion=1;ObservedUtc=[DateTimeOffset]::UtcNow.ToString('O');GuestId=$GuestId.ToString();Stage='Inspecting';ComputeSystems=$null;Endpoints=$null;VmCreators=$null;ErrorType=$null;ErrorMessage=$null}
try {
    $raw=& (Join-Path $env:WINDIR 'System32\hcsdiag.exe') list -raw
    if($LASTEXITCODE -ne 0) { throw 'Microsoft compute diagnostics could not enumerate running guests.' }
    $state.ComputeSystems=($raw -join "`n") | ConvertFrom-Json
    $state.Endpoints=@(Get-HnsEndpoint | Select-Object Id,Name,VirtualNetwork,IPAddress,Policies,SharedContainers,State)
    $state.VmCreators=@(Get-NetFirewallHyperVVMCreator | Select-Object VMCreatorId,FriendlyName)
    $state.Stage='Complete'
} catch {
    $state.Stage='Failed'; $state.ErrorType=$_.Exception.GetType().Name
    $state.ErrorMessage=$_.Exception.Message.Substring(0,[Math]::Min(500,$_.Exception.Message.Length))
} finally {
    $json=$state | ConvertTo-Json -Depth 20
    if($json.Length -gt 1048576) { throw 'Sandbox metadata exceeded its 1 MiB diagnostic bound.' }
    $temporary=$output+'.tmp'
    [IO.File]::WriteAllText($temporary,$json,[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $output -Force
}
if($state.Stage -eq 'Failed') { exit 1 }
