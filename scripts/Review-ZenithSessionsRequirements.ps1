param(
    [Parameter(Mandatory)][ValidateSet('Before','After')][string]$Phase,
    [Parameter(Mandatory)][string[]]$RequirementIds,
    # Cycle label such as 0.57, 0.45-initial or 0.57-game-links. Each phase and label gets its own record, and an existing record is never replaced: rerun under a new label such as 0.57-rerun2.
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+(-[a-z0-9]+)*$')][string]$Checkpoint,
    # Test result files (for example .trx) this cycle wrote, relative to the source root. Required for After.
    [string[]]$TestArtifact=@()
)
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$rootPrefix=$sourceRoot.TrimEnd('\')+'\'
$requestedArtifacts=@($TestArtifact | Where-Object { $_ } | Select-Object -Unique)
if($Phase -eq 'After' -and $requestedArtifacts.Count -eq 0) {
    throw 'An After checkpoint must name the test artifacts this cycle wrote. Pass -TestArtifact with each result file, for example artifacts\my-cycle-tests\results.trx.'
}
$testArtifacts=@(foreach($artifact in $requestedArtifacts) {
    $full=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($artifact)) { $artifact } else { Join-Path $sourceRoot $artifact }))
    if(-not $full.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Test artifact $artifact is outside the source tree. Pass a result file inside this repository." }
    $relative=$full.Substring($rootPrefix.Length).Replace('\','/')
    if(-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Test artifact $relative was not found. Pass a result file this cycle's tests wrote." }
    [ordered]@{ Path=$relative; LastWriteUtc=(Get-Item -LiteralPath $full).LastWriteTimeUtc.ToString('O'); Sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash }
})
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
$fileName=$Phase.ToLowerInvariant()+'-'+$Checkpoint+'.json'
$path=Join-Path $directory $fileName
if(Test-Path -LiteralPath $path) {
    throw "$Phase $Checkpoint checkpoint not written: artifacts/sessions-requirements-review/$fileName already exists and is kept unchanged. Pass a new -Checkpoint label, for example $($Checkpoint)-rerun, to record another review."
}
$temporary=$path+'.tmp'
$record=[ordered]@{
    FormatVersion=2; Checkpoint=$Checkpoint; ReviewedUtc=[DateTimeOffset]::UtcNow.ToString('O'); Phase=$Phase
    Sources=$sources; LedgerSha256=(Get-FileHash -LiteralPath $ledgerPath -Algorithm SHA256).Hash
    Scope=$selected; TestArtifacts=$testArtifacts
    FullProductAcceptance=$false
    EvidenceRule='This records source/scope review only. It does not execute tests, validate a feature, prove native arrival or close a requirement. TestArtifacts names the result files this cycle cites, with their write time and hash; it does not read their outcome.'
}
try { [IO.File]::WriteAllText($temporary,(($record | ConvertTo-Json -Depth 7)+[Environment]::NewLine),(New-Object Text.UTF8Encoding $false)); Move-Item -LiteralPath $temporary -Destination $path }
finally { if(Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
Write-Output "$Phase $Checkpoint review: all three prompt fingerprints match; $($selected.Count) scoped requirements and $($testArtifacts.Count) test artifacts recorded in artifacts/sessions-requirements-review/$fileName. Feature and release acceptance are unchanged."
