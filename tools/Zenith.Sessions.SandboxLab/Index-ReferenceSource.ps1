# Guest-only public text inspection. Never runs or builds downloaded source.
$ErrorActionPreference='Stop'
if([Security.Principal.WindowsIdentity]::GetCurrent().Name -notmatch '\\WDAGUtilityAccount$'){throw 'Public reference source stays inside Windows Sandbox.'}
Add-Type -AssemblyName System.Net.Http
$client=[Net.Http.HttpClient]::new();$client.Timeout=[TimeSpan]::FromSeconds(25);$client.MaxResponseContentBufferSize=2097152
$client.DefaultRequestHeaders.UserAgent.ParseAdd('Zenith-Source-Inspection/1.0')
$output='C:\ZenithLab\Research';[IO.Directory]::CreateDirectory($output)|Out-Null
$rows=[Collections.Generic.List[object]]::new();$errors=[Collections.Generic.List[object]]::new();$total=0L
$terms=[ordered]@{NativeLaunch='Command::new|Process.Start|authentication_ticket';InstanceHandling='singleton|multi.instance';Capture='GraphicsCapture|requestAnimationFrame|setInterval';Memory='EmptyWorkingSet|PrivateUsage|private_bytes|WorkingSet';Queue='mpsc::channel|sync_channel|unbounded|bounded|VecDeque';Workers='thread::|spawn_blocking|async_runtime::spawn';Persistence='Sqlite|sqlcipher|AES|Argon2|DPAPI|atomic';Observation='ProcessId|GetProcessTimes|logwatch|browser_tracker';Updates='updater|verify_signature|Authenticode';UI='useEffect|useMemo|useQuery|virtualiz'}
try {
    $tree=$client.GetStringAsync('https://api.github.com/repos/inspectsoftware/RoALT/git/trees/v0.9.2?recursive=1').GetAwaiter().GetResult()|ConvertFrom-Json
    if($tree.truncated -or $tree.sha -notmatch '^[a-f0-9]{40}$'){throw 'A complete pinned public source tree is required.'}
    $files=@($tree.tree|Where-Object{$_.type -eq 'blob' -and ($_.path -match '\.(rs|tsx|ts|css|toml)$' -or $_.path -in @('LICENSE','package.json','src-tauri/tauri.conf.json'))})
    if($files.Count -gt 700){throw 'The public source tree exceeds this inspection bound.'}
    $directory=Join-Path $output $tree.sha;[IO.Directory]::CreateDirectory($directory)|Out-Null
    foreach($entry in $files){
        if($entry.path -notmatch '^[A-Za-z0-9_./-]+$' -or $entry.path.Contains('..') -or $entry.size -gt 2097152){throw 'An unsafe or oversized source path was refused.'}
        try {
            $text=$client.GetStringAsync(('https://raw.githubusercontent.com/inspectsoftware/RoALT/'+$tree.sha+'/'+$entry.path)).GetAwaiter().GetResult()
            $bytes=[Text.Encoding]::UTF8.GetBytes($text);$total+=$bytes.Length;if($total -gt 33554432){throw 'The source text exceeds the total inspection budget.'}
            $path=Join-Path $directory $entry.path;[IO.Directory]::CreateDirectory((Split-Path -Parent $path))|Out-Null
            [IO.File]::WriteAllBytes($path,$bytes)
            $hits=[ordered]@{};foreach($term in $terms.GetEnumerator()){$hits[$term.Key]=[regex]::Matches($text,$term.Value,[Text.RegularExpressions.RegexOptions]::IgnoreCase).Count}
            $hash=[Security.Cryptography.SHA256]::Create();try{$sha=([BitConverter]::ToString($hash.ComputeHash($bytes))).Replace('-','')}finally{$hash.Dispose()}
            $rows.Add([pscustomobject]@{Path=$entry.path;GitBlob=$entry.sha;Sha256=$sha;Bytes=$bytes.Length;Lines=($text.Split("`n")).Length;TermReferences=$hits})
        } catch {$errors.Add([pscustomobject]@{Path=$entry.path;ErrorType=$_.Exception.GetType().Name})}
        [ordered]@{Stage='Indexing';ProcessId=$PID;Completed=$rows.Count;Expected=$files.Count;Failed=$errors.Count}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'source-state.json')
    }
    [ordered]@{FormatVersion=1;Study='Public reference source index';Repository='inspectsoftware/RoALT';Ref='v0.9.2';Commit=$tree.sha;ObservedUtc=[DateTimeOffset]::UtcNow.ToString('O');ExecutedReferenceCode=$false;SourceStoredOnlyInGuest=$true;ExpectedFiles=$files.Count;Files=@($rows.ToArray());Errors=@($errors.ToArray());Interpretation='Term counts are navigation aids, not proof of defects, vulnerability, performance, or runtime behavior.'}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'source-index.json') -Encoding UTF8
    [ordered]@{Stage=if($errors.Count -eq 0){'Complete'}else{'Partial'};ProcessId=$PID;Completed=$rows.Count;Expected=$files.Count;Failed=$errors.Count}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'source-state.json')
} finally {$client.Dispose()}
if($errors.Count -ne 0){exit 1}
