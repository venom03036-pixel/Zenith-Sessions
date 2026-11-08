# Closing a client (R-instances-12)

Owner decision of 2026-11-08: a stuck client may be closed in steps. First WM_CLOSE to its window, again after about
400 ms for Roblox's leave prompt; if the process is still alive after a bounded wait, offer TerminateProcess behind an
explicit confirmation that names the client; record it in history as a forced end; never automatic. This reverses the
earlier "never force-kill" rule in exactly that bounded way and in no other: there is no batch kill, no kill on Panic, and
nothing ends a process without the user's confirmation for that one client.

## Owners and fences

| Piece | Owner | What it does |
|---|---|---|
| `IClientCloseControl` (Core) | contract | `CloseAsync`, `ForceCloseAsync`, `CancelCloseOperations`; result records with plain statuses. `ClientCloseResult.OffersForceClose` is true only for StillOpen after at least one request reached the client. |
| `ClientCloseOperations` (Windows) | the escalation | Opens the exact instance, posts WM_CLOSE, waits `LeavePromptDelay` (400 ms) on the process's exit, posts again, waits until `ExitGrace` (6 s from the first post) has passed, and reports Exited, StillOpen, NotCurrent, NotSent or Cancelled. Force close opens a second handle with the terminate right, checks it, terminates, stamps the time and confirms the exit for `TerminationGrace` (2 s). A generation counter makes Panic, Lock and exit end every waiting close at its next step. |
| `NativeClientCloseAdapter` (Windows) | the Windows calls | `OpenProcess` with query-limited and synchronize rights (plus terminate for the force path), kept only when the process is alive, `GetProcessTimes` creation time equals the observed start exactly (`ProcessInstanceIdentity`), and `QueryFullProcessImageName` names an accepted client executable. `PostClose` goes through `NativeClientWindowAdapter.SameClient` (PID, start, top-level window, owner thread, executable). Exit waits are `RegisterWaitForSingleObject` on a SYNCHRONIZE-only duplicate of the handle (Rule 14). `TerminateProcess` runs on the checked handle after a last liveness check; a refusal after the process exited reads as AlreadyExited, any other as Denied. |
| `WindowsDesktopMonitor` | composition | Implements the control beside the window operations. Closes do not take the window lane, so a 6 s wait never holds up Tile, Minimize or Resize. `RequestCloseAsync` (one post, no wait) stays for the plain monitor contract. |
| `ShellViewModel` (`ShellClientCloseFeatures.cs`) | presentation | `RequestCloseAsync` runs the escalation when the monitor offers the control and keeps the single request otherwise (sample mode, tests). One close per client at a time. StillOpen sets `SessionRow.ForceCloseOffered` on that row only, when not locked. `ForceCloseAsync` is refused unless that row offers it and the dashboard is not locked; an accepted termination records `RecentClients.RecordForcedEnd` and writes an activity line. `StopClientCloseOperations` runs as a Panic/Lock step and at shutdown: cancels every waiting close, cancels the control, and withdraws every offer. Exit awaits the in-flight work. |
| `MainWindow` | confirmations | Close keeps its confirmation. Force close is a row button visible only while offered; its confirmation names the client (PID and start time), says what is lost, and defaults to No. |
| `RecentClientHistory` / store | record | `RecentClientHistoryEntry.ForcedEndUtc`; `ClientEndCause.ForcedEnd` ranks first because TerminateProcess leaves no fault record and other evidence describes the client before it was ended. `RecordForcedEnd` accepts the active entry or one that ended within the inventory age (the termination and the scan race). The store writes `Forced` in the optional evidence column; the payload keeps its fourteen fields, so builds that predate end evidence still read and rewrite the history (they drop the field, as they drop the other evidence). |

## Why these numbers

- 400 ms matches the time Roblox needs to show its leave prompt before a second WM_CLOSE is read as the answer. It is
  short enough that a client already closing is not asked twice (the first wait usually sees the exit).
- 6 s is long enough for a healthy client to leave a game and exit, and short enough that a user waiting on a hung client
  is offered the next step quickly. It is a constant of the control, shortened only by tests.
- 2 s after an accepted termination is only a confirmation wait; Windows guarantees the end once the call is accepted, so
  an unconfirmed exit is still recorded as a forced end.

## What is never done

- No termination without the second confirmation, no batch, no termination on Panic, Lock, exit or arrival.
- No handle is kept open across operations; each close or force close opens and closes its own.
- No message is sent to a window whose process, start time, owner thread or executable does not match the observation.
- Nothing in the client's files or settings is touched; the leave prompt is answered by the ordinary close message only.

## Rule 18 checklist

CPU: two posts and two registered waits per click; no polling. Memory: one handle pair per in-flight close, at most one
per client. Threads: pool callbacks only. Cancellation: the linked token, the generation counter and the shell's stop step.
Errors: Win32 refusals become statuses with plain wording; unexpected exceptions reach `Fail(..., ClientWindows)`.
Testability: a scripted adapter for the escalation and a PowerShell WinForms helper for the Windows calls. Security: no
secret is involved; PIDs and times only. Compatibility: settings unchanged; the history store keeps format 1 and the
fourteen-field payload.

## Verification

`ClientCloseOperationsTests` (scripted adapter), `ClientCloseNativeTests` (test-owned window helper through the production
adapter), `ClientClosePresentationTests` (shell and XAML), `RecentClientHistoryTests` and `RecentClientHistoryStoreTests`.
Unverified until a live trial: Roblox's actual leave-prompt timing against the 400 ms gap, and how long a real client takes
to exit within the 6 s wait.
