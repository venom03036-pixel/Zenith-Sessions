# Packaged self-check lifecycle repair

2026-11-08, cycle `0.60-d9lifecycle`, D9 based on `3cfa4c6`. Covers O-C11-1 through O-C11-4; corresponding ledger scope is PROD-QA-03, PROD-QA-04, CAP-14-01 and PROD-QA-08.

The self-check records the stage failure before releasing shared stores. When an asynchronous owner exceeds its shutdown deadline, the original tagged timeout remains the result and shared stores remain owned by the short-lived self-check process until exit. This avoids disposing a repository semaphore while a worker still uses it. Other failures still attempt every store release; a release failure records only its type and does not replace the original failure. Reports add optional `ReleaseErrorType` and `SharedResourcesRetained` fields; the report version and success stage set remain compatible.

`SelfCheckOwners` decides which task finished first once, rather than testing shutdown completion in an exception filter after a timeout. A late owner fault is observed. Nested owners are asked to stop after an inner stall without replacing the first stalled owner's identity. An owner's own timeout remains its own failure. Diagnostics and layout disposal now go through this same bounded helper. The source guard covers explicit disposal awaits as well as `await using`; diagnostics is no longer incorrectly allowlisted.

The session-log replay regression waits for the appended replacement's completed-byte boundary, not merely any newer publication, and reads one immutable snapshot per observation. Its final identity/destination assertions remain intact. A repeated integrated suite must still confirm the historical loaded-machine flake is gone.

Rule 18: one short-lived timer per normal asynchronous shutdown and one fault-observation continuation only for abandoned owners; no new polling or capture/input workers. All records contain fixed stages and type names, never exception messages or credentials. No new dependency, installed application, real account, Roblox client or network request was introduced. Product shutdown semantics were not changed. Synchronous native calls and report I/O are not claimed to be cancellable; the staging script's independent process deadline remains final containment.

Executed in this worktree:

- Focused lifecycle, shared-store, failed-case, package coverage, summary and layout tests: **22 passed, 0 failed**, `artifacts/sessions-tests-0.60-d9lifecycle/d9-lifecycle.trx`.
- Synthetic session-log correlator tests: **50 passed, 0 failed**, `artifacts/sessions-tests-0.60-d9lifecycle/d9-logcorrelator.trx`.
- `scripts/Check-ZenithSessionsManager.ps1 -SkipTests -StageOnly -StageFolder sessions-manager-review`: **43/43**, no warning/error emitted. It staged an isolated development build and did not promote/install it. Log: `artifacts-d9-package.log`; exact hash report: `artifacts/sessions-distribution-check/distribution-check.json`.
- `git diff --check`: clean.

The first restricted-token attempts could not run .NET. The initial elevated compile found the retained incomplete test's old helper signature after the implementation changed; its three compile errors were corrected before the passing runs. This is not represented as a baseline failing behavioral test. The paid cloud test was never included in these focused filters.

Full integrated suites, live/native acceptance, CI, installer and signed-update completion remain separate work. Release readiness remains false.
