# Process control review repairs

Scope: round-4 D6a findings, checkpoint `0.60-d6process`, requirements CAP-11-03, CAP-13-04, PROD-UI-03 and PROD-QA-03. All new evidence uses fake process adapters and isolated temporary records. No real Roblox client, account or administrator prompt was used.

## CPU ownership and leftover records

The cap ledger now stores its Windows boot estimate. A different boot discards old records before opening any PID. Legacy schema-1 files remain readable; records whose process start predates this boot are ignored. The two-minute tolerance matches the existing upload-limit record; large system-clock changes can invalidate the estimate, so this is conservative bookkeeping, not an operating-system boot identifier.

Leftover checks open with QUERY_LIMITED_INFORMATION and inspect only creation time and liveness. They do not request priority mutation rights or inspect mutable priority/job state. A denied current-boot read stays uncertain; a reused PID or exited instance is forgotten. Microsoft documents the required rights for [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes) and [GetExitCodeProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getexitcodeprocess).

When Zenith owns a priority/efficiency change but never set a CPU cap, a foreign job joining later no longer makes the unrelated restore conflict. Actual Zenith cap ownership still requires an exact cap readback. The change neither removes nor modifies the foreign job. CPU-cap wording now says frame rate *may* fall when CPU time is limiting; no performance benefit is claimed as measured.

Evidence in `artifacts/sessions-tests-0.60-d6process/`:

- `d6-cpu-before.trx`: two expected failures with the original ownership comparison restored temporarily; the new regression tests and test seams remained. Both manual restore and exit cleanup failed. The repaired source was restored in `finally`.
- `d6-cpu.trx`: 79 passing CPU/control/presentation checks.
- `d6-cpu-close.trx`: 107 passing CPU and close checks after restoring the repair.

Review covered ownership retention on failed writes, exact PID/start comparisons, cap conflict preservation, query-only access, temporary-file compatibility, cancellation and disposal. The patch adds no packages or polling loop. Native CPU/FPS behavior remains an attended trial; synthetic results do not establish live Roblox compatibility.

## Close access failures

Opening a close handle now returns a typed outcome: opened, no longer the same instance, access denied, or identity/state unconfirmed. Only an actual gone/changed instance is described as exited/not current. Denied or unknown opens send no close or termination, stamp no end time and never offer Force close. Creation-time and image-query failures remain unconfirmed rather than being treated as proof of exit. The checked process handle remains owned until the operation ends.

`d6-close-before.trx` records two expected failures after temporarily restoring the original error-collapsing classification with the typed seam retained: both access denied and an unknown handle error incorrectly became NotCurrent. `d6-upload-first.trx` then passed 83 close/upload checks after restoring the repaired classification; `d6-cpu-close.trx` passed 107 CPU/close checks. Native client closing remains an attended test.

## Late administrator-prompt answers

The runner retains an operation-specific completion when cancellation stops waiting for ShellExecute. A later Declined/NotStarted result releases its resources and reports that nothing ran. The controller restores the exact prior record (including its original timestamp) only while this is still the latest operation. An approved helper remains uncertain: closing a handle does not stop a helper already approved. A response that arrives before the cancellation result is also handled, so ordering does not leave false uncertainty.

A monotonic operation revision fences queued UI updates. The record text and final status refresh after a late answer, while an older callback cannot replace a newer operation. Shutdown unsubscribes the view; late no-start can still repair its owned file without publishing into the closed view. The existing two-second release bound is retained. An unreadable prior record remains explicitly unknown; it never becomes a false “no limit” claim. Save failures are surfaced in the result and diagnostic event.

Evidence: `d6-upload-before.trx` has nine expected failures against the unfixed controller with the callback seam added; every tested late decline/failed start retained false uncertainty. `d6-final.trx` passes **185** focused checks, including runner ownership/fault observation, both callback orderings, persisted restore, no overwrite of a newer operation, no UI publication after dispose, unavailable prior record, queued presentation revision checks, and all affected priority/close regressions. Tests inject ShellExecute completion and process handles; no administrator prompt, QoS policy command, real client, or paid test ran. The Release installer/signature path is not exercised by this patch.

Rule 18 review: work is one callback per abandoned operation; no polling or new timer/package; file writes retain the existing atomic replacement; prior-state snapshots contain only policy metadata; all deferred start work stays under the runner's release owner. Cancellation does not undo an already approved external operation. Rollback is reverting the three repair commits plus presentation follow-up; existing schema-1 cap files remain readable.

## Integrated process controls (0.62-processmerge)

The D6a lane is integrated with the executable qualification, account observations, localization, GPU/keep-awake, updates, EFS and lifecycle lanes. Its close/upload cancellation and disposal paths coexist with the other owners. The shared injected clock remains one instance, used for idle lock, arrival mute and scheduled shutdown. The new Upload limit Guide heading uses the English catalog; two legacy Guide assertions now resolve that same catalog before checking their unchanged expected wording.

The first integrated run passed 437 and failed five resource-reading assertions (`artifacts/completion-integration/process-merge.trx`); the corrected test readers retain all original behavioral and wording checks. The accepted run passed **442, failed 0, skipped 0** (`process-merge-accepted.trx`). This is merged offline evidence, not native process/UAC acceptance or the final all-suite gate.

## Integrated suite corrections (0.62-integrationsuite)

The first full merged Sessions run recorded 6,854 passes, 12 failures and one existing opt-in Windows crash-test skip (`artifacts/completion-integration/sessions-integration-first.trx`). News now receives Ctrl+9 through the same shortcut table as the sidebar and Guide. Sidebar tests keep every previous label and add News; one joining-surface test resolves the production English accessibility catalog before checking the same Sessions control.

The share-link integration test now covers both outcomes required by M-L1a-6: a route 401 followed by an identity refusal must still mark only that account SignInRequired; a valid same-player confirmation must retain Linked and report the route unavailable. Both assert exactly one synthetic identity call and no leaked credentials. The former test encoded the explicitly superseded rule that the route alone invalidates the account.

The native audio fixture establishes an unmuted baseline on its own synthetic PowerShell helper because Windows can remember a prior process's mute. The original baseline, mute, invalid-identity, restore and exit assertions remain. Exit restoration is now captured **before** emergency fixture cleanup, so cleanup cannot hide a failed restore. The affected integrated checks passed **67/67**, no skips (`integration-repairs.trx`). No real Roblox client/account or paid AI test was used.

## Shutdown cancellation failures (0.62-shutdownfaults)

A throwing cancellation callback could abort BeginShutdown before the remaining service owners received cancellation. Each shutdown step is now isolated, diagnostics record only the exception type, and later owners still cancel and dispose. The process-control owners also isolate their initial stop request before awaiting their own drain/dispose path. Upload-limit cancellation is explicitly included in the first shutdown pass.

Two deterministic regressions inject a throwing lifetime cancellation callback or memory-trim cancellation owner. Both failed against the preceding implementation (`shutdown-faults-before.trx`); the accepted merged run passes **278/278**, no skips (`artifacts/completion-integration/shutdown-faults-accepted.trx`). It checks that query and authorization work still cancels, remaining owners are disposed, and secret-bearing exception messages never enter diagnostics. This does not establish native client shutdown timing; that remains an attended trial.
