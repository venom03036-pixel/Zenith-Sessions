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
