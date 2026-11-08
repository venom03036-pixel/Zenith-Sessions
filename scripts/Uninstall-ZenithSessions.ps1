[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ZenithSessionsDistribution.Common.ps1')
$paths=Get-ZenithDistributionPaths
if([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ne [IO.Path]::GetFullPath($paths.Install).TrimEnd('\')){throw 'Run the uninstaller from the fixed per-user Zenith Sessions installation.'}
Assert-ZenithClosed
Assert-ZenithOwnedInstall $paths.Install
if((Read-Host 'Uninstall Zenith Sessions? Account data will remain unless separately confirmed. Type UNINSTALL') -cne 'UNINSTALL'){return}
$deleteData=(Read-Host ('Delete saved accounts and all Personal data at '+$paths.Data+'? Type DELETE PERSONAL DATA; otherwise press Enter to keep it')) -ceq 'DELETE PERSONAL DATA'
[void](Assert-ZenithDistributionPath $paths.Install)
if($deleteData){[void](Assert-ZenithDistributionPath $paths.Data)}
# Paths are fixed above and rechecked before recursive removal. Other Zenith editions are never selected.
Remove-Item -LiteralPath $paths.Install -Recurse -Force
if(Test-Path -LiteralPath $paths.Shortcut){Remove-Item -LiteralPath $paths.Shortcut -Force}
if(Test-Path -LiteralPath $paths.Registry){Remove-Item -LiteralPath $paths.Registry -Recurse -Force}
if($deleteData -and (Test-Path -LiteralPath $paths.Data)){Remove-Item -LiteralPath $paths.Data -Recurse -Force}
Write-Output 'Zenith Sessions was uninstalled. Retained previous/pending update folders are available for manual recovery.'
