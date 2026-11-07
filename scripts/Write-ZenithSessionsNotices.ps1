param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destinationPath=[IO.Path]::GetFullPath($Destination)
if([IO.Path]::GetDirectoryName($destinationPath) -ne (Join-Path $root 'artifacts') -or [IO.Path]::GetFileName($destinationPath) -notin @('sessions-manager-stage','sessions-manager-candidate','sessions-manager-review')){throw 'Notices may only be written to a fixed companion staging folder.'}
$assets=Get-Content -LiteralPath (Join-Path $root 'src\Zenith.Sessions.App\obj\project.assets.json') -Raw|ConvertFrom-Json
$packages=@();$lines=[Collections.Generic.List[string]]::new()
$lines.Add('ZENITH SESSIONS — THIRD-PARTY NOTICES')
$lines.Add('Production NuGet dependencies and the Windows SDK runtime projection. The Microsoft Windows Desktop Runtime is required separately and is not bundled.')
$lines.Add('RoALT was inspected as a reference; no RoALT source or binary is redistributed in this companion.')
foreach($entry in @($assets.libraries.PSObject.Properties|Where-Object{$_.Value.type -eq 'package'}|Sort-Object Name)) {
    $packageRoot=$null
    foreach($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate=Join-Path $folder $entry.Value.path
        if(Test-Path -LiteralPath $candidate -PathType Container){$packageRoot=$candidate;break}
    }
    if(!$packageRoot){throw 'A resolved production dependency is missing from the local package cache.'}
    $spec=@(Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' -File)
    if($spec.Count -ne 1 -or $spec[0].Length -gt 1048576){throw 'Dependency license metadata was missing or oversized.'}
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create($spec[0].FullName,$settings)
    try {$xml=[Xml.XmlDocument]::new();$xml.XmlResolver=$null;$xml.Load($reader)}finally{$reader.Dispose()}
    $metadata=$xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $license=$metadata.SelectSingleNode('*[local-name()="license"]')
    $copyright=$metadata.SelectSingleNode('*[local-name()="copyright"]')
    $repository=$metadata.SelectSingleNode('*[local-name()="repository"]')
    if(!$license){throw ('License metadata is missing for '+$entry.Name)}
    $text=$license.InnerText
    if($license.type -eq 'file') {
        if($text -ne 'LICENSE.txt' -or ($entry.Name -notlike 'SQLite/*' -and $entry.Name -ne 'Microsoft.Web.WebView2/1.0.3800.47')){throw 'An unreviewed file-based dependency license requires review.'}
        if($entry.Name -eq 'Microsoft.Web.WebView2/1.0.3800.47') {
            if((Get-FileHash -LiteralPath (Join-Path $packageRoot 'LICENSE.txt') -Algorithm SHA256).Hash -ne '0AF8F1B807512AAE39C2AC1AA4D0CAE65CABECB6FD554B8439A5162A0D6ECA55' -or (Get-FileHash -LiteralPath (Join-Path $packageRoot 'NOTICE.txt') -Algorithm SHA256).Hash -ne '106423785C5B7EBA0A8E61D1837F2132E9C828E20AD530F565D981C1DF60DD90') { throw 'The reviewed WebView2 SDK terms or third-party notice changed.' }
            $lines.Add((Get-Content -LiteralPath (Join-Path $packageRoot 'NOTICE.txt') -Raw))
        }
        $text=Get-Content -LiteralPath (Join-Path $packageRoot 'LICENSE.txt') -Raw
    } elseif($text -notin @('MIT','Apache-2.0')){throw ('An unreviewed dependency license requires review: '+$entry.Name)}
    $item=[ordered]@{Package=$entry.Name;License=$license.InnerText;Copyright=if($copyright){$copyright.InnerText}else{''};Repository=if($repository){$repository.url}else{''}}
    $packages+=$item
    $lines.Add('');$lines.Add($entry.Name);$lines.Add('License: '+$text);if($copyright){$lines.Add($copyright.InnerText)}
}
# Framework downloadDependencies are not represented in assets.libraries. Audit the two shipped
# WinRT projection files explicitly instead of silently labelling this SDK pack as MIT.
$sdkPacks=@($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object { $_.name -eq 'Microsoft.Windows.SDK.NET.Ref' } | Select-Object -ExpandProperty version -Unique)
if($sdkPacks.Count -ne 1 -or $sdkPacks[0] -ne '[10.0.19041.57, 10.0.19041.57]'){throw 'The Windows SDK runtime pack changed and requires license review.'}
$sdkRoot=$null
foreach($folder in $assets.packageFolders.PSObject.Properties.Name) {
    $candidate=Join-Path $folder 'microsoft.windows.sdk.net.ref/10.0.19041.57'
    if(Test-Path -LiteralPath $candidate -PathType Container){$sdkRoot=$candidate;break}
}
if(!$sdkRoot){throw 'The reviewed Windows SDK runtime pack is missing.'}
$sdkFiles=@()
foreach($name in @('Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll')) {
    $original=Join-Path $sdkRoot ('lib/net8.0/'+$name)
    $shipped=Join-Path $destinationPath $name
    if(!(Test-Path -LiteralPath $original -PathType Leaf) -or !(Test-Path -LiteralPath $shipped -PathType Leaf)){throw 'A required Windows SDK runtime file is missing.'}
    $hash=(Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
    if((Get-FileHash -LiteralPath $shipped -Algorithm SHA256).Hash -ne $hash){throw 'A Windows SDK runtime file was modified or came from another pack.'}
    $sdkFiles+=[ordered]@{Name=$name;Sha256=$hash}
}
$sdkLicense=Join-Path $root 'docs/licensing/Microsoft-Windows-SDK.rtf'
if((Get-FileHash -LiteralPath $sdkLicense -Algorithm SHA256).Hash -ne 'DD07EB178E00C6BBA4148457FC00FF77CD4887EB521D504186FE59C9EC8BBE62'){throw 'The reviewed Windows SDK license text changed.'}
Copy-Item -LiteralPath $sdkLicense -Destination (Join-Path $destinationPath 'MICROSOFT-WINDOWS-SDK-LICENSE.rtf')
$packages+=[ordered]@{Package='Microsoft.Windows.SDK.NET.Ref/10.0.19041.57';Origin='Windows SDK framework download';License='Microsoft Windows SDK terms';Copyright='Copyright Microsoft Corporation. All rights reserved.';LicenseUrl='https://aka.ms/WinSDKLicenseURL';RedistributableList='https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref';LicenseFile='MICROSOFT-WINDOWS-SDK-LICENSE.rtf';LicenseSha256='DD07EB178E00C6BBA4148457FC00FF77CD4887EB521D504186FE59C9EC8BBE62';RuntimeFiles=$sdkFiles;ExternalAgreementReviewRequired=$true}
$lines.Add('');$lines.Add('Microsoft.Windows.SDK.NET.Ref 10.0.19041.57: Microsoft.Windows.SDK.NET.dll and WinRT.Runtime.dll, unmodified.')
$lines.Add('Copyright Microsoft Corporation. All rights reserved. Windows SDK terms apply; see MICROSOFT-WINDOWS-SDK-LICENSE.rtf.')
$lines.Add('Source terms: https://aka.ms/WinSDKLicenseURL; authorized runtime list: https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref')
$lines.Add('This is a local development candidate. External distribution requires satisfying the SDK downstream agreement terms; bundling notices alone does not establish that agreement.')
$lines.Add('');$lines.Add('The complete MIT and Apache-2.0 texts follow. Package-specific copyright notices above remain applicable.')
$lines.Add((Get-Content -LiteralPath (Join-Path $root 'docs\licensing\Microsoft-MIT.txt') -Raw))
$lines.Add((Get-Content -LiteralPath (Join-Path $root 'docs\licensing\Apache-2.0.txt') -Raw))
[IO.File]::WriteAllLines((Join-Path $destinationPath 'THIRD-PARTY-NOTICES.txt'),$lines,[Text.UTF8Encoding]::new($false))
[ordered]@{FormatVersion=2;GeneratedUtc=[DateTimeOffset]::UtcNow.ToString('O');Packages=$packages}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $destinationPath 'dependency-notices.json') -Encoding UTF8
