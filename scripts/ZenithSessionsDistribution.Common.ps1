$ErrorActionPreference='Stop'
function Assert-ZenithDistributionPath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    for($part=$full;$part;$part=[IO.Path]::GetDirectoryName($part)) {
        if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Distribution paths cannot contain redirected folders.'}
    }
    if(Test-Path -LiteralPath $full -PathType Container){
        $pending=New-Object 'Collections.Generic.Stack[string]';$pending.Push($full)
        while($pending.Count -gt 0){foreach($item in Get-ChildItem -LiteralPath $pending.Pop() -Force){
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Distribution contains a redirected child.'}
            if($item.PSIsContainer){$pending.Push($item.FullName)}
        }}
    }
    return $full
}
function Get-ZenithDistributionPaths {
    $programs=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
    [pscustomobject]@{Programs=$programs;Install=(Join-Path $programs 'Zenith Sessions');Pending=(Join-Path $programs 'Zenith Sessions.pending');Previous=(Join-Path $programs 'Zenith Sessions.previous');Data=([IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ZenithSessions\Personal')));Shortcut=(Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Zenith Sessions.lnk');Registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ZenithSessionsPersonal'}
}
function Assert-ZenithOwnedInstall([string]$Path) {
    [void](Assert-ZenithDistributionPath $Path)
    $marker=Join-Path $Path '.zenith-install.json'
    if(-not (Test-Path -LiteralPath $marker -PathType Leaf)){throw 'Existing folder has no Zenith installer ownership marker; it was preserved.'}
    $info=Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if($info.Product -ne 'Zenith.Sessions.Personal' -or $info.FormatVersion -ne 1){throw 'Existing folder has an incompatible ownership marker; it was preserved.'}
}
function Assert-ZenithReleaseFiles([string]$Package) {
    $root=Assert-ZenithDistributionPath $Package
    $manifestPath=Join-Path $root 'release-manifest.json'
    if(-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or (Get-Item -LiteralPath $manifestPath).Length -gt 1048576){throw 'Missing or oversized release manifest.'}
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $parsedVersion=$null
    if($manifest.FormatVersion -ne 1 -or $manifest.Product -ne 'Zenith.Sessions.Personal' -or $manifest.ReleaseReady -ne $true -or -not [version]::TryParse($manifest.Version,[ref]$parsedVersion)){throw 'This development preview is not approved for installation.'}
    $files=@($manifest.Files);if($files.Count -lt 1 -or $files.Count -gt 2048){throw 'Invalid release file inventory.'}
    $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $files){
        $name=[string]$entry.Path
        if([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name) -or $name -match '(^|[\\/])(\.\.?|[^\\/]*[. ])([\\/]|$)' -or $name -match '[:*?"<>|]' -or -not $seen.Add($name.Replace('/','\'))){throw 'Unsafe or duplicate release path.'}
        $file=[IO.Path]::GetFullPath((Join-Path $root $name))
        if(-not $file.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $entry.Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or -not (Test-Path -LiteralPath $file -PathType Leaf)){throw 'Release file is missing or outside the package.'}
        if((Get-Item -LiteralPath $file).Length -ne $entry.Length -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256){throw 'Release file integrity failed.'}
    }
    foreach($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force){
        $relative=$file.FullName.Substring($root.TrimEnd('\').Length+1)
        if($relative -notin @('release-manifest.json','.zenith-install.json') -and -not $seen.Contains($relative)){throw 'Release contains an unlisted file.'}
    }
    foreach($required in @('Zenith.Sessions.App.exe','Zenith.Sessions.App.dll','Zenith.Sessions.App.runtimeconfig.json','Zenith.Sessions.App.deps.json','Zenith.Sessions.Core.dll','Zenith.Sessions.Windows.dll','Uninstall-ZenithSessions.ps1','ZenithSessionsDistribution.Common.ps1')){if(-not $seen.Contains($required)){throw 'A required application/uninstaller file is missing.'}}
    return $manifest
}
function Assert-ZenithPublisher([string]$Path,[string]$CertificateSha256) {
    if($CertificateSha256 -notmatch '^[A-Fa-f0-9]{64}$'){throw 'An explicitly reviewed release publisher SHA-256 certificate fingerprint is required.'}
    $signature=Get-AuthenticodeSignature -LiteralPath $Path
    if($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate){throw 'The release tool has no valid Authenticode signature.'}
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$actual=[BitConverter]::ToString($sha.ComputeHash($signature.SignerCertificate.RawData)).Replace('-','')}finally{$sha.Dispose()}
    if($actual -ne $CertificateSha256){throw 'The release publisher differs from the approved certificate.'}
}
function Test-ZenithDesktopRuntime {
    $shared=Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
    if(-not (Test-Path -LiteralPath $shared)){return $false}
    return [bool](@(Get-ChildItem -LiteralPath $shared -Directory | Where-Object {$_.Name -match '^10\.\d+\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'PresentationFramework.dll'))}).Count)
}
function Test-ZenithWebView2 {
    $id='{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    foreach($path in @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$id","HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$id","HKCU:\Software\Microsoft\EdgeUpdate\Clients\$id")){
        $entry=Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
        if($null -ne $entry -and $entry.pv -match '^\d+\.\d+\.\d+\.\d+$' -and [version]$entry.pv -gt [version]'0.0.0.0'){return $true}
    }
    return $false
}
function Assert-ZenithClosed {
    if(Get-Process -Name 'Zenith.Sessions.App' -ErrorAction SilentlyContinue){throw 'Close Zenith Sessions first so its queue and stores have stopped. No process will be terminated by this installer.'}
}
function Promote-ZenithRelease([string]$Source,[string]$Install,[string]$Pending,[string]$Previous,[string]$VerifiedInventorySha256) {
    # Callers supply the three fixed sibling paths. Test fixtures use one fresh isolated parent.
    $parent=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Install))
    foreach($path in @($Install,$Pending,$Previous)){if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $parent){throw 'Promotion paths must be fixed siblings.'};[void](Assert-ZenithDistributionPath $path)}
    if([IO.Path]::GetFileName($Install) -ne 'Zenith Sessions' -or [IO.Path]::GetFileName($Pending) -ne 'Zenith Sessions.pending' -or [IO.Path]::GetFileName($Previous) -ne 'Zenith Sessions.previous'){throw 'Unexpected promotion destination.'}
    if($VerifiedInventorySha256 -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath (Join-Path $Source 'release-manifest.json')).Hash -ne $VerifiedInventorySha256){throw 'The release inventory differs from its freshly verified signed ZIP provenance.'}
    $manifest=Assert-ZenithReleaseFiles $Source
    if(Test-Path -LiteralPath $Pending){throw 'A pending installation already exists. Preserve it for recovery before retrying.'}
    if(Test-Path -LiteralPath $Previous){throw 'A previous installation is retained. Review recovery before replacing it.'}
    if(Test-Path -LiteralPath $Install){Assert-ZenithOwnedInstall $Install}
    New-Item -ItemType Directory -Path $Pending -Force | Out-Null
    $moved=$false;$promoted=$false
    try{
        Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Pending -Recurse -Force
        if((Get-FileHash -LiteralPath (Join-Path $Pending 'release-manifest.json')).Hash -ne $VerifiedInventorySha256){throw 'Release inventory changed during promotion.'}
        [void](Assert-ZenithReleaseFiles $Pending)
        [IO.File]::WriteAllText((Join-Path $Pending '.zenith-install.json'),('{"FormatVersion":1,"Product":"Zenith.Sessions.Personal","Version":"'+$manifest.Version+'"}'))
        if(Test-Path -LiteralPath $Install){Move-Item -LiteralPath $Install -Destination $Previous;$moved=$true}
        Move-Item -LiteralPath $Pending -Destination $Install
        $promoted=$true
        [void](Assert-ZenithReleaseFiles $Install)
        return $manifest
    }catch{
        if($promoted -and (Test-Path -LiteralPath $Install) -and -not (Test-Path -LiteralPath $Pending)){Move-Item -LiteralPath $Install -Destination $Pending}
        if($moved -and -not (Test-Path -LiteralPath $Install)){Move-Item -LiteralPath $Previous -Destination $Install}
        # Preserve partial files for review; never erase the prior installation or user data on failure.
        throw
    }
}
function Get-ZenithRegistrationSnapshot($Paths) {
    $shortcut=$null
    if(Test-Path -LiteralPath $Paths.Shortcut){if((Get-Item -LiteralPath $Paths.Shortcut).Length -gt 1048576){throw 'The existing shortcut is unexpectedly large.'};$shortcut=[IO.File]::ReadAllBytes($Paths.Shortcut)}
    $values=@();$exists=Test-Path -LiteralPath $Paths.Registry
    if($exists){$key=Get-Item -LiteralPath $Paths.Registry;try{foreach($name in $key.GetValueNames()){$values+=@([pscustomobject]@{Name=$name;Value=$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);Kind=$key.GetValueKind($name).ToString()})}}finally{$key.Dispose()}}
    return [pscustomobject]@{Shortcut=$shortcut;RegistryExisted=$exists;Values=$values}
}
function Restore-ZenithRegistration($Paths,$Snapshot) {
    if($null -eq $Snapshot.Shortcut){if(Test-Path -LiteralPath $Paths.Shortcut){Remove-Item -LiteralPath $Paths.Shortcut -Force}}
    else{[IO.File]::WriteAllBytes($Paths.Shortcut,$Snapshot.Shortcut)}
    if(Test-Path -LiteralPath $Paths.Registry){Remove-Item -LiteralPath $Paths.Registry -Recurse -Force}
    if($Snapshot.RegistryExisted){
        New-Item -Path $Paths.Registry -Force | Out-Null
        $key=Get-Item -LiteralPath $Paths.Registry
        try{foreach($entry in $Snapshot.Values){$key.SetValue($entry.Name,$entry.Value,[Microsoft.Win32.RegistryValueKind]$entry.Kind)}}finally{$key.Dispose()}
    }
}
function Register-ZenithInstallation($Paths,[string]$Version) {
    $shell=New-Object -ComObject WScript.Shell;$shortcut=$null
    try{$shortcut=$shell.CreateShortcut($Paths.Shortcut);$shortcut.TargetPath=Join-Path $Paths.Install 'Zenith.Sessions.App.exe';$shortcut.WorkingDirectory=$Paths.Install;$shortcut.Save()}
    finally{if($null -ne $shortcut){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)};[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)}
    New-Item -Path $Paths.Registry -Force | Out-Null
    $uninstall='powershell.exe -NoProfile -File "'+(Join-Path $Paths.Install 'Uninstall-ZenithSessions.ps1')+'"'
    foreach($entry in @{DisplayName='Zenith Sessions Personal';DisplayVersion=$Version;Publisher='Zenith';InstallLocation=$Paths.Install;UninstallString=$uninstall;DisplayIcon=(Join-Path $Paths.Install 'Zenith.Sessions.App.exe')}.GetEnumerator()){New-ItemProperty -LiteralPath $Paths.Registry -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null}
    New-ItemProperty -LiteralPath $Paths.Registry -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $Paths.Registry -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
}
function Complete-ZenithRegistration($Paths,[scriptblock]$Register,[scriptblock]$Restore) {
    try{& $Register}
    catch{
        $original=$_;$recovery=@()
        try{& $Restore}catch{$recovery+=@($_.Exception.GetType().Name)}
        try{
            foreach($path in @($Paths.Install,$Paths.Pending,$Paths.Previous)){[void](Assert-ZenithDistributionPath $path)}
            Assert-ZenithOwnedInstall $Paths.Install
            if(Test-Path -LiteralPath $Paths.Pending){throw 'Recovery stage already exists.'}
            if(Test-Path -LiteralPath $Paths.Previous){Assert-ZenithOwnedInstall $Paths.Previous}
            Move-Item -LiteralPath $Paths.Install -Destination $Paths.Pending
            if(Test-Path -LiteralPath $Paths.Previous){Move-Item -LiteralPath $Paths.Previous -Destination $Paths.Install}
        }catch{$recovery+=@($_.Exception.GetType().Name)}
        if($recovery.Count -gt 0){throw ('Installation registration failed and rollback needs manual recovery: '+($recovery -join ', ')+'. Previous/pending files were preserved.')}
        throw $original
    }
}
