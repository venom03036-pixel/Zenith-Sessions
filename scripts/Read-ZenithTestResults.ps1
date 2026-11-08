param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference='Stop'
[xml]$result=Get-Content -LiteralPath $Path -Raw
$counter=$result.TestRun.ResultSummary.Counters
$rows=@($result.TestRun.Results.UnitTestResult)
$skipped=@($rows | Where-Object {$_.outcome -eq 'NotExecuted'})
$passed=@($rows | Where-Object {$_.outcome -eq 'Passed'})
$failed=@($rows | Where-Object {$_.outcome -notin @('Passed','NotExecuted')})
if($rows.Count -eq 0 -or $passed.Count -eq 0 -or $failed.Count -ne 0 -or [int]$counter.failed -ne 0 -or [int]$counter.error -ne 0 -or [int]$counter.timeout -ne 0 -or [int]$counter.aborted -ne 0){throw 'The TRX contains no passing executed tests, or a failure/error/timeout/aborted result.'}
if(@($rows | Where-Object {$_.testName -like '*AIVision_CloudProvider_ClicksGreenTargetButton_WhenKeyConfigured*'}).Count -ne 0){throw 'The excluded paid test appeared in the results.'}
if([int]$counter.total -ne $rows.Count -or [int]$counter.executed -ne ($rows.Count-$skipped.Count) -or [int]$counter.passed -ne $passed.Count){throw 'The TRX result inventory and execution counters disagree.'}
[pscustomobject]@{Total=$rows.Count;Executed=($rows.Count-$skipped.Count);Passed=$passed.Count;Failed=$failed.Count;NotExecuted=$skipped.Count;SkippedNames=@($skipped | ForEach-Object {[string]$_.testName});ReportedNotExecuted=[int]$counter.notExecuted}
