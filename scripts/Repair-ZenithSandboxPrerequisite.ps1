#Requires -RunAsAdministrator
# Owner-approved repair of Microsoft's component store, followed by the single
# Sandbox feature. No cleanup/reset-base, third-party payloads, VM execution,
# security-setting changes, automatic reboot, or access to account data.
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output=Join-Path $root 'artifacts\sandbox-repair-status.json'
$temporary=$output+'.tmp'
$log=Join-Path $root 'artifacts\sandbox-repair-dism.log'
$state=[ordered]@{
    FormatVersion=1; ProcessId=$PID; StartedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    Stage='Checking Windows component health'; HealthBefore=$null; HealthAfter=$null
    RepairAttempted=$false; Feature='Containers-DisposableClientVM'; FeatureBefore=$null
    FeatureAfter=$null; RestartNeeded=$null; CbsRestartPending=$null
    UpdateRestartPending=$null; ErrorType=$null; ErrorHResult=$null
    ErrorMessage=$null; CompletedUtc=$null
}
function Save-State {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    $state | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $output -Force
}
function Test-RestartPending {
    $state.CbsRestartPending=Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
    $state.UpdateRestartPending=Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
    return ($state.CbsRestartPending -or $state.UpdateRestartPending)
}
try {
    Save-State
    $health=Repair-WindowsImage -Online -CheckHealth -NoRestart -LogPath $log
    $state.HealthBefore=$health.ImageHealthState.ToString()
    if($state.HealthBefore -eq 'NonRepairable') { throw 'Windows reports a non-repairable component store; no feature changes were attempted.' }
    $state.RepairAttempted=$true; $state.Stage='Repairing Windows components through Microsoft DISM'; Save-State
    $repair=Repair-WindowsImage -Online -RestoreHealth -NoRestart -LogPath $log
    $state.RestartNeeded=[bool]$repair.RestartNeeded
    $verified=Repair-WindowsImage -Online -CheckHealth -NoRestart -LogPath $log
    $state.HealthAfter=$verified.ImageHealthState.ToString()
    if($state.HealthAfter -ne 'Healthy') { throw 'Windows repair did not report a healthy component store; Sandbox setup was not retried.' }
    if($state.RestartNeeded -or (Test-RestartPending)) {
        $state.RestartNeeded=$true; $state.Stage='Restart required before retrying Sandbox'
    } else {
        $before=Get-WindowsOptionalFeature -Online -FeatureName $state.Feature -LogPath $log
        $state.FeatureBefore=$before.State.ToString()
        if($before.State -eq 'Enabled') {
            $state.FeatureAfter='Enabled'; $state.Stage='Complete'
        } elseif($before.State -eq 'EnablePending') {
            $state.FeatureAfter='EnablePending'; $state.RestartNeeded=$true; $state.Stage='Restart required'
        } else {
            $state.Stage='Enabling Microsoft Windows Sandbox'; Save-State
            $enabled=Enable-WindowsOptionalFeature -Online -FeatureName $state.Feature -All -NoRestart -LogPath $log
            $state.RestartNeeded=[bool]$enabled.RestartNeeded
            $state.FeatureAfter=(Get-WindowsOptionalFeature -Online -FeatureName $state.Feature -LogPath $log).State.ToString()
            if($state.FeatureAfter -notin @('Enabled','EnablePending')) { throw 'Sandbox did not reach an enabled or restart-pending state.' }
            $state.Stage=if($state.RestartNeeded -or $state.FeatureAfter -eq 'EnablePending') { 'Restart required' } else { 'Complete' }
        }
    }
} catch {
    $state.Stage='Failed'; $state.ErrorType=$_.Exception.GetType().Name
    $state.ErrorHResult='0x{0:X8}' -f $_.Exception.HResult
    $state.ErrorMessage=$_.Exception.Message.Substring(0,[Math]::Min(500,$_.Exception.Message.Length))
    try { $state.FeatureAfter=(Get-WindowsOptionalFeature -Online -FeatureName $state.Feature -LogPath $log).State.ToString() }
    catch { $state.FeatureAfter='Unavailable' }
} finally {
    if(Test-RestartPending) { $state.RestartNeeded=$true }
    $state.CompletedUtc=[DateTimeOffset]::UtcNow.ToString('O'); Save-State
}
if($state.Stage -eq 'Failed') { exit 1 }
