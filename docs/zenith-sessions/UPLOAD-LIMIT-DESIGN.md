# Upload limit (R-instances-18)

Owner decision of 2026-11-08: an opt-in upload bandwidth limit for Roblox clients through a Windows QoS policy
(New-NetQosPolicy / Remove-NetQosPolicy run elevated with the UAC prompt the user accepts), showing the current policy and a
Remove button; never applied without the user's click. It is Zenith's own version of a capability the RoALT reference has;
no RoALT code, text or branding was used. The still-excluded techniques stay excluded: nothing in the client is read or
written, nothing is injected, and no Roblox request is made.

## What it does

Sessions > Upload limit takes a whole rate in kilobits per second (100 to 10,000,000) and offers **Apply upload limit**,
**Remove** and **Cancel**. Apply and Remove each start Windows PowerShell 5.1 as administrator, so Windows shows its
administrator prompt first; nothing changes until the user approves it, and Zenith never applies a limit on its own. The
policy is one Windows QoS policy per client executable Sessions observes (`RobloxPlayerBeta.exe`, `RobloxPlayer.exe`,
`Roblox.exe`), named `ZenithSessionsUploadLimit-<executable>`, in Windows' **active policy store only**: it is in force at
once and ends when Windows restarts, so nothing about the machine stays changed. It is one shared ceiling on what every
matching client sends (`ThrottleRateActionBitsPerSecond`), not a share per client and not a download limit.

Reading the active store needs administrator rights (an unelevated `Get-NetQosPolicy -PolicyStore ActiveStore` is denied),
so "the current policy" is Zenith's own record of the last change it asked for, kept in `upload-limit.json` in the data folder
for this Windows session, and the section says so in words: not a readback, another administrator may have changed it since.
Each change is confirmed where it can be: the same administrator run reads the policy back and reports through its exit code.

## Owners and fences

| Piece | Owner | What it does |
|---|---|---|
| `UploadLimitPolicy` (Core) | the pure part | The rate range and typed-rate parsing (digits with space or comma separators), the two fixed script texts with only the checked integer in them, the exit-code meanings (0 confirmed, 3 readback mismatch, anything else refused), the record's Windows-session check (boot times within two minutes), and the 60 s wait. |
| `UploadLimitResult`, `UploadLimitRecord`, `IUploadLimitControl` (Core) | contract | Typed statuses with plain explanations; the record is the last action, the rate asked for, when, and whether Windows confirmed it. |
| `ElevatedPowerShellRunner` (Windows) | the administrator run | `ShellExecute` with the `runas` verb on `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`, `-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand <script>`. The script is an encoded command, so there is no quoting and no `-ExecutionPolicy Bypass`: execution policy applies to script files, not to a command. `ShellExecute` returns only when the prompt is answered, so the start runs on the thread pool; a declined prompt is `ERROR_CANCELLED` (1223); the exit code is read through the handle `ShellExecute` hands back; the wait is bounded by the 60 s timeout. A wait stopped at the prompt leaves the start to release its handle when it comes, tracked and awaited (bounded) at dispose. Tests pass `elevate: false` to run the same scripts without rights. |
| `WindowsUploadLimitControl` (Windows) | ownership | One operation at a time (a second is `Busy`); `CancelOperations` cancels the wait; dispose cancels and awaits the in-flight operation. Maps the run to a status, and keeps the record: a confirmed Apply records the rate, a confirmed Remove deletes the record, and a refused, unconfirmed, timed-out or stopped run marks the record **uncertain** with the rate asked for (a Remove keeps the last known rate), because the store may have changed partly or the approved run may still finish. A declined prompt or a missing PowerShell ran nothing and changes nothing. A record write that fails leaves the result confirmed, flagged `RecordSaved: false`. |
| `UploadLimitRecordFile` (Windows) | the record | `upload-limit.json`: `schema` 1, `lastAction`, `kilobitsPerSecond`, `atUtc`, `bootUtc`, `confirmed`; written through a temporary file. Read once at startup: a record from an earlier Windows session (boot time outside the tolerance) describes a policy Windows has dropped, so it is deleted and nothing is shown; a damaged record is reported unreadable, never guessed at, and the next change replaces it. |
| `ShellViewModel` (`ShellUploadLimitFeatures.cs`) | presentation | `UploadLimitRate` (prefilled 1000, or the recorded rate), `ApplyUploadLimit`, `RemoveUploadLimit`, `CancelUploadLimit`, `UploadLimitStatus`, `CurrentUploadLimitText`. The rate is read at the click (an invalid one is refused in words before anything runs). `StopUploadLimitOperations` runs as a Panic/Lock step and at exit; a Cancelled result after Panic or Lock says so. A declined prompt or a stop is a status line; a refused, unconfirmed, unavailable or timed-out run is an error with the new `ManagerSubsystem.UploadLimit`. Sample mode without an injected control never offers it. |
| `MainWindow` | the section and the Guide | The expander, the rate box, the three buttons, the record line, the status line, and the caveats. Settings search ("upload limit", "bandwidth", "qos") lands on the rate box, which changes nothing by itself. |

