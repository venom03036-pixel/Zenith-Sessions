# Shutdown and public-network review repairs

Review target: immutable `3cfa4c6..ec48e0d`. This was a partial independent source review of shutdown, network enforcement, updater preparation/cancellation, log cleanup, CPU/audio restoration, keep-awake and arrival layout. It did not establish complete changed-file coverage or live Windows/Roblox behavior.

## Shutdown submission ownership (0.65-shutdownabort)

A tool can submit Windows' scheduled shutdown before its process wait finishes. Cancelling that wait formerly threw away the outcome; the shell then had no scheduled flag and skipped its promised abort on Tray Exit. Killing shutdown.exe does not undo the OS schedule.

The runner now distinguishes cancellation before process creation from cancellation after creation. Before creation, no tool starts. Afterwards, the known exit code is retained, or the result is explicitly unconfirmed. A synthetic process seam exercises this distinction without ever running shutdown.exe. The shell retains a separate cleanup obligation for an unconfirmed submission, aborts it on Panic or Tray Exit with the exit cleanup token, and clears it only after cancellation is confirmed or Windows says nothing is scheduled. It never automatically aborts a confirmed preexisting schedule, and Windows session-end cleanup does not undo its own shutdown. An unconfirmed attempt cannot identify the OS-wide schedule's creator; cleanup may cancel whichever shutdown Windows holds, exactly as the existing explicit Cancel action does.

Evidence: `artifacts/sessions-tests-0.65-shutdownabort/shutdown-ownership-red.trx` records **3 expected failures, 2 passes** against the original behavior with only the synthetic process seam added. The failures are after-start cancellation, Exit losing an unconfirmed submission, and Panic losing a late unconfirmed submission. `shutdown-ownership-green.trx` passes **57 tests**, including preexisting schedules, pre-start cancellation, known exit codes, session end, stop/lifecycle and production-window contracts. `shutdown-ownership-final.trx` repeats those checks after the final truthful-status guard.

No real shutdown, sign-in, Roblox process, network change or paid AI test ran. The change adds no polling, package or unbounded worker: one existing bounded helper invocation and a small in-memory ownership flag. Reverting this repair restores the previous behavior. Real Windows timing and interaction remain an attended test; release/live readiness is unchanged.

### Failed-wait follow-up (0.65-shutdownwait)

Independent review of the first repair found that its generic failed-wait branch retained schedule uncertainty but could leave the helper running. Two controlled cases failed first in `artifacts/sessions-tests-0.65-shutdownwait/shutdown-wait-red.trx`. A failed wait now attempts to stop the exact owned helper before returning unconfirmed, and a stop failure cannot erase that uncertainty. A defensive handle-release guard preserves an already known result if the test seam throws during disposal; this is not a reproduced defect in Windows Process.Dispose. The combined accepted run, `artifacts/sessions-tests-0.66-networkepoch/network-shutdown-final.trx`, passes 77 affected shutdown/network/lifecycle/production-window tests, with no skips or failures. No native shutdown was run.

## Public-network notification ordering (0.66-networkepoch)

The first Public notification immediately holds queue admission, before the UI dispatcher applies its lock and status. Previously, an older queued NotPublic or Unknown callback could clear that newer hold. A callback retained by a stopped monitor could also change the state after the guard was enabled again.

Each watch now owns a distinct callback generation, and each delivered reading has a monotonically increasing revision. Applying a queued reading validates both and publishes the admission flag under one short lock, so a newer callback cannot be overtaken between validation and publication. Stop invalidates the old generation before unsubscribing. The initial Start result cannot replace a later notification. Dispatcher notifications, queue signals and Windows monitor operations remain outside that lock; the opt-in setting, Unknown policy and UI layout are unchanged.

`artifacts/sessions-tests-0.66-networkepoch/network-epoch-red.trx` records all four regressions failing on the prior behavior: old NotPublic/Unknown callbacks temporarily lift a newer Public hold, and old-watch callbacks incorrectly change the restarted watch in either direction. The tests inspect real shell admission between queued callbacks, with a fake monitor only. The final combined `network-shutdown-final.trx` passes **77 tests, 0 failures, 0 skips**, including these four cases, shutdown ownership and existing network/lifecycle/production-window contracts. The test build emitted no warning.

No real adapter/network settings were changed. There is one delegate per active watch and constant-size epoch/revision fields; no polling, task loop, persisted data or package was added. Rollback is reverting this network repair. Native Windows notifications and attended live acceptance remain separate from this evidence.
