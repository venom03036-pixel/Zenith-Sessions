param([switch]$SkipTests,[switch]$AllowRestoreDownloads,[switch]$StageOnly,[switch]$ForceRebuild,
    [ValidateSet('sessions-manager-stage','sessions-manager-candidate','sessions-manager-review')][string]$StageFolder='sessions-manager-stage')
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts=Join-Path $sourceRoot 'artifacts'
$project=Join-Path $sourceRoot 'src\Zenith.Sessions.App\Zenith.Sessions.App.csproj'
$testProject=Join-Path $sourceRoot 'tests\Zenith.Sessions.Tests\Zenith.Sessions.Tests.csproj'
$testOutput=Join-Path $artifacts 'sessions-tests-bin\'
$buildFolder=if($StageFolder -eq 'sessions-manager-candidate'){'sessions-manager-candidate-build'}else{'sessions-manager-build'}
$buildOutput=Join-Path $artifacts ($buildFolder+'\')
$stage=Join-Path $artifacts $StageFolder
$current=Join-Path $artifacts 'sessions-manager-current'
$previous=Join-Path $artifacts 'sessions-manager-previous'
$results=Join-Path $artifacts 'sessions-manager-tests'
$env:DOTNET_CLI_HOME=Join-Path $artifacts '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$restore=@('--ignore-failed-sources','--disable-parallel','--disable-build-servers','-m:1','-nr:false','-p:NuGetAudit=true','-p:NuGetAuditMode=all')
if(-not $AllowRestoreDownloads) { $restore+=@('--source',(Join-Path $env:USERPROFILE '.nuget\packages')) }
function Assert-SafeOutput([string]$Path) {
    $resolved=[IO.Path]::GetFullPath($Path)
    if([IO.Path]::GetDirectoryName($resolved) -ne $artifacts -or [IO.Path]::GetFileName($resolved) -notin @('sessions-manager-stage','sessions-manager-candidate','sessions-manager-review','sessions-manager-current','sessions-manager-previous','sessions-manager-build','sessions-manager-candidate-build')) {
        throw 'Output path is outside the fixed manager build folders.'
    }
    if(Test-Path -LiteralPath $resolved) {
        if((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A build output cannot be a redirected folder.' }
        if(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'A build output contains a redirected child path. Preserve it for manual review.' }
        $personalFiles=Get-ChildItem -LiteralPath $resolved -Recurse -Force -File | Where-Object { $_.Name -in @('accounts.db','client-history.db','macro-assignments.db','macro-assignments.db-journal','macro-assignments.db-wal','macro-assignments.db-shm') -or $_.Extension -in @('.dpapi','.zmacro','.zlicense') }
        if($personalFiles) { throw 'User data was found inside a build output. Preserve it and move it outside that output before rebuilding.' }
    }
}
function Assert-WritableOutput([string]$Path) {
    # Detect in-use or owner-protected binaries before deleting any part of an old preview.
    if(-not (Test-Path -LiteralPath $Path)) { return }
    foreach($file in Get-ChildItem -LiteralPath $Path -Recurse -Force -File) {
        $handle=$null
        try { $handle=[IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
        catch { throw 'The selected build folder is in use or not writable. Its contents were preserved. Close that development preview, or use the separate fixed candidate stage.' }
        finally { if($null -ne $handle) { $handle.Dispose() } }
    }
}
if(-not $SkipTests) {
    & dotnet restore $testProject @restore
    if($LASTEXITCODE -ne 0) { throw 'Manager test restore failed. No output was installed.' }
    if($ForceRebuild) {
        & dotnet build $testProject -c Release --no-restore --no-incremental --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false "-p:OutDir=$testOutput"
        if($LASTEXITCODE -ne 0) { throw 'The clean regression rebuild failed. No preview was promoted.' }
    }
    & dotnet test $testProject -c Release --no-restore --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false "-p:OutDir=$testOutput" --logger 'trx;LogFileName=manager-core.trx' --results-directory $results
    if($LASTEXITCODE -ne 0) { throw 'Manager checks failed. The previous preview was preserved.' }
}
Assert-SafeOutput $stage
Assert-WritableOutput $stage
Assert-SafeOutput (Join-Path $artifacts $buildFolder)
Assert-WritableOutput (Join-Path $artifacts $buildFolder)
if(Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
& dotnet restore $project -r win-x64 -p:SelfContained=false -p:EnableRuntimePackDownload=false @restore
if($LASTEXITCODE -ne 0) { throw 'Manager restore failed. The previous preview was preserved.' }
if($ForceRebuild) {
    & dotnet build $project -c Release -r win-x64 --no-restore --no-incremental --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false -p:SelfContained=false -p:EnableRuntimePackDownload=false -p:DebugType=None -p:DebugSymbols=false "-p:OutDir=$buildOutput"
    if($LASTEXITCODE -ne 0) { throw 'The clean candidate rebuild failed. No preview was promoted.' }
}
& dotnet publish $project -c Release -r win-x64 --no-restore --self-contained false --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false -p:EnableRuntimePackDownload=false -p:DebugType=None -p:DebugSymbols=false "-p:OutDir=$buildOutput" -o $stage
if($LASTEXITCODE -ne 0) { throw 'Manager publish failed. The previous preview was preserved.' }
$required=@('Zenith.Sessions.App.exe','Zenith.Sessions.App.dll','Zenith.Sessions.App.runtimeconfig.json','Zenith.Sessions.App.deps.json','Zenith.Sessions.Windows.dll','Zenith.Sessions.Core.dll','Zenith.Automation.Input.dll','Zenith.MacroHost.Contracts.dll','Zenith.MacroHost.Windows.dll','Microsoft.Data.Sqlite.dll','Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll')
foreach($file in $required) { if(-not (Test-Path -LiteralPath (Join-Path $stage $file) -PathType Leaf)) { throw "The preview is incomplete: missing $file" } }
if(-not ((Test-Path -LiteralPath (Join-Path $stage 'e_sqlite3.dll')) -or (Test-Path -LiteralPath (Join-Path $stage 'runtimes\win-x64\native\e_sqlite3.dll')))) { throw 'The Windows x64 database runtime is missing.' }
$fixture=Join-Path $artifacts 'sessions-distribution-check'
$nativeReport=Join-Path $fixture 'distribution-check.json'
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $stage 'Zenith.Sessions.App.exe'))
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
# Under Windows PowerShell 5.1 (.NET Framework) ProcessStartInfo has no ArgumentList. A Windows path cannot contain a quote,
# and this fixed folder ends in its name rather than a backslash, so plain quoting reaches the app unchanged.
$start.Arguments='--self-check --data-root "'+$fixture+'"'
$launchedUtc=[DateTime]::UtcNow
$check=[Diagnostics.Process]::Start($start)
try {
    if(-not $check.WaitForExit(20000)) { $check.Kill(); $null=$check.WaitForExit(5000); throw 'The published-file check exceeded its deadline. No preview was promoted.' }
    Write-Output $check.StandardOutput.ReadToEnd()
    Write-Output $check.StandardError.ReadToEnd()
    if($check.ExitCode -ne 0) { throw 'The published files failed their non-interactive check. The previous preview was preserved.' }
    if(-not (Test-Path -LiteralPath $nativeReport) -or (Get-Item -LiteralPath $nativeReport).LastWriteTimeUtc -lt $launchedUtc) { throw 'A fresh published-file result was not produced.' }
    $native=Get-Content -LiteralPath $nativeReport -Raw -Encoding UTF8 | ConvertFrom-Json
    $expectedCases=@('Isolated storage ownership','Packaged brand image','Packaged production window composition','Packaged native SQLite and profile round trip','Library-wide group review and commit','Independent saved group catalog','Atomic settings round trip','Synthetic final handoff and current-grant boundary','Windows protected authorization round trip','Protected private-server store round trip','Saved session and browser runtime boundaries','Conditional protected-session ownership','Snapshot sign-in create and disconnect','Direct verified account creation','Generation-owned identity recovery cleanup','Capture-only handoff policy boundaries','Native Windows resource sampling','Durable guarded launch queue','Durable unverified process receipt','Synthetic preparation queue and recovery','Synthetic trial consent and reservations','Pinned resource forecasts and candidate admission','Recovered queue generation and no replay','Bounded typed diagnostics journal','Bounded diagnostic session projection','Recent client history metadata round trip','Public game metadata and capacity boundaries','Public game search and destination isolation','Atomic destination favorites round trip','Recipient bundle metadata round trip','Physical client layout planner','Physical content-size planner and invalid-client guard','Portable manager performance round trip','Read-only installation inventory','Selected-file publisher boundary','Process I/O counters and telemetry boundaries','Preview runtime and invalid target boundaries','Controlled targeted input ownership','Read-only Personal macro catalog pipe','Durable shared macro assignments','Public headshot and ordered gallery boundaries','All-observed tiled Windows capture scheduling','Durable credential cleanup')
    if($native.FormatVersion -ne 1 -or $native.DevelopmentSelfCheck -ne $true -or $native.ReleaseReady -ne $false -or $native.Cases.Count -ne $expectedCases.Count -or @($native.Cases.Name | Select-Object -Unique).Count -ne $expectedCases.Count -or @($native.Cases | Where-Object { $_.Passed -ne $true -or $_.Name -notin $expectedCases }).Count -gt 0) { throw 'The published-file evidence is incomplete.' }
    if($native.AssemblySha256 -ne (Get-FileHash -LiteralPath (Join-Path $stage 'Zenith.Sessions.App.dll') -Algorithm SHA256).Hash) { throw 'The published-file result belongs to a different build.' }
    # The App hash alone cannot identify the build: a Core- or Windows-only change leaves Zenith.Sessions.App.dll byte-identical.
    # Keep this list in step with DistributionSelfCheck.FirstPartyAssemblies; the identity is SHA-256 of "name:hash`n" lines in this order.
    $firstParty=@('Zenith.Sessions.App.dll','Zenith.Sessions.Core.dll','Zenith.Sessions.Windows.dll','Zenith.Automation.Input.dll','Zenith.MacroHost.Contracts.dll','Zenith.MacroHost.Windows.dll')
    if($null -eq $native.FirstPartySha256 -or (@($native.FirstPartySha256.PSObject.Properties.Name | Sort-Object) -join '|') -ne (@($firstParty | Sort-Object) -join '|')) { throw 'The published-file result does not identify every first-party assembly.' }
    $identityLines=''
    foreach($name in $firstParty) {
        $staged=(Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash
        if($native.FirstPartySha256.$name -ne $staged) { throw 'The published-file result belongs to a different build.' }
        $identityLines+=('{0}:{1}' -f $name,$staged)+"`n"
    }
    $identity=(Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($identityLines))) -Algorithm SHA256).Hash
    if($native.FirstPartyIdentitySha256 -ne $identity) { throw 'The published-file result belongs to a different build.' }
} finally { $check.Dispose() }
$readMe=@'
ZENITH SESSIONS — PERSONAL DEVELOPMENT PREVIEW

Run Zenith.Sessions.App.exe on Windows 10 (2004 or later), x64.
The Microsoft .NET 10 Windows Desktop Runtime must already be installed.
This preview does not install or unpack a runtime when you open it.

Sessions > Join a game includes an explicit native launch preparation inspection. Select a
saved account and game-only destination, then choose Inspect and prevent launch.
The official page opens privately; choosing Play captures only bounded structural
facts and cancels external dispatch. It starts no client. Successful cleanup and
same-player sign-in verification are required before saving the current session.
An incomplete or cancelled check may require signing in again. This diagnostic
does not establish native account selection, arrival or multi-client compatibility.

Sessions > Join a game also contains a separately confirmed native account TRIAL. Select a
native Roblox player file in Settings, a saved account and an explicit game/server.
This test can start a real native client after publisher, sign-in, capacity and
stop checks. It is unverified, never automatic, and does not enable normal/group
launching. Pause/Panic/Lock/restart revoke permission for unstarted trials.
Requested destination, process receipt, account identity and arrival stay separate.
Launch evidence shows exact process presence alongside separately timestamped,
unverified reported player/place/server values and last-known history. Copy/Save
exports contain those IDs and timing, never credentials, names, paths or raw logs.
Each request keeps its recorded physical/commit estimate when defaults change.
Recovered possible clients without a recorded estimate require closure review.
Startup spacing/stabilization uses a monotonic clock; UTC adjustments cannot skip
the hold. Completion of the countdown alone never grants launch permission.

Data is created automatically under %LOCALAPPDATA%\ZenithSessions\Personal.
Your existing Zenith Macros installation is not replaced.
Close hides the manager to the tray. Use … → Exit manager for a full exit.
Use the in-app Guide for profiles, saved Roblox sign-ins, settings and monitoring.
Private sign-in requires Microsoft's installed Evergreen WebView2 Runtime.
The SDK assemblies/loader are included; a browser Runtime is not bundled or
installed automatically. Missing Runtime is reported without changing accounts.

Implemented: account metadata library, search/groups/dense rows, metadata
import/export, process/resource observations, theme/settings persistence,
independent empty groups with colors, ordering and reviewed rename/removal;
profile metadata exports include memberships but exclude empty definitions/colors/order;
account schema 5 migration keeps a verified schema 4 backup; close older previews first;
per-account Windows-protected Roblox sessions, explicit user-ID validation,
durable credential generations and credential cleanup;
conditional sign-in/check/inspection updates and disconnect with cross-process file
ownership, exact snapshots, generation-owned identity-journal cleanup and owner-only
cleanup; close older previews before first use of version 2 generation metadata;
local Settings search moves to existing controls without changing draft values;
explicit physical content resizing and a fields-only tiny preset use the existing
window-operation owner without focusing clients; actual placement remains unverified;
named account bundles with metadata-only references, cross-page additions,
explicit missing-profile review and frozen destination requests;
queue format 7 process-start receipts, pinned resource forecasts and explicit manual closure acknowledgment,
with reservation-free preparation and no automatic replay on restart;
client-start evidence never counts as verified account identity or arrival;
fresh exact process measurements credit only private consumption already counted
in system memory, retaining forecast growth and unknown/stale reservations;
explicit public avatars and server-link/public-server browsing;
explicit experience names, creator, IDs and bounded icons;
fresh requested-server capacity advice for the selected recipients;
saved manager performance profiles, process cadence and launch-order policies;
optional adapter-wide GPU engine/dedicated/shared-memory counters, with unknown
and stale evidence kept separate; a sustained reliable 85% engine load holds
new admissions, while recovery requires thirty healthy seconds;
explicit read-only Roblox publisher verification using cached Windows trust
evidence; no executable is opened or given an account handoff by this check;
read-only saved Personal macro catalog with explicit fresh page review;
durable individual/group/bundle macro-ID assignments with transactional conflict checks;
assignments contain no active runs and never resume automation;
a shared visible-page headshot loader keyed by verified player IDs, with sample-mode networking disabled;
ordered public experience picture galleries with manual previous/next and explicit unavailable/video states;
eight-section sidebar with an all-observed tiled Windows view, preserved local Ctrl+1-7 navigation, bounded diagnostic view,
severity/text filters, pause/clear view and redacted copy/JSON export.
Read-only bounded log correlation displays unverified reported player/place/server
IDs, with separate timestamped last-known values. File-use evidence is not
writer proof or verified account identity. No account actions use these fields.
Recent-client presence history retains up to 100 ended observations in its own
bounded local database. Only fresh complete scans can mark a client no longer
observed. A normal close request is recorded separately; cause remains unknown.
Partial, stale or failed scans do not establish exit or crash. History restores
no running state or automatic launch. Storage failures remain visible for retry.
Overview adds bounded CPU/RAM/commit history; Sessions has Table, Previews and
History modes. Client charts retain up to 120 actual samples over two minutes,
with real timestamps and unknown gaps. Six process I/O byte/operation rates
include file, network and device activity; they do not isolate disk, network,
paging or hard faults. Read-only counters never verify an account or arrival.
No history is persisted and no per-account polling loops/timers are created.
Optional client previews are off by default. Select up to four observed windows
in Sessions, choose Previews, then start. Outputs fit 320x180 without upscaling
and update at most 1 Hz; Windows internal capture can run faster and original-size
readback remains. Hidden/minimized/locked/pressured contexts stop and clear them.
Images expire after 3 seconds and are never persisted or used to verify an account.
Real-client capture availability and CPU/GPU overhead remain unverified.
The two unmodified Windows SDK projection DLLs have explicit provenance and
the complete Microsoft SDK license. External release still requires satisfying
the downstream agreement terms; this local candidate is not a release license.
Public server queries include occupancy direction and exclude-full options;
reported-ping ranking compares the loaded page only.
Named destination favorites support save/use/rename/confirmed removal and
bounded atomic recovery. Requested and currently reported server links can be
copied explicitly; reported links are rechecked for newer invalidating evidence.
Checked client windows can be tiled on a chosen monitor without requesting
focus. Layout preferences save with all settings. Mixed-DPI live placement,
actual clipboard interaction, focus and final layout acceptance remain unverified.

NOT A FINISHED AUTOMATION RELEASE:
Native account-selecting launch/reconnect, verified Roblox background macros,
live broadcasting, owned-game bridge, host execution integration, sixteen-client tests,
native client FPS/graphics policies, custom appearances, mini mode and final
UI/performance acceptance are pending. Manager profiles do not prove game
performance or a higher sustainable client count.
Saved Roblox sign-ins do not by themselves prove native client launch/arrival.
No private owner data or authorization is included.
'@
# Set-Content -Encoding utf8 adds a byte-order mark under Windows PowerShell 5.1 only; write BOM-free UTF-8 so READ-ME.txt hashes the same on both.
$utf8=[Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $stage 'READ-ME.txt'),$readMe+[Environment]::NewLine,$utf8)
& (Join-Path $PSScriptRoot 'Write-ZenithSessionsNotices.ps1') -Destination $stage
# Path.GetRelativePath is missing from .NET Framework; every file listed here sits under the stage folder.
$stagePrefix=[IO.Path]::GetFullPath($stage).TrimEnd('\')+'\'
$manifest=@(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    if(-not $_.FullName.StartsWith($stagePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'A staged file is outside the preview folder. No preview was promoted.' }
    [ordered]@{ Path=$_.FullName.Substring($stagePrefix.Length); Length=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifestJson=[ordered]@{ FormatVersion=1; DevelopmentPreview=$true; ReleaseReady=$false; BuiltUtc=[DateTimeOffset]::UtcNow.ToString('O'); Files=$manifest } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $stage 'preview-manifest.json'),$manifestJson+[Environment]::NewLine,$utf8)
if($StageOnly) {Write-Output "Verified staged development preview: $stage. Running previews and installed applications were left unchanged.";return}
Assert-SafeOutput $current
Assert-SafeOutput $previous
Assert-WritableOutput $current
Assert-WritableOutput $previous
if(Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
if(Test-Path -LiteralPath $current) { Move-Item -LiteralPath $current -Destination $previous }
try { Move-Item -LiteralPath $stage -Destination $current }
catch {
    if((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $current)) { Move-Item -LiteralPath $previous -Destination $current }
    throw
}
$installedManifest=Get-Content -LiteralPath (Join-Path $current 'preview-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach($entry in $installedManifest.Files) {
    $file=Join-Path $current $entry.Path
    if(-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) {
        throw 'Preview verification failed. Keep the previous output for recovery.'
    }
}
Write-Output "Verified development preview: $current"
Write-Output 'No graphical window was opened and nothing was installed. The candidate ran only its non-interactive self-check; release readiness remains false.'



