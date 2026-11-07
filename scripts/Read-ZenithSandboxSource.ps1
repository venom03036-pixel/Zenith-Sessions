#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^ZenithSourceRead\.[a-f0-9]{32}$')][string]$PipeName,
    [Parameter(Mandatory)][string]$PathsBase64,[ValidateRange(1,5000)][int]$StartLine=1,
    [ValidateRange(1,240)][int]$LinesPerFile=160)
# A bounded, read-only source viewer. Source text is streamed to the caller, never saved on the host.
# This never executes reference code, reads account data, mounts paths or changes the guest.
$ErrorActionPreference='Stop'
$commit='4d250c0565f5e6daea99797d591c83f76108b916'
$result=[ordered]@{FormatVersion=1;State='Failed';Step='Connect';ObservedUtc=[DateTimeOffset]::UtcNow.ToString('O');Commit=$commit;GuestId=$null;Files=@();ErrorType=$null;NativeExitCode=$null}
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$PipeName,[IO.Pipes.PipeDirection]::Out,[IO.Pipes.PipeOptions]::Asynchronous)
try {
    $pipe.Connect(15000)
    $result.Step='Validate source selection'
    if($PathsBase64.Length -gt 8192) {throw 'Source selection is oversized.'}
    $selectionJson=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($PathsBase64))
    $paths=ConvertFrom-Json -InputObject $selectionJson
    if($paths.Count -lt 1 -or $paths.Count -gt 6) {throw 'Select one to six public source files.'}
    $root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $index=Get-Content -LiteralPath (Join-Path $root 'artifacts/reference-source-index.json') -Raw|ConvertFrom-Json
    foreach($path in $paths) {
        if($path -isnot [string] -or $path.Length -gt 180 -or $path -notmatch '^[A-Za-z0-9_./-]+$' -or $path.Contains('..') -or
            $path.StartsWith('/') -or $path -notin @($index.Files.Path)) {throw 'Only known public-source paths are readable.'}
    }
    $result.Step='Identify running guest'
    $raw=(& "$env:WINDIR/System32/hcsdiag.exe" list -raw)-join "`n"
    if($LASTEXITCODE -ne 0 -or $raw.Length -gt 1048576) {throw 'Sandbox inventory is unavailable.'}
    $inventory=$raw|ConvertFrom-Json
    $systems=if($inventory.PSObject.Properties.Name -contains 'value') {@($inventory.value)} else {@($inventory)}
    $guests=@($systems|Where-Object {$_.Owner -eq 'WindowsSandbox' -and $_.State -eq 'Running'})
    if($guests.Count -ne 1 -or $guests[0].Id -notmatch '^[0-9a-fA-F-]{36}$') {throw 'Exactly one running Windows Sandbox is required.'}
    $result.GuestId=$guests[0].Id
    $files=[Collections.Generic.List[object]]::new()
    foreach($path in $paths) {
        $result.Step='Read known source file'
        $guestPath='C:\ZenithLab\Research\'+$commit+'\'+$path.Replace('/','\')
        $start=[Diagnostics.ProcessStartInfo]::new("$env:WINDIR/System32/hcsdiag.exe")
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
        $start.Arguments='read '+$result.GuestId+' "'+$guestPath+'"'
        $process=[Diagnostics.Process]::Start($start)
        try {
            $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
            if(-not $process.WaitForExit(10000)) {$process.Kill();$process.WaitForExit();throw 'Source read exceeded its deadline.'}
            $text=$stdout.GetAwaiter().GetResult();$null=$stderr.GetAwaiter().GetResult()
            $result.NativeExitCode=$process.ExitCode
            if($process.ExitCode -ne 0 -or $text.Length -gt 2097152) {throw 'The known source file is unavailable or oversized.'}
            $result.Step='Verify indexed source fingerprint'
            $hasher=[Security.Cryptography.SHA256]::Create()
            try {$hash=([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-','')} finally {$hasher.Dispose()}
            $expected=@($index.Files|Where-Object {$_.Path -eq $path})
            if($expected.Count -ne 1 -or $hash -ne $expected[0].Sha256) {throw 'The source file does not match its previously indexed public version.'}
            $lines=$text -split '\r?\n'
            $selection=($lines|Select-Object -Skip ($StartLine-1) -First $LinesPerFile)-join "`n"
            if($selection.Length -gt 65536) {throw 'The selected source excerpt exceeds its display bound.'}
            $files.Add([ordered]@{Path=$path;Sha256=$hash;MatchesIndexedSource=$true;StartLine=$StartLine;DisplayedLines=[Math]::Max(0,[Math]::Min($LinesPerFile,$lines.Count-$StartLine+1));TotalLines=$lines.Count;Text=$selection})
        } finally {$process.Dispose()}
    }
    $result.Files=@($files.ToArray());$result.State='Complete';$result.Step='Complete'
} catch {$result.ErrorType=$_.Exception.GetType().Name}
finally {
    if($pipe.IsConnected) {
        $writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true)
        try {$writer.Write(($result|ConvertTo-Json -Depth 5 -Compress));$writer.Flush()} finally {$writer.Dispose()}
    }
    $pipe.Dispose()
}
if($result.State -ne 'Complete') {exit 1}
