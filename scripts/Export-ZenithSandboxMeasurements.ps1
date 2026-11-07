#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory=$true)][Guid]$ComputeSystemId,[switch]$SourceIndex)
# Retrieves only our bounded numeric benchmark report. Never copies program,
# account, vault, browser-profile, or authentication files from the guest.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output=Join-Path $root $(if($SourceIndex){'artifacts\reference-source-index.json'}else{'artifacts\reference-benchmark-uncontrolled.json'})
$temporary=$output+'.tmp'
try {
    $inventory=(& "$env:WINDIR\System32\hcsdiag.exe" list -raw)-join "`n"
    if($LASTEXITCODE -ne 0){throw 'Sandbox inventory was unavailable.'}
    $parsed=$inventory|ConvertFrom-Json
    $systems=if($parsed.PSObject.Properties.Name -contains 'value'){@($parsed.value)}else{@($parsed)}
    $match=@($systems|Where-Object{$_.Id -eq $ComputeSystemId.ToString() -and $_.Owner -eq 'WindowsSandbox' -and $_.State -eq 'Running'})
    if($match.Count -ne 1){throw ('The selected running Windows Sandbox could not be verified. Inventory: '+(@($systems|Select-Object Id,Owner,State)|ConvertTo-Json -Compress))}
    $guestPath=if($SourceIndex){'C:\ZenithLab\Research\source-index.json'}else{'C:\ZenithLab\Benchmarks\roalt-idle.json'}
    & "$env:WINDIR\System32\hcsdiag.exe" read $ComputeSystemId.ToString() $guestPath $temporary
    if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $temporary)){throw 'The benchmark report could not be retrieved.'}
    if((Get-Item -LiteralPath $temporary).Length -gt 1048576){throw 'The numeric inspection report exceeded its bound.'}
    $record=Get-Content -LiteralPath $temporary -Raw|ConvertFrom-Json
    if($SourceIndex) {
        if($record.FormatVersion -ne 1 -or $record.Study -ne 'Public reference source index' -or $record.Commit -notmatch '^[a-f0-9]{40}$' -or $record.ExecutedReferenceCode -ne $false -or @($record.Files).Count -gt 700){throw 'The source metadata report was invalid.'}
    } else {
        if($record.FormatVersion -ne 1 -or $record.Study -ne 'RoALT idle reference baseline' -or @($record.Samples).Count -ne 90){throw 'The benchmark report did not match the expected completed measurement.'}
        $record|Add-Member -NotePropertyName ComparisonEligible -NotePropertyValue $false -Force
        $record|Add-Member -NotePropertyName ExclusionReason -NotePropertyValue 'Uncontrolled workload. Client count was not sampled. RoALT later displayed already playing, but the user reports Roblox failed in the VM. Neither a confirmed client nor a controlled idle baseline is established.' -Force
    }
    $record|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $output -Force
} catch {
    [ordered]@{Stage='Failed';ErrorType=$_.Exception.GetType().Name;Message=$_.Exception.Message}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'artifacts\reference-export-status.json')
    throw
} finally {if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
