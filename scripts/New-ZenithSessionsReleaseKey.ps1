[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseTool,[Parameter(Mandatory)][string]$PrivateKeyPath,[Parameter(Mandatory)][string]$PublicKeyPath)
$ErrorActionPreference='Stop'
& $ReleaseTool keygen $PrivateKeyPath $PublicKeyPath
if($LASTEXITCODE -ne 0){throw 'Key generation failed. Private keys must stay outside the repository.'}
