$ErrorActionPreference='Stop'
$root='C:\ZenithLab'
if([Security.Principal.WindowsIdentity]::GetCurrent().Name -notmatch '\\WDAGUtilityAccount$' -or
    (Test-Path -LiteralPath 'C:\Users\noelm')) { throw 'This setup runs only in the disposable guest.' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$status=[ordered]@{FormatVersion=1;Stage='Copying trusted runtime';StartedUtc=[DateTimeOffset]::UtcNow.ToString('O');RuntimeVersion=$null;RamBytes=$null;TemporaryFolderWritable=$false;ProbeProcessId=$null;ProbeStartedUtc=$null;CompletedUtc=$null;ErrorType=$null;ErrorMessage=$null}
$statusPath=Join-Path $root 'SetupStatus.json'
function Save-Status { $status | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8 }
try {
    Save-Status
    $manifest=Get-Content -LiteralPath 'C:\ZenithSource\manifest.json' -Raw | ConvertFrom-Json
    if($manifest.FormatVersion -ne 1 -or $manifest.Files.Count -lt 10 -or $manifest.Files.Count -gt 1500 -or
        ($manifest.Files | Measure-Object Length -Sum).Sum -gt 2147483648) { throw 'The runtime manifest is invalid.' }
    $status.RuntimeVersion=$manifest.RuntimeVersion
    foreach($file in $manifest.Files) {
        if($file.Path -notmatch '^(Runtime|Check)\\' -or $file.Path -match '(^|[\\/])\.\.([\\/]|$)' -or $file.Path.Contains(':')) { throw 'An unexpected runtime path was rejected.' }
        $source=Join-Path 'C:\ZenithSource' $file.Path
        $target=[IO.Path]::GetFullPath((Join-Path $root $file.Path))
        if(-not $target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The runtime path escaped its guest directory.' }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        [IO.File]::Copy($source,$target,$true)
        if((Get-Item -LiteralPath $target).Length -ne $file.Length -or
            (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.Sha256) { throw 'A copied runtime file did not verify.' }
    }
    $runtime=Join-Path $root 'Runtime'
    $signature=Get-AuthenticodeSignature -LiteralPath (Join-Path $runtime 'msedgewebview2.exe')
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') { throw 'The runtime is not verified Microsoft code.' }
    $temporary=Join-Path $root 'Temp'
    [IO.Directory]::CreateDirectory($temporary) | Out-Null
    foreach($name in @('TEMP','TMP')) {
        [Environment]::SetEnvironmentVariable($name,$temporary,'User')
        [Environment]::SetEnvironmentVariable($name,$temporary,'Process')
    }
    [IO.File]::WriteAllText((Join-Path $temporary 'write-check.txt'),'guest write test')
    $status.TemporaryFolderWritable=[IO.File]::ReadAllText((Join-Path $temporary 'write-check.txt')) -eq 'guest write test'
    if(-not $status.TemporaryFolderWritable) { throw 'The guest temporary directory is not writable.' }
    [Environment]::SetEnvironmentVariable('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER',$runtime,'User')
    [Environment]::SetEnvironmentVariable('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER',$runtime,'Process')
    $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Policies\Microsoft\Edge\WebView2\BrowserExecutableFolder')
    try { $key.SetValue('*',$runtime,[Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
    $status.RamBytes=(Get-CimInstance Win32_ComputerSystem -OperationTimeoutSec 10).TotalPhysicalMemory
    if($status.RamBytes -lt 9GB) { throw 'The guest did not receive the requested memory.' }
    $status.Stage='Runtime copied and configured'; $status.CompletedUtc=[DateTimeOffset]::UtcNow.ToString('O'); Save-Status
    $probe=Start-Process -FilePath (Join-Path $root 'Check\RuntimeCheck.exe') -WorkingDirectory (Join-Path $root 'Check') -PassThru
    try { $status.ProbeProcessId=$probe.Id; $status.ProbeStartedUtc=$probe.StartTime.ToUniversalTime().ToString('O'); Save-Status }
    finally { $probe.Dispose() }
} catch {
    $status.Stage='Failed'; $status.ErrorType=$_.Exception.GetType().Name
    $status.ErrorMessage=$_.Exception.Message.Substring(0,[Math]::Min(400,$_.Exception.Message.Length))
    $status.CompletedUtc=[DateTimeOffset]::UtcNow.ToString('O'); Save-Status
    throw
}
