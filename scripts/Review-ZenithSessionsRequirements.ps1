param(
    [Parameter(Mandatory)][ValidateSet('Before','After')][string]$Phase,
    [Parameter(Mandatory)][string[]]$RequirementIds
)
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$ledgerPath=Join-Path $sourceRoot 'docs\zenith-sessions\REQUIREMENTS-LEDGER.md'
$ledger=Get-Content -LiteralPath $ledgerPath -Raw -Encoding UTF8
$promptRoot=Join-Path $env:USERPROFILE 'Downloads'
$names=@('Roblox_Launcher_Full_Prompt_With_Encrypted_Account_Vault.txt','Roblox_Saved_Accounts_Encrypted_Vault_Addendum.txt',"# MASTER PROMPT $([char]0x2014) COMPLETE PRODUCT.txt")
$sources=@(foreach($name in $names) {
    $path=Join-Path $promptRoot $name
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "A required prompt is unavailable: $name. Review cannot claim full source coverage." }
    $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    [ordered]@{ Name=$name; Sha256=$hash; MatchesReviewedLedger=($ledger.IndexOf($hash,[StringComparison]::OrdinalIgnoreCase) -ge 0) }
})
if(@($sources | Where-Object { -not $_.MatchesReviewedLedger }).Count) {
    throw 'A prompt changed since the ledger review. Read the changed source and reconcile its requirements before continuing the cycle.'
}
$selected=@(foreach($id in ($RequirementIds | Select-Object -Unique)) {
    if($id -notmatch '^(CAP-\d{2}-\d{2}|VA-\d{2}-\d{2}|PROD-[A-Z0-9-]+|CHAT-\d{2}|GAP-\d{2}|CYCLE-\d{2})$') { throw "Invalid requirement identifier: $id" }
    $line=($ledger -split '\r?\n' | Where-Object { $_ -match ('^[-] \*\*'+[regex]::Escape($id)+'(?:\s|\*)') })
    if(@($line).Count -ne 1) { throw "Requirement $id is absent or ambiguous in the ledger." }
    [ordered]@{ Id=$id; ReviewedClause=[string]$line }
})
if($selected.Count -eq 0) { throw 'Select concrete requirements before a review cycle.' }
$directory=Join-Path $sourceRoot 'artifacts\sessions-requirements-review'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$path=Join-Path $directory ($Phase.ToLowerInvariant()+'.json')
$temporary=$path+'.tmp'
$trx=Join-Path $sourceRoot 'artifacts\sessions-manager-tests\manager-core.trx'
$testTime=if(Test-Path -LiteralPath $trx) { (Get-Item -LiteralPath $trx).LastWriteTimeUtc.ToString('O') } else { $null }
$record=[ordered]@{
    FormatVersion=1; ReviewedUtc=[DateTimeOffset]::UtcNow.ToString('O'); Phase=$Phase
    Sources=$sources; LedgerSha256=(Get-FileHash -LiteralPath $ledgerPath -Algorithm SHA256).Hash
    Scope=$selected; LatestTestArtifactUtc=$testTime
    FullProductAcceptance=$false
    EvidenceRule='This records source/scope review only. It does not execute tests, validate a feature, prove native arrival or close a requirement.'
}
try { [IO.File]::WriteAllText($temporary,(($record | ConvertTo-Json -Depth 7)+[Environment]::NewLine),(New-Object Text.UTF8Encoding $false)); Move-Item -LiteralPath $temporary -Destination $path -Force }
finally { if(Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
Write-Output "$Phase review: all three prompt fingerprints match; $($selected.Count) scoped requirements recorded. Feature and release acceptance are unchanged."
