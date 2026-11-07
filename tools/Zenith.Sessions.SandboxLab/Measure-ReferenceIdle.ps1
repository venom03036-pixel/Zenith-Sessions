# Guest-only, metadata-only baseline. No credentials, process memory contents,
# command lines, trimming, input, priority changes or protected-client changes.
$ErrorActionPreference='Stop'
if([Security.Principal.WindowsIdentity]::GetCurrent().Name -notmatch '\\WDAGUtilityAccount$'){throw 'Run this collector only inside Sandbox.'}
$output='C:\ZenithLab\Benchmarks'
[IO.Directory]::CreateDirectory($output)|Out-Null
$root=@(Get-Process -ErrorAction Stop|Where-Object{$_.ProcessName -ieq 'roalt'})
if($root.Count -ne 1){throw 'Exactly one running RoALT process is required.'}
$rootId=$root[0].Id;$rootStart=$root[0].StartTime.ToUniversalTime();$rootFile=$root[0].MainModule.FileName
$identity=[ordered]@{Name=$root[0].ProcessName;ProcessId=$rootId;StartedUtc=$rootStart.ToString('O');Version=[Diagnostics.FileVersionInfo]::GetVersionInfo($rootFile).FileVersion;Sha256=(Get-FileHash -LiteralPath $rootFile -Algorithm SHA256).Hash}
foreach($item in $root){$item.Dispose()}
$initialMetadata=Get-CimInstance Win32_Process -Filter ('ProcessId='+$rootId) -Property ProcessId,CreationDate
if(-not $initialMetadata -or [Math]::Abs(($initialMetadata.CreationDate.ToUniversalTime()-$rootStart).Ticks) -gt 100){throw 'The initial process observations did not agree.'}
# CIM timestamps have microsecond precision, while GetProcessTimes preserves
# 100 ns ticks. Compare later CIM observations with the same API's initial value.
$cimRootStart=$initialMetadata.CreationDate.ToUniversalTime()
$collector=[Diagnostics.Process]::GetCurrentProcess()
$previous=@{};$rows=[Collections.Generic.List[object]]::new();$failures=[Collections.Generic.List[string]]::new()
$started=[DateTimeOffset]::UtcNow;$stage='Warmup'
[ordered]@{Stage='Warming';ProcessId=$PID;StartedUtc=$started.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'reference-state.json') -Encoding utf8
Start-Sleep -Seconds 15
$clock=[Diagnostics.Stopwatch]::StartNew()
try{
    for($sample=0;$sample -lt 90;$sample++){
        $sampleStart=$clock.ElapsedMilliseconds
        $stage='Process identity'
        $all=@(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name,CreationDate)
        $currentRoot=@($all|Where-Object{$_.ProcessId -eq $rootId -and $_.CreationDate.ToUniversalTime() -eq $cimRootStart})
        if($currentRoot.Count -ne 1){throw 'The reference process exited or changed identity.'}
        if($all.Count -gt 2048){throw 'The guest process inventory exceeded the collector bound.'}
        $ids=[Collections.Generic.HashSet[int]]::new();[void]$ids.Add($rootId)
        for($depth=0;$depth -lt 8;$depth++){
            $added=0
            foreach($process in $all){if($ids.Contains([int]$process.ParentProcessId) -and $ids.Add([int]$process.ProcessId)){$added++}}
            if($added -eq 0){break}
        }
        $stage='Process counters'
        $working=0L;$private=0L;$virtual=0L;$handles=0L;$threads=0;$cpuSeconds=0.0;$cpuValid=($sample -gt 0)
        foreach($id in $ids){
            $process=$null
            try{
                $process=[Diagnostics.Process]::GetProcessById($id)
                $start=$process.StartTime.ToUniversalTime().Ticks;$key=$id.ToString()+':'+$start
                $cpu=$process.TotalProcessorTime.TotalSeconds
                if($previous.ContainsKey($key)){$cpuSeconds += [Math]::Max(0,$cpu-$previous[$key].Cpu)}else{$cpuValid=$false}
                $previous[$key]=@{Cpu=$cpu;Sample=$sample}
                $working+=$process.WorkingSet64;$private+=$process.PrivateMemorySize64;$virtual+=$process.VirtualMemorySize64
                $handles+=$process.HandleCount;$threads+=$process.Threads.Count
            }catch [ArgumentException]{$cpuValid=$false}catch [InvalidOperationException]{$cpuValid=$false}
            finally{if($process){$process.Dispose()}}
        }
        foreach($key in @($previous.Keys)){if($previous[$key].Sample -lt $sample){$previous.Remove($key)}}
        $stage='System memory counters'
        $memory=Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
        $logical=[Environment]::ProcessorCount
        $at=$clock.Elapsed.TotalSeconds
        $elapsed=if($sample -gt 0){$at-$previousTime}else{0}
        $cpuPercent=if($cpuValid -and $elapsed -gt 0){100*$cpuSeconds/$elapsed/$logical}else{$null}
        $previousTime=$at
        $collector.Refresh()
        $row=[pscustomobject]@{Trial=[int][Math]::Floor($sample/30)+1;Index=$sample;ObservedUtc=[DateTimeOffset]::UtcNow.ToString('O');TreeProcessCount=$ids.Count;CpuPercentOfGuest=$cpuPercent;WorkingSetBytes=$working;PrivateCommitBytes=$private;VirtualAddressBytes=$virtual;Handles=$handles;Threads=$threads;GuestAvailablePhysicalBytes=$memory.AvailableBytes;GuestCommittedBytes=$memory.CommittedBytes;GuestCommitLimitBytes=$memory.CommitLimit;GuestPagesInputPerSecond=$memory.PagesInputPersec;GuestPageReadsPerSecond=$memory.PageReadsPersec;CollectorCpuSeconds=$collector.TotalProcessorTime.TotalSeconds;CollectorWorkingSetBytes=$collector.WorkingSet64;CollectionMs=$clock.ElapsedMilliseconds-$sampleStart}
        $rows.Add($row)
        if($sample%10 -eq 0){[ordered]@{Stage='Sampling';ProcessId=$PID;StartedUtc=$started.ToString('O');AcceptedSamples=$rows.Count}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'reference-state.json') -Encoding utf8}
        $delay=1000-($clock.ElapsedMilliseconds-$sampleStart)
        if($delay -gt 0){Start-Sleep -Milliseconds $delay}
    }
}catch{$failures.Add($stage+':'+$_.Exception.GetType().Name)}finally{$collector.Dispose()}
$record=[ordered]@{FormatVersion=1;Study='RoALT idle reference baseline';StartedUtc=$started.ToString('O');CompletedUtc=[DateTimeOffset]::UtcNow.ToString('O');Reference=$identity;OperatingSystem=[Environment]::OSVersion.VersionString;LogicalProcessors=[Environment]::ProcessorCount;RequestedTrials=3;SamplesPerTrial=30;WarmupSeconds=15;ConfiguredRobloxClients=0;Virtualized=$true;GpuMode='WARP; physical GPU metrics unavailable';WorkingSetCanDoubleCountSharedPages=$true;CpuPercentDenominator='All logical processors exposed to guest';PrivateCommitIsNotPhysicalMemory=$true;CollectorOverheadIncludedInSystemCounters=$true;ComparableZenithTrialCompleted=$false;PerformanceWinnerEstablished=$false;Errors=@($failures.ToArray());Samples=@($rows.ToArray())}
$path=Join-Path $output 'roalt-idle.json'
$record|ConvertTo-Json -Depth 7|Set-Content -LiteralPath ($path+'.tmp') -Encoding utf8
Move-Item -LiteralPath ($path+'.tmp') -Destination $path -Force
[ordered]@{Stage=if($rows.Count -eq 90 -and $failures.Count -eq 0){'Complete'}else{'Failed'};ProcessId=$PID;AcceptedSamples=$rows.Count;CompletedUtc=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'reference-state.json') -Encoding utf8
if($rows.Count -ne 90 -or $failures.Count -ne 0){exit 1}
