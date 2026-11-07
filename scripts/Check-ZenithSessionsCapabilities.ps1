param([string]$OutputDirectory, [switch]$RunNativeProbe)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $sourceRoot 'artifacts\sessions-feasibility'
}
$project = Join-Path $sourceRoot 'tools\Zenith.Sessions.CapabilityHarness\Zenith.Sessions.CapabilityHarness.csproj'
$nativeArguments = @()
if ($RunNativeProbe) {
    $nativeProject = Join-Path $sourceRoot 'tools\Zenith.Sessions.NativeProbe\Zenith.Sessions.NativeProbe.csproj'
    & dotnet build $nativeProject --configuration Release --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $nativeExecutable = Join-Path $sourceRoot 'tools\Zenith.Sessions.NativeProbe\bin\Release\net10.0-windows10.0.19041.0\Zenith.Sessions.NativeProbe.exe'
    $nativeReport = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'native-probe.json'
    $start = [System.Diagnostics.ProcessStartInfo]::new($nativeExecutable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Under Windows PowerShell 5.1 (.NET Framework) ProcessStartInfo has no ArgumentList. A Windows path cannot contain a quote,
    # and this one ends in a file name rather than a backslash, so plain quoting reaches the probe unchanged.
    $start.Arguments = '--output "' + $nativeReport + '"'
    $launchedUtc = [DateTime]::UtcNow
    $probe = [System.Diagnostics.Process]::Start($start)
    try {
        if (-not $probe.WaitForExit(35000)) {
            $probe.Kill()
            $probe.WaitForExit(5000) | Out-Null
            throw 'The isolated native probe exceeded its deadline; no gate was approved.'
        }
        Write-Output $probe.StandardOutput.ReadToEnd()
        Write-Output $probe.StandardError.ReadToEnd()
        if ($probe.ExitCode -notin @(0, 3)) { throw 'The native probe failed; no gate was approved.' }
        if (-not (Test-Path -LiteralPath $nativeReport) -or (Get-Item -LiteralPath $nativeReport).LastWriteTimeUtc -lt $launchedUtc) {
            throw 'A fresh native report was not produced; the previous result will not be reused.'
        }
    } finally { $probe.Dispose() }
    $nativeArguments = @('--native-report', $nativeReport)
}
& dotnet run --project $project --configuration Release --no-launch-profile --disable-build-servers -p:UseSharedCompilation=false -p:BuildInParallel=false -- --source-root $sourceRoot --output $OutputDirectory @nativeArguments
$result = $LASTEXITCODE
if ($result -eq 3) {
    Write-Output 'Inspection completed. The required live-client capabilities remain blocked; the full companion was not installed.'
}
exit $result
