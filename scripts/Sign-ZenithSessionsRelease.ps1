[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseTool,[Parameter(Mandatory)][string]$PrivateKeyPath,[Parameter(Mandatory)][string]$ManifestPath,[Parameter(Mandatory)][string]$KeyId,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
& $ReleaseTool sign $PrivateKeyPath $ManifestPath $KeyId $OutputPath
if($LASTEXITCODE -ne 0){throw 'Release signing failed.'}
