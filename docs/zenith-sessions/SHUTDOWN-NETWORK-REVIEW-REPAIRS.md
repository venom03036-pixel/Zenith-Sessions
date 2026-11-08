# Shutdown and public-network review repairs

Review target: immutable `3cfa4c6..ec48e0d`. This was a partial independent source review of shutdown, network enforcement, updater preparation/cancellation, log cleanup, CPU/audio restoration, keep-awake and arrival layout. It did not establish complete changed-file coverage or live Windows/Roblox behavior.

## Shutdown submission ownership (0.65-shutdownabort)

A tool can submit Windows' scheduled shutdown before its process wait finishes. Cancelling that wait formerly threw away the outcome; the shell then had no scheduled flag and skipped its promised abort on Tray Exit. Killing shutdown.exe does not undo the OS schedule.

The runner now distinguishes cancellation before process creation from cancellation after creation. Before creation, no tool starts. Afterwards, the known exit code is retained, or the result is explicitly unconfirmed. A synthetic process seam exercises this distinction without ever running shutdown.exe. The shell retains a separate cleanup obligation for an unconfirmed submission, aborts it on Panic or Tray Exit with the exit cleanup token, and clears it only after cancellation is confirmed or Windows says nothing is scheduled. It never automatically aborts a confirmed preexisting schedule, and Windows session-end cleanup does not undo its own shutdown. An unconfirmed attempt cannot identify the OS-wide schedule's creator; cleanup may cancel whichever shutdown Windows holds, exactly as the existing explicit Cancel action does.

Evidence: `artifacts/sessions-tests-0.65-shutdownabort/shutdown-ownership-red.trx` records **3 expected failures, 2 passes** against the original behavior with only the synthetic process seam added. The failures are after-start cancellation, Exit losing an unconfirmed submission, and Panic losing a late unconfirmed submission. `shutdown-ownership-green.trx` passes **57 tests**, including preexisting schedules, pre-start cancellation, known exit codes, session end, stop/lifecycle and production-window contracts. `shutdown-ownership-final.trx` repeats those checks after the final truthful-status guard.

No real shutdown, sign-in, Roblox process, network change or paid AI test ran. The change adds no polling, package or unbounded worker: one existing bounded helper invocation and a small in-memory ownership flag. Reverting this repair restores the previous behavior. Real Windows timing and interaction remain an attended test; release/live readiness is unchanged.
