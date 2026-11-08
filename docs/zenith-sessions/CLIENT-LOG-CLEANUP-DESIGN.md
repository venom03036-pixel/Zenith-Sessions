# Roblox client log cleanup

Diagnostics > Roblox client logs removes old Roblox Player logs on demand, in two explicit steps, and never on its own. It is
Zenith's own version of a capability the RoALT reference has (its log cleaner deletes logs older than a day or over 50 MB, never
a running client's); no RoALT code was used, and the owner's decision of 2026-11-08 shaped it: show count and size first,
delete only logs older than N days that no bound or recent correlation needs, confirm, report. Age is the only rule; the size
rule was not adopted.

## What it touches

Only the current user's documented Roblox log roots below LocalApplicationData, the same two folders the log probe watches
(`RobloxClientFolders.LogRoots`: the Win32 `Roblox\logs` and the Store package's `LocalState\logs`). Each root must sit below
that anchor with no reparse point on the way (`RobloxClientFolders.IsBelowAnchor`, shared with the probe), only files directly
inside it are considered, and only `*.log` files whose name contains `_Player_`. Studio logs, crash reports and anything else
are never counted or deleted. Nothing is read from a log, Roblox's settings and files are not otherwise changed, and no path or
file name reaches diagnostics, exports or the support report.

## The two steps

1. **Check old logs** (`IClientLogCleanup.SurveyAsync`). The user enters a cutoff in whole days, 1 to 365, default 7. A
   cutoff under one day is refused because the correlator discovers logs written within a day. The check lists every Player
   log older than the cutoff that may be deleted and counts what is kept: newer logs, logs in use (see below) and logs that
   could not be checked (a reparse point, a denied or failed open). The status line shows the count and total size ("3 Roblox
   Player logs older than 7 days can be removed (48.0 MB). Kept: 2 newer, 1 in use or watched by Zenith. Nothing is deleted
   until you confirm."), says so when there is nothing to remove, and names the case where no Roblox logs folder exists.
2. **Delete these logs** is offered only after a check that found something. The view asks a question that names the same
   count and size, says the files are removed permanently (not sent to the Recycle Bin) and what is kept, and only a Yes runs
   `IClientLogCleanup.DeleteAsync` with exactly the checked list. Changing the days, Panic, Lock, Cancel and exit drop the
   checked list, so a stale count can never be deleted. The result is reported ("2 Roblox Player logs deleted (32.0 MB freed).
   Kept: 1 in use or watched by Zenith."); a stopped delete says what went before the stop and how many files were not touched.

## What is kept, and why a running client's log cannot be taken

- **Newer than the cutoff** by last write time. With a one-day minimum this covers every log the correlator can still discover.
- **Watched by the correlator.** `WindowsSessionLogCorrelator.WatchedLogPaths` lists, as of its last discovery, every
  discovered candidate (written within the last day) and every log still bound to an offered process. The cleanup reads it at
  the start of each check and each delete. It is kept apart from the correlator's snapshot, which carries no path (tests pin that).
  Recent-client history keeps no paths, so "recent correlation" is protected by time: anything written within the cutoff stays.
- **Held open by another process.** The check opens each candidate for reading while sharing only deletion. Windows refuses
  that open (sharing violation) while any other handle has the file open for reading or writing, as a running client's own log
  is, and the file is counted as in use. The delete uses the very same open with delete-on-close, so there is no window between
  "not held" and "deleted" in which a client could reopen it; the file is gone when the handle closes.
- **Changed since the check.** The delete re-reads each listed file and keeps one whose size or last write differs, one that
  is no longer older than the cutoff, or one that is already gone.

Only a Player log directly inside one of the documented roots, still below the anchor, is ever deleted, whatever the list says.

## Shell, stopping and sample mode

`ShellClientLogCleanupFeatures` owns the two `AsyncCommand`s, the Cancel command and the status; the view's `DeleteOldClientLogs`
handler asks the question from `ClientLogCleanupConfirmation`. Each check or delete runs as one pool task with a token linked to
the shell's lifetime; `StopClientLogCleanup` is a `Step` in `StopOperations`, so Panic and Lock cancel the work and drop the
checked list, and `DisposeClientLogCleanupFeaturesAsync` joins the work on exit. Failures record only the exception type against
the new append-only `ManagerSubsystem.ClientLogs` ("Roblox client logs") and show a fixed message; a refused days value echoes
the user's own text. In sample mode with no injected cleanup the tool stays off, so the sample profile never reads or deletes
the real Roblox folders; the packaged layout self-check verifies the controls, their bindings and that boundary. No setting is
saved; the days value is per run.

## Rule 18

CPU and time: one check inspects at most 8,192 directory entries within 10 seconds and lists at most 2,048 files, each costing
one metadata read and one open; a larger folder is cleaned in rounds, each shown and confirmed on its own. A delete costs one
open per listed file and checks cancellation before each. Memory: the checked list holds a path, a size and a time per file,
bounded by the 2,048 cap. Threads: one pool task per call, no timer, no poller, no new dependency (System.IO only). Errors: a
failed root enumeration surfaces as a failure; a failed file is counted, never swallowed silently. Testability: the cleanup is
tested against synthetic files in a temporary anchor (`RobloxClientLogCleanupTests`), with a handle this test host holds as the
"running client"; the shell is tested over a fake `IClientLogCleanup` (`ClientLogCleanupPresentationTests`). Security: no
credentials, no AI, no network; it deletes files Roblox owns, which is why every step is explicit. Backwards compatibility: no
settings, queue or history format changed.

## What the live trial must confirm

That a real Roblox client's open log is refused by the exclusive-share open (the tests prove it with a file this test host holds
open with read sharing, as the C runtime does), and that the Player log names Roblox writes today still contain `_Player_`.
