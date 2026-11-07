[CmdletBinding()]
param([Parameter(Mandatory)][string[]]$RelativePaths,[ValidateRange(1,5000)][int]$StartLine=1,
    [ValidateRange(1,240)][int]$LinesPerFile=160)
$ErrorActionPreference='Stop'
$name='ZenithSourceRead.'+[Guid]::NewGuid().ToString('N')
$pipe=[IO.Pipes.NamedPipeServerStream]::new($name,[IO.Pipes.PipeDirection]::In,1,[IO.Pipes.PipeTransmissionMode]::Byte,
    ([IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly))
$deadline=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(45))
$process=$null
try {
    $wait=$pipe.WaitForConnectionAsync($deadline.Token)
    $selection=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @($RelativePaths) -Compress)))
    $helper=Join-Path $PSScriptRoot 'Read-ZenithSandboxSource.ps1'
    $arguments='-NoProfile -File "'+$helper+'" -PipeName '+$name+' -PathsBase64 '+$selection+' -StartLine '+$StartLine+' -LinesPerFile '+$LinesPerFile
    $process=Start-Process -FilePath "$env:WINDIR/System32/WindowsPowerShell/v1.0/powershell.exe" -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    $wait.GetAwaiter().GetResult()
    $reader=[IO.StreamReader]::new($pipe,[Text.UTF8Encoding]::new($false),$false,4096,$true)
    try {$result=$reader.ReadToEndAsync($deadline.Token).GetAwaiter().GetResult()} finally {$reader.Dispose()}
    if($result.Length -gt 420000) {throw 'Source display response exceeded its bound.'}
    $parsed=$result|ConvertFrom-Json
    if($parsed.FormatVersion -ne 1 -or $parsed.State -ne 'Complete') {throw ('Read-only guest inspection failed at '+$parsed.Step+' ('+$parsed.ErrorType+'; native exit '+$parsed.NativeExitCode+'). Guest data was preserved.')}
    $result
} finally {
    $deadline.Cancel();$deadline.Dispose();$pipe.Dispose()
    if($null -ne $process) {$process.Dispose()}
}
