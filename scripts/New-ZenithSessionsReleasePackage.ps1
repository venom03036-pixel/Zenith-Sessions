[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][version]$Version,[Parameter(Mandatory)][string]$OutputDirectory,[string]$ReleaseToolPath,[string]$ReleaseApprovalFile)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ZenithSessionsDistribution.Common.ps1')
$sourceRoot=Assert-ZenithDistributionPath $Source
$output=[IO.Path]::GetFullPath($OutputDirectory)
if($output.StartsWith($sourceRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $output -eq $sourceRoot){throw 'Package output must be outside its source.'}
[void](Assert-ZenithDistributionPath $output)
if(Test-Path -LiteralPath $output){throw 'Choose a new output directory; existing release evidence is never overwritten.'}
$previewPath=Join-Path $sourceRoot 'preview-manifest.json'
$preview=Get-Content -LiteralPath $previewPath -Raw | ConvertFrom-Json
if($preview.FormatVersion -ne 1 -or @($preview.Files).Count -gt 2048){throw 'Invalid source preview inventory.'}
$sourceFiles=@(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force)
if($sourceFiles.Count -ne @($preview.Files).Count+1){throw 'Source contains files outside its preview inventory.'}
$sourceNames=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $preview.Files){
    $path=[IO.Path]::GetFullPath((Join-Path $sourceRoot $entry.Path))
    if(-not $sourceNames.Add($path) -or -not $path.StartsWith($sourceRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $entry.Length -or (Get-FileHash -LiteralPath $path).Hash -ne $entry.Sha256){throw 'Source differs from its verified preview inventory.'}
}
foreach($file in $sourceFiles){if($file.FullName -ne $previewPath -and -not $sourceNames.Contains($file.FullName)){throw 'Source contains an unlisted file.'}}
if($sourceFiles | Where-Object {$_.Extension -in @('.db','.dpapi','.zmacro','.zlicense','.pem')}){throw 'User data or key material must never enter a release package.'}
$ready=$false
if($ReleaseApprovalFile){
    $approval=Get-Content -LiteralPath $ReleaseApprovalFile -Raw | ConvertFrom-Json
    if($approval.FormatVersion -ne 1 -or $approval.ReleaseReady -ne $true -or $approval.Version -ne $Version.ToString() -or $approval.SourcePreviewManifestSha256 -ne (Get-FileHash -LiteralPath $previewPath).Hash){throw 'Release approval does not identify this exact preview/version.'}
    if(-not $ReleaseToolPath){throw 'An approved release requires the signed single-file release tool.'}
    Assert-ZenithPublisher (Join-Path $sourceRoot 'Zenith.Sessions.App.exe') $approval.PublisherCertificateSha256
    Assert-ZenithPublisher $ReleaseToolPath $approval.PublisherCertificateSha256
    $ready=$true
}
$content=Join-Path $output 'content';New-Item -ItemType Directory -Path $content -Force | Out-Null
foreach($file in $sourceFiles){
    if($file.FullName -eq $previewPath){continue}
    $destination=Join-Path $content $file.FullName.Substring($sourceRoot.TrimEnd('\').Length+1)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
foreach($entry in $preview.Files){if((Get-FileHash -LiteralPath (Join-Path $content $entry.Path)).Hash -ne $entry.Sha256){throw 'Source changed during release copying.'}}
foreach($name in @('Install-ZenithSessions.ps1','Uninstall-ZenithSessions.ps1','ZenithSessionsDistribution.Common.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $content $name)}
if($ReleaseToolPath){Copy-Item -LiteralPath $ReleaseToolPath -Destination (Join-Path $content 'Zenith.Sessions.ReleaseTool.exe')}
$files=@(Get-ChildItem -LiteralPath $content -File -Recurse | Sort-Object FullName | ForEach-Object {[pscustomobject][ordered]@{Path=$_.FullName.Substring($content.Length+1);Length=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$bytes=($files | Measure-Object -Property Length -Sum).Sum
if($files.Count -gt 2048 -or $bytes -gt 536870912 -or ($files | Where-Object {$_.Length -gt 268435456})){throw 'Release exceeds the update package budget.'}
$manifest=[ordered]@{FormatVersion=1;Product='Zenith.Sessions.Personal';Version=$Version.ToString();ReleaseReady=$ready;Files=$files}
[IO.File]::WriteAllText((Join-Path $content 'release-manifest.json'),($manifest | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath=Join-Path $output 'package.zip';$zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($file in Get-ChildItem -LiteralPath $content -File -Recurse | Sort-Object FullName){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$file.FullName.Substring($content.Length+1).Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal)}}finally{$zip.Dispose()}
if((Get-Item -LiteralPath $zipPath).Length -gt 536870912){throw 'Compressed release exceeds the update download budget.'}
[ordered]@{FormatVersion=1;ReleaseReady=$ready;Version=$Version.ToString();PackageBytes=(Get-Item -LiteralPath $zipPath).Length;PackageSha256=(Get-FileHash -LiteralPath $zipPath).Hash;SourcePreviewManifestSha256=(Get-FileHash -LiteralPath $previewPath).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'package-evidence.json') -Encoding UTF8
Write-Output ('Prepared '+$zipPath+'; ReleaseReady='+$ready+'. No upload or installation occurred.')
