[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseTool,[Parameter(Mandatory)][uri]$FeedUri,[Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$PublisherCertificateSha256,[int]$WaitForProcessId=0,[long]$WaitForProcessStartTicks=0)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ZenithSessionsDistribution.Common.ps1')
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try{if(([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run this per-user installer from a normal, non-administrator PowerShell window.'}}finally{$identity.Dispose()}
if($WaitForProcessId -gt 0){
    $parent=Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
    if($null -ne $parent){try{
        if($parent.StartTime.ToUniversalTime().Ticks -ne $WaitForProcessStartTicks){throw 'The startup handoff process identity changed. Close Zenith and run the installer explicitly.'}
        if(-not $parent.WaitForExit(10000)){throw 'Zenith has not finished closing. No installation was changed.'}
    }finally{$parent.Dispose()}}
}
$paths=Get-ZenithDistributionPaths
[void](Assert-ZenithDistributionPath $paths.Programs)
Assert-ZenithClosed
if(-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt 19041){throw 'Zenith Sessions requires Windows 10 build 19041 or newer, x64.'}
if(-not (Test-ZenithDesktopRuntime)){throw 'Install the Microsoft .NET 10 Desktop Runtime (x64), then retry: https://dotnet.microsoft.com/download/dotnet/10.0'}
if(-not (Test-ZenithWebView2)){
    $choice=Read-Host 'WebView2 is missing. Download and run the Microsoft-signed Evergreen bootstrapper now? Type INSTALL to continue'
    if($choice -cne 'INSTALL'){throw 'WebView2 is required. Nothing was installed.'}
    $bootstrap=Join-Path ([IO.Path]::GetTempPath()) ('zenith-webview2-'+[Guid]::NewGuid().ToString('N')+'.exe')
    try{
        $request=[Net.HttpWebRequest]::Create('https://go.microsoft.com/fwlink/p/?LinkId=2124703');$request.Timeout=30000;$request.ReadWriteTimeout=30000
        $response=$request.GetResponse()
        try{
            if($response.ContentLength -gt 10485760){throw 'Unexpected WebView2 bootstrapper size.'}
            $input=$response.GetResponseStream();$output=[IO.File]::Open($bootstrap,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try{
                $buffer=New-Object byte[] 65536;$count=0L;$clock=[Diagnostics.Stopwatch]::StartNew()
                while(($read=$input.Read($buffer,0,$buffer.Length)) -gt 0){$count+=$read;if($count -gt 10485760 -or $clock.Elapsed.TotalSeconds -gt 120){throw 'WebView2 bootstrapper exceeded the download budget.'};$output.Write($buffer,0,$read)}
            }finally{$output.Dispose();$input.Dispose()}
        }finally{$response.Dispose()}
        $bootstrapLock=[IO.File]::Open($bootstrap,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try {
        $signature=Get-AuthenticodeSignature -LiteralPath $bootstrap
        if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)'){throw 'The WebView2 bootstrapper is not validly signed by Microsoft.'}
        $process=Start-Process -FilePath $bootstrap -ArgumentList '/install' -PassThru -WindowStyle Hidden
        if(-not $process.WaitForExit(180000)){throw 'WebView2 setup is still running. Finish it manually, then retry.'}
        if($process.ExitCode -ne 0 -or -not (Test-ZenithWebView2)){throw 'WebView2 setup did not complete successfully.'}
        } finally { $bootstrapLock.Dispose() }
    }finally{if(Test-Path -LiteralPath $bootstrap){Remove-Item -LiteralPath $bootstrap -Force}}
}
[void](Assert-ZenithDistributionPath $ReleaseTool)
# The release tool must be published as one signed file, so its embedded verifier/trust cannot be replaced beside the EXE.
$toolLock=[IO.File]::Open([IO.Path]::GetFullPath($ReleaseTool),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try { Assert-ZenithPublisher $ReleaseTool $PublisherCertificateSha256
$version='0.0.0.0'
if(Test-Path -LiteralPath $paths.Install){Assert-ZenithOwnedInstall $paths.Install;$version=(Get-Content -LiteralPath (Join-Path $paths.Install '.zenith-install.json') -Raw | ConvertFrom-Json).Version}
$staging=Join-Path $env:LOCALAPPDATA 'ZenithSessions\Updates'
$result=& $ReleaseTool stage $FeedUri.AbsoluteUri $version $staging
if($LASTEXITCODE -ne 0){throw 'Signed update verification failed. No installation was changed.'}
} finally { $toolLock.Dispose() }
$stage=$result | ConvertFrom-Json
$expectedRoot=[IO.Path]::GetFullPath($staging).TrimEnd('\')+'\'
if(-not ([IO.Path]::GetFullPath($stage.Directory)).StartsWith($expectedRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'The verified stage is outside the update folder.'}
$choice=Read-Host ('Install Zenith Sessions '+$stage.Version+' to '+$paths.Install+'? Type INSTALL to continue')
if($choice -cne 'INSTALL'){Write-Output 'Verified files were staged; installation was not changed.';return}
Assert-ZenithClosed
$registration=Get-ZenithRegistrationSnapshot $paths
$stageLocks=New-Object 'Collections.Generic.List[IO.FileStream]'
try {
    [void](Assert-ZenithDistributionPath $stage.Directory)
    $stageLocks.Add([IO.File]::Open([IO.Path]::GetFullPath($ReleaseTool),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
    Assert-ZenithPublisher $ReleaseTool $PublisherCertificateSha256
    foreach($file in Get-ChildItem -LiteralPath $stage.Directory -File -Recurse -Force){$stageLocks.Add([IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))}
    $proof=& $ReleaseTool verify-stage $stage.Directory $version
    if($LASTEXITCODE -ne 0){throw 'Staged content failed signed ZIP revalidation. The installed application was preserved.'}
    $inventory=$proof | ConvertFrom-Json
    if($inventory.Version -ne $stage.Version){throw 'The verified release version changed after approval.'}
    $manifest=Promote-ZenithRelease (Join-Path $stage.Directory 'content') $paths.Install $paths.Pending $paths.Previous $inventory.ManifestSha256
} finally { foreach($handle in $stageLocks){$handle.Dispose()} }
Complete-ZenithRegistration $paths {Register-ZenithInstallation $paths $manifest.Version} {Restore-ZenithRegistration $paths $registration}
Write-Output 'Installed. The previous version, if any, was retained for recovery. Account data was preserved. Start Zenith Sessions when ready.'
