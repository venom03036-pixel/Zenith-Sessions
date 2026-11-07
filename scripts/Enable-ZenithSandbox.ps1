#Requires -RunAsAdministrator
# Enables only Microsoft's disposable Windows VM feature. It never restarts Windows,
# downloads third-party software, launches a sandbox, or accesses account credentials.
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output=Join-Path $root 'artifacts\sandbox-setup-status.json'
$temporary=$output+'.tmp'
$state=[ordered]@{ FormatVersion=2; ProcessId=$PID; StartedUtc=[DateTimeOffset]::UtcNow.ToString('O'); Feature='Containers-DisposableClientVM'; Stage='Inspecting'; Before=$null; After=$null; RestartNeeded=$null; ErrorType=$null; ErrorHResult=$null; ErrorMessage=$null; CbsRestartPending=$null; UpdateRestartPending=$null; CompletedUtc=$null }
function Save-State {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    $state | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $output -Force
}
try {
    Save-State
    $before=Get-WindowsOptionalFeature -Online -FeatureName $state.Feature
    $state.Before=$before.State.ToString()
    if($before.State -eq 'Enabled') { $state.After='Enabled'; $state.RestartNeeded=$false; $state.Stage='Complete' }
    elseif($before.State -eq 'EnablePending') { $state.After='EnablePending'; $state.RestartNeeded=$true; $state.Stage='Restart required' }
    else {
        $state.Stage='Enabling Microsoft Windows Sandbox'; Save-State
        $result=Enable-WindowsOptionalFeature -Online -FeatureName $state.Feature -All -NoRestart
        $state.RestartNeeded=[bool]$result.RestartNeeded
        $state.After=(Get-WindowsOptionalFeature -Online -FeatureName $state.Feature).State.ToString()
        $state.Stage=if($state.RestartNeeded -or $state.After -eq 'EnablePending') { 'Restart required' } else { 'Complete' }
    }
} catch {
    $state.Stage='Failed'; $state.ErrorType=$_.Exception.GetType().Name
    $state.ErrorHResult='0x{0:X8}' -f $_.Exception.HResult
    $state.ErrorMessage=$_.Exception.Message.Substring(0,[Math]::Min(500,$_.Exception.Message.Length))
    try { $state.After=(Get-WindowsOptionalFeature -Online -FeatureName $state.Feature).State.ToString() }
    catch { $state.After='Unavailable' }
} finally {
    $state.CbsRestartPending=Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
    $state.UpdateRestartPending=Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
    if($state.CbsRestartPending -or $state.UpdateRestartPending) { $state.RestartNeeded=$true }
    $state.CompletedUtc=[DateTimeOffset]::UtcNow.ToString('O'); Save-State
}
if($state.Stage -eq 'Failed') { exit 1 }
