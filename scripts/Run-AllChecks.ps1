[CmdletBinding()]
param([switch]$AllowRestoreDownloads,[switch]$AcquireLuau,[switch]$NoRestore,[switch]$PlanOnly,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root ('artifacts\all-checks\'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))}
$output=(New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$recordPath=Join-Path $output 'checks.json'
if(Test-Path -LiteralPath $recordPath){throw 'This result folder already contains checks.json. Choose a fresh output directory.'}
function Get-SourceState {
    $previousPreference=$ErrorActionPreference
    try {
    $ErrorActionPreference='Continue'
    $head=& git -C $root rev-parse HEAD 2>$null
    if($LASTEXITCODE -ne 0){return [ordered]@{Available=$false;Commit=$null;DirtyStatus=@();ContentSha256=$null;FileCount=0}}
    $ErrorActionPreference='Stop'
    $dirty=@(& git -C $root status --porcelain=v1 --untracked-files=all)
    if($LASTEXITCODE -ne 0){throw 'Could not read source status.'}
    $listing=& git -C $root -c core.quotepath=false ls-files -z --cached --others --exclude-standard
    if($LASTEXITCODE -ne 0){throw 'Could not inventory source files.'}
    $names=@(($listing -join "`n").Split([char]0) | Where-Object {$_} | Sort-Object -Unique)
    $lines=New-Object 'Collections.Generic.List[string]'
    foreach($name in $names){
        $file=Join-Path $root $name
        $hash=if(Test-Path -LiteralPath $file -PathType Leaf){(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash}else{'MISSING'}
        $lines.Add($name+':'+$hash)
    }
    $stream=[IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))
    try{$fingerprint=(Get-FileHash -InputStream $stream -Algorithm SHA256).Hash}finally{$stream.Dispose()}
    return [ordered]@{Available=$true;Commit=([string]$head).Trim();DirtyStatus=$dirty;ContentSha256=$fingerprint;FileCount=$names.Count}
    } catch { return [ordered]@{Available=$false;Commit=$null;DirtyStatus=@();ContentSha256=$null;FileCount=0;Failure=$_.Exception.GetType().Name} }
    finally { $ErrorActionPreference=$previousPreference }
}
$sourceStart=Get-SourceState
$paid='MacroSuite.Infrastructure.Tests.AI.AIVisionLiveActionTests.AIVision_CloudProvider_ClicksGreenTargetButton_WhenKeyConfigured'
$filter='FullyQualifiedName!='+$paid
$projects=@('MacroSuite.Core.Tests','MacroSuite.Infrastructure.Tests','MacroSuite.SDK.Tests','MacroSuite.LicenseIssuer.Tests','Zenith.Sessions.CapabilityHarness.Tests','Zenith.Sessions.Tests')
$actual=@(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.csproj' -Recurse -File | Where-Object {$_.Name -like '*Tests.csproj'} | ForEach-Object {$_.BaseName} | Sort-Object)
if(($actual -join '|') -ne (($projects | Sort-Object) -join '|')){throw 'The test project inventory changed. Update the gate so every suite is covered.'}
$steps=New-Object 'Collections.Generic.List[object]'
function Add-Step([string]$Name,[string]$Kind,[string[]]$Arguments){$steps.Add([ordered]@{Name=$Name;Kind=$Kind;Arguments=$Arguments;Status='Pending';ExitCode=$null;Log=$null})}
Add-Step 'Pinned Luau tools' 'Tools' @()
foreach($name in $projects){
    $project=Join-Path $root ('tests\'+$name+'\'+$name+'.csproj')
    if(-not $NoRestore){
        $arguments=@('restore',$project,'--disable-parallel','-m:2','-nr:false','-p:NuGetAudit=true','-p:NuGetAuditMode=all')
        if(-not $AllowRestoreDownloads){$arguments+=@('--source',(Join-Path $env:USERPROFILE '.nuget\packages'))}
        Add-Step ($name+' restore') 'Dotnet' $arguments
    }
    Add-Step ($name+' Release build') 'Dotnet' @('build',$project,'-c','Release','--no-restore','-m:2','-nr:false','-warnaserror')
    Add-Step ($name+' tests') 'Dotnet' @('test',$project,'-c','Release','--no-build','--no-restore','-m:2','-nr:false','--filter',$filter,'--logger',('trx;LogFileName='+$name+'.trx'),'--results-directory',$output)
}
Add-Step 'Owned experience Luau verifier' 'Luau' @()
Add-Step 'Packaged manager 43-stage self-check' 'Package' @('-SkipTests','-StageOnly','-StageFolder','sessions-manager-review')
$record=[ordered]@{FormatVersion=1;StartedUtc=[DateTimeOffset]::UtcNow.ToString('O');CompletedUtc=$null;Status='Planned';ReleaseReady=$false;Configuration='Release';ExcludedTest=$paid;RestoreDownloads=[bool]$AllowRestoreDownloads;ToolDownloads=[bool]$AcquireLuau;NoRestore=[bool]$NoRestore;SourceStart=$sourceStart;SourceEnd=$null;SourceChanged=$false;Steps=$steps}
function Save-Record{[IO.File]::WriteAllText($recordPath,($record|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))}
Save-Record
if($PlanOnly){Write-Output $recordPath;return}
if([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT){throw 'All checks require Windows with .NET 8 and 10 SDKs and desktop runtimes.'}
$record.Status='Running';Save-Record
$stepIndex=0;$failed=$false;$luau=$null;$built=@{}
foreach($step in $steps){
    $stepIndex++;$log=Join-Path $output ($stepIndex.ToString('00')+'-'+($step.Name -replace '[^a-zA-Z0-9.-]','-')+'.log');$step.Log=$log
    Write-Output ('Checking '+$step.Name)
    try{
        switch($step.Kind){
            'Tools' {$luau=& (Join-Path $PSScriptRoot 'Get-ZenithCheckTools.ps1') -AllowDownload:$AcquireLuau;[IO.File]::WriteAllText($log,[string]$luau);$code=0}
            'Dotnet' {
                $suite=$step.Name -replace ' (restore|Release build|tests)$',''
                if($step.Name.EndsWith(' tests') -and $built[$suite] -ne $true){throw 'This suite was not built successfully; stale binaries will not run.'}
                # Native stderr is evidence, not a PowerShell terminating error; the exit code owns the result.
                $ErrorActionPreference='Continue';$PSNativeCommandUseErrorActionPreference=$false
                $arguments=$step.Arguments
                & dotnet @arguments *> $log
                $code=$LASTEXITCODE;$ErrorActionPreference='Stop'
                if($step.Name.EndsWith(' Release build')){$built[$suite]=($code -eq 0)}
                if($code -eq 0 -and $step.Name.EndsWith(' tests')){
                    $trx=Join-Path $output (($step.Name -replace ' tests$','')+'.trx')
                    if(-not (Test-Path -LiteralPath $trx -PathType Leaf)){throw 'The test runner did not produce its expected TRX.'}
                    [xml]$result=Get-Content -LiteralPath $trx -Raw
                    $counter=$result.TestRun.ResultSummary.Counters
                    if([int]$counter.executed -le 0 -or [int]$counter.failed -ne 0){throw 'The TRX reported no executed tests or a failure.'}
                    $step['Passed']=[int]$counter.passed;$step['Failed']=[int]$counter.failed;$step['NotExecuted']=[int]$counter.notExecuted
                    if(@($result.TestRun.Results.UnitTestResult | Where-Object {$_.testName -like '*AIVision_CloudProvider_ClicksGreenTargetButton_WhenKeyConfigured*'}).Count -ne 0){throw 'The excluded paid test appeared in the results.'}
                }
            }
            'Luau' {
                if($null -eq $luau){throw 'Pinned Luau tools were not verified; the verifier is not skipped.'}
                & (Join-Path $PSScriptRoot 'Test-ZenithOwnedExperienceBridge.ps1') -LuauDirectory $luau -OutputDirectory (Join-Path $output 'luau') *> $log
                $code=0
            }
            'Package' {
                if($failed){throw 'Earlier checks failed; packaged release validation cannot be accepted.'}
                & (Join-Path $PSScriptRoot 'Check-ZenithSessionsManager.ps1') -SkipTests -StageOnly -StageFolder sessions-manager-review -AllowRestoreDownloads:$AllowRestoreDownloads *> $log
                if(Select-String -LiteralPath $log -Pattern '\bwarning\s+[A-Z]+\d+\s*:' -Quiet){throw 'The packaged publish emitted a warning; zero-warning release evidence is required.'}
                $report=Join-Path $root 'artifacts\sessions-distribution-check\distribution-check.json'
                $native=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
                if(@($native.Cases).Count -ne 43 -or @($native.Cases | Where-Object {-not $_.Passed}).Count -ne 0){throw 'Expected all 43 packaged stages to pass.'}
                Copy-Item -LiteralPath $report -Destination (Join-Path $output 'distribution-check.json')
                $code=0
            }
        }
        $step.ExitCode=$code
        if($code -ne 0){throw ('Exit code '+$code)}
        $step.Status='Passed'
    }catch{
        $ErrorActionPreference='Stop';$step.Status='Failed';$step['Failure']=$_.Exception.Message;$failed=$true
        if($null -eq $step.ExitCode){$step.ExitCode=1}
        Write-Output ($step.Name+' failed: '+$_.Exception.Message)
    }finally{
        $sourceNow=Get-SourceState
        if(-not $sourceNow.Available -or -not $sourceStart.Available -or $sourceNow.Commit -ne $sourceStart.Commit -or $sourceNow.ContentSha256 -ne $sourceStart.ContentSha256 -or ($sourceNow.DirtyStatus -join "`n") -ne ($sourceStart.DirtyStatus -join "`n")){$record.SourceChanged=$true;$failed=$true}
        $record.SourceEnd=$sourceNow;Save-Record
    }
}
$record.CompletedUtc=[DateTimeOffset]::UtcNow.ToString('O');$record.Status=if($failed){'Failed'}else{'Passed'};Save-Record
Write-Output ('Recorded all checks: '+$recordPath)
if($failed){throw 'One or more checks failed. Read checks.json and its step logs.'}
