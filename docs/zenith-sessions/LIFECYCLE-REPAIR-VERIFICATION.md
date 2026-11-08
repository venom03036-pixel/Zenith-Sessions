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

## Behavioral baseline replay — 0.60-d9lifecyclereplay

The original compile-only baseline above was insufficient evidence of the four reported defects. This follow-up replays the pre-`eb3670d` behavior using controlled scheduling and existing synthetic data; it does not count a compilation problem as a reproduced defect. Production source was restored in a `finally` block before the passing run.

`artifacts/sessions-tests-0.60-d9lifecyclereplay/lifecycle-before.trx`: **4 failed, 0 passed, 0 skipped**, after a successful build. SHA-256: `431ABEDC290334E73CBFE3B1BCDDBFD6A43ADF63E7634DEDCAB13783436B7E85`.

| Finding | Executed regression | Historical behavior and observed failure |
| --- | --- | --- |
| O-C11-1 | `AStartThatStallsInsideTheVaultIsReportedAtTheCoordinatorsShutdownNotAtTheVaultsRefusedDisposal` | The retained helper signature was used to replay unconditional using-style store disposal. The fake starter was held inside the real vault's dispatch lease. Disposing the vault raised `InvalidOperationException: Cancel and await pending vault operations before disposing the vault`, interrupting reporting instead of preserving the tagged coordinator timeout. |
| O-C11-2 | `ExpiredWaitKeepsItsCauseEvenWhenTheOwnerFinishesBeforeTheContinuationRuns` | The exact old `WaitAsync` / `catch (TimeoutException) when (!shutdown.IsCompleted)` implementation was restored, retaining only the TimeProvider injection. A held SynchronizationContext made the deadline expire, completed the owner, then resumed the filter. The raw timeout escaped with null InnerException instead of the original InvalidDataException. |
| O-C11-3 | `AStalledDiagnosticsWriterCannotHoldThePackagedReport` | The actual packaged diagnostics body was extracted unchanged into `VerifyDiagnosticsAsync` for injection. Replaying its former await-using plus direct DisposeAsync left it pending behind the held writer. The test's separate containment timeout had no ManagerDiagnostics owner tag, demonstrating that the stage itself did not return its bounded failure. The writer was released and drained in fixture cleanup. |
| O-C11-4 | `ReplacementWaitRejectsANewerPublicationThatHasNotReadTheAppendedBytes` | The actual test wait helper was given a controlled sequence: newer revision 2 had read only 50 of the required 100 bytes; revision 3 completed the boundary. Restoring its old revision-only predicate returned revision 2. This reproduces the faulty wait contract without claiming a new spontaneous loaded-machine failure. |

The first replay reproduced three cases; O-C11-2 passed because the continuation could run before the owner completed. The final replay adds a held continuation to enforce the reported ordering and produces all four failures. The retained final TRX replaces that exploratory run; its four named failures are the evidence above.

`lifecycle-after.trx`: **83 passed, 0 failed, 0 skipped**, covering all packaged self-check tests, the 51 synthetic session-log correlator tests, and the 12 optional-update owner tests. The genuine original real-file disconnect/replacement test also passes. New seams do not introduce production workers: packaged diagnostics has the same body/bound and one helper call; the extra snapshot getter and held continuation exist only in tests. The shell update admission now also rejects sample profiles; public-network startup ordering is an integration concern tracked by the root review.

Replay details remain local at `artifacts/Replay-LifecycleBefore.ps1`; fixed and historical source copies plus `historical-replay.diff` are in the same test artifact directory. They preserve what was temporarily changed, including the retained test seams. No real credentials, Roblox client, desktop input, installation or paid cloud test was involved. Final integrated self-check/build identity still belongs to the all-checks gate.