## The scripts

Apply, run as administrator with `$ErrorActionPreference = 'Stop'`: for each executable, `Remove-NetQosPolicy` of Zenith's
policy (a missing one is silenced), `New-NetQosPolicy -Name <Zenith's name> -AppPathNameMatchCondition <executable>
-ThrottleRateActionBitsPerSecond <rate × 1000> -PolicyStore ActiveStore`, then `Get-NetQosPolicy -Name ... -PolicyStore
ActiveStore` and `exit 3` if its `ThrottleRateAction` or `AppPathNameMatchCondition` differs; `exit 0` at the end. Any error
stops the run with exit 1. Remove: for each executable, the same `Remove-NetQosPolicy`, then the store is listed with Stop
and `exit 3` if Zenith's policy is still there. Listing with Stop is deliberate: unelevated, both scripts end with exit 1
(verified in `UploadLimitNativeTests`), so a denied read can never pass as a confirmed removal.

## What is never done

- No policy is created, changed or removed without the user's click and Windows' own administrator prompt.
- No persistent (registry or Group Policy) store is written; the policy ends when Windows restarts.
- Nothing is removed on exit: a second prompt at exit would surprise, and the user has Remove. The Guide says so.
- No client file, setting, FFlag or memory is read or written; no Roblox request is made; no User-Agent is involved.
- No readback is claimed that was not made: the section shows Zenith's record and says when it is uncertain or unreadable.
- No `-ExecutionPolicy Bypass`, no PowerShell with user text in it (only the checked integer varies), no new dependency.

## Rules 18 and 19

One process start per click on the thread pool, one bounded wait (60 s) on the process handle (no poll, no timer), one small
JSON record read once at startup and written per change. Cancellation stops every wait; a run the user already approved
finishes on its own within seconds, and the status says so. Failures are typed statuses and exit codes, never paths,
command lines or exception text. Old settings files are untouched (no settings field was added). Windows PowerShell 5.1 and
`ShellExecute` are the platform tools the owner named; a CIM client library would have been a new dependency for the same
calls, and a `-Command` string would have needed quoting rules the encoded command avoids.

## Verified and not verified

Tests: `UploadLimitPolicyTests` (pure), `UploadLimitControlTests` (control over a fake administrator run and a temporary
record file), the upload-limit tests in `ClientPreviewPresentationTests` (shell over a probe control, Panic/Lock/Cancel, the
error subsystem, the XAML and Guide), and `UploadLimitNativeTests` (Windows PowerShell 5.1 parses both scripts without running
them; the production runner's unelevated runs exit 1). Not verified here, because no test may answer a UAC prompt or change
this PC's policies: the elevated Apply creating the three policies in the active store and reading them back, the elevated
Remove deleting them, and any effect on the clients' traffic (which also needs the QoS Packet Scheduler bound to the adapter,
as it is by default). Zenith has not measured any effect on game play or on other programs.
