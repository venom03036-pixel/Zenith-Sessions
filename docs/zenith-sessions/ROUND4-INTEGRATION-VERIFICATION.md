# Round-4 integration evidence

This is incremental evidence, not the final twice-on-one-commit acceptance record. All accounts, credentials, network handlers and license files in these checks are synthetic. The paid cloud AI test is excluded.

## Merged suite checkpoints

- `artifacts/completion-integration/sessions-integration-second.trx`: 6,934 passed, no failures, one existing opt-in Windows fault-report test skipped after the account, password, signed-server and shutdown merges.
- `artifacts/completion-integration/all-checks-preflight/checks.json`: correctly failed because this new worktree lacked restore assets for five projects and the release tool. No stale test assemblies or packaged success were accepted. Its Sessions Release suite passed 6,963 cases, with the same one native crash skip.
- `artifacts/completion-integration/all-checks-restored/checks.json`: the local NuGet-cache restores, all six warning-as-error Release builds, the release-tool publish and Luau checks passed. Core 762, SDK 33, LicenseIssuer 11 and CapabilityHarness 40 passed. Sessions passed 6,963 with one native crash skip. Infrastructure passed 1,224, failed three license-view-model expectations, and skipped three test-defined cases. Package validation was refused after the failures. This is a failed gate and cannot count toward final acceptance.

## Public versus developer license fixtures

The three Infrastructure failures expected developer-only activation/text in a public Release build. Production correctly refused the administrator command. The test project now follows the App project's compile-time developer flag. The administrator command test retains all developer-edition entitlement assertions and, in Release, requires the fixed refusal message, Free state, no administrator authority, all paid features locked and no license file. The clear-license fixture explicitly seeds its own synthetic license so the clear operation is exercised in both profiles; it additionally verifies removal of the file and root authority. Invalid unsigned keys still cannot unlock any gated feature.

No production licensing code, build gate or entitlement was loosened. The original three failures are retained in the restored Infrastructure TRX. `licensing-release-after.trx` and `licensing-debug-after.trx` each pass all nine view-model cases, without skips. Checkpoint `0.62-releasefixtures` records the source review and result hashes. The full merged gate must run again after the remaining changes.

## Integrated queue and preview fixtures

The `all-checks-integrated` gate on `ec48e0d` passed Core 762, Infrastructure 1,227 (three named existing skips), SDK 33, LicenseIssuer 11 and CapabilityHarness 40. Sessions passed 6,991, failed three and skipped the opt-in native fault-report case. All warning-as-error builds and Luau checks passed; package validation correctly refused to accept the failed suite.

Two failures used format 9 as an unknown future queue generation. Private-server intent now explicitly uses format 9, so the fixture now uses `int.MaxValue`; every refusal and byte-preservation assertion remains intact. The preview cleanup test started its five-second watchdog before the independently bounded eight-second overview exercise. It now waits for the fake provider's disposal-entry signal before starting the same five-second assertion. The 200-ms cleanup bound, exact owner classification, null inner cause and successful exercise assertions remain unchanged. No production implementation or check was relaxed.

`suite-fixtures-after.trx` passes all 74 affected Release cases without skips. Checkpoint `0.64-suitefixtures` records the original full-suite failures and focused result. These fixture corrections do not establish final commit acceptance.

## Portable-menu integration and retained export deadline proof

The merged `fa966d0` full Sessions run (`sessions-portable-integrated.trx`) passed 7,025 cases, failed one and skipped the opt-in native fault-report case. The production account-menu test still expected three entries after the two protected-backup actions were added. Its updated fixture asserts all five handlers, preserves every original entry/context assertion, and additionally proves backup permission can disable only the two protected operations while metadata actions remain enabled. No production UI was changed by this fixture correction.

O-C7-3's actual pre-fix `CheckAsync` helper from `42bf80d^` was temporarily restored while retaining the regression assertion. `export-deadline-before.trx` records the expected failure: the one-millisecond work budget expired during initialization, with Settings still Not loaded. The replay restores the exact current file bytes in `finally`. `backup-fixture-export-deadline-after.trx` passes 495 affected Release cases, including the restored helper regression, account workspace and portable-backup checks, without skips. This is a test-harness correctness repair, not a production export timeout change.

## Settings update controls

The production Settings page exposed none of the merged updater commands. `update-controls-before.trx` fails the missing-panel contract. A collapsed section now binds the six existing owner commands, feed/publisher configuration, daily opt-in and accessible status using the existing theme styles. Existing settings/recovery actions remain intact. `update-controls-after.trx` passes all 20 presentation, update-session and localization cases with no skips. This is offline wiring evidence; it does not establish production release signing, feed trust or attended installation.

## Update startup ordering

The merged updater could start a persisted daily check before the initial public-network guard locked the shell. `update-startup-before.trx` reproduces an update attempt already persisted when the synthetic network monitor starts. Initialization now applies the saved network guard before starting update scheduling; the updater owner checks current shell admission before a request. `update-startup-after.trx` passes 44 affected network, updater and launch-mode qualification checks with no skips. The test injects a synthetic backend and monitor into the real shell lifecycle and confirms no attempt or backend call occurs while locked, including an explicit Check now. No network/helper/installer runs in this regression.

## Controlled diagnostics shutdown clock

`all-checks-review2` on `6ec980e` passed five suites (Core 762, Infrastructure 1,227 with three named hardware skips, SDK 33, LicenseIssuer 11, CapabilityHarness 40), every warning-as-error Release build, tool publish and Luau. Sessions passed 7,070, failed one and skipped the existing opt-in crash case. The diagnostics regression received its own 500-ms watchdog timeout instead of the tagged owner timeout under concurrent load. The packaged gate correctly refused acceptance after that failure.

The diagnostic stage now accepts an optional clock solely through its existing internal verification seam; production still uses the same system clock and two-second shutdown bound. The test observes the requested 20-ms deadline, asserts the stage is pending with its writer held, explicitly expires that clock and requires the same ManagerDiagnostics owner tag. A five-second wall-clock limit is containment only, not the shutdown contract. All existing cleanup and owner assertions remain. `diagnostics-clock-after.trx` passes 17 affected checks without skips. Historical O-C11-3 red proof still establishes the missing bounded disposal defect; this correction removes a fixture scheduling race, not a product limit.

## Merged stop and helper ownership checks

`latest-repairs-merged.trx` passes 101 affected Release checks without reported skips after the settings/startup and initial upload-helper ownership merges. It includes the controlled diagnostics clock and ordinary PowerShell parsing/refusal controls; it does not run an elevated policy mutation or a Roblox client. Independent review then found a separate clock-adjustment counterexample in persisted helper recovery. The follow-up's failing-before evidence and conservative monotonic-uptime repair are recorded in [UPLOAD-LIMIT-OWNER-REVIEW-REPAIR.md](UPLOAD-LIMIT-OWNER-REVIEW-REPAIR.md). The 101-case result predates that follow-up and cannot substitute for the final full gates.

## Controlled report-retry fixture

`final-pass-1` on `388792b` is a failed gate, despite its folder name. Core 762, Infrastructure 1,227, SDK 33, LicenseIssuer 11 and CapabilityHarness 40 passed. Sessions passed 7,104, failed `ExplicitReportFileTests.ReplaceRetriesWhileAPriorReportIsBrieflyHeld`, and retained its one opt-in crash skip; Infrastructure retained three named hardware skips. Release builds, the release tool and Luau passed. Package acceptance was correctly refused after the test failure.

The old fixture scheduled release of a real held destination after 60 ms. Under concurrent load, its continuation did not release the file within the production ten-attempt, 25-ms retry sequence. Production still has those exact limits. An internal TimeProvider seam now lets the fixture wait for the first actual sharing failure, assert the exact 25-ms delay and preserved old bytes, release the file, then expire that retry explicitly. It asserts one retry and the final new contents/temporary-file cleanup. The cancellation control cancels during that same held retry and still requires OperationCanceledException and preservation of the old report. Every started test write is cancelled/joined in finally; five-second outer waits contain a stuck fixture only. `report-clock-final.trx` passes all 42 affected export/report checks without skips. This is a scheduling-fixture correction, not a longer production retry budget or a claimed new product defect fix.

## Native audio fixture baseline

`final-pass-2` on `7d292a4` is also a failed gate. Core 762, Infrastructure 1,227, SDK 33, LicenseIssuer 11 and CapabilityHarness 40 passed. Sessions passed 7,120, failed one native audio fixture and retained the opt-in crash skip; the three Infrastructure hardware skips remained. Release builds, tool publish and Luau passed; package acceptance was refused.

`LaterHelpersOfTheSameProgramAreUnmutedOnExitEvenWhenWindowsStartsOneMuted` failed on its first mute, before creating the later helpers. Windows started that first owned PowerShell helper with remembered mute state. The fixture assumed it was unmuted, unlike its sibling which explicitly establishes that baseline. Production correctly refused to claim a pre-existing mute. This failure is retained as fixture evidence, not a reason to relax audio ownership or claim the full gate passed.

The repaired fixture deterministically seeds that owned first helper muted, establishes and reads back its unmuted baseline before creating the production control, and leaves later helpers' inherited states intact. Every original ownership/restoration assertion remains; exit readback precedes fixture cleanup. Cleanup now attempts every owned helper and verifies native unmute acceptance. The original first-mute assertion failed before repair, and both affected runs passed all 20 cases. [Exact evidence and limits](AUDIO-FIXTURE-BASELINE-REPAIR.md). No production audio implementation changed.

## Host-pipe deadline ordering found during the minor follow-up

The four owner-requested minor repairs were merged at `dd1c298`. `final-pass-5` passed all six suites (9,210 passing cases and four existing opt-in skips), zero-warning Release checks and 43 packaged stages. Its attempted second pass, `final-pass-6`, failed `MacroHostPipeTests.ConnectedHostThatMissesTheDeadlineIsBusyNotDisconnected`: expected Busy, actual Disconnected, after 3.0686494 seconds. The Sessions TRX records 7,136 passed, one failed and the existing crash-test skip; the other five suites passed. Package acceptance was refused after the failure. These runs are not an accepted pair.

Source review established a production timer-ordering race. The client starts its three-second request timer before connecting, while the server starts an equal timer after accepting. The server can reach its deadline and close the pipe while the client's due cancellation callback is still waiting to run. Checking only that callback's token state misclassifies the connected request. Deadline classification must observe the client's monotonic elapsed budget, preserve earlier EOF/disposal/caller-cancellation distinctions and also reject a late valid response. A deterministic regression must withhold the timer callback while advancing monotonic time; neither retrying the full gate until lucky nor relaxing the original Busy assertion is acceptable. The replacement candidate requires independent review and two new complete gate runs.

The merged repair uses a monotonic timestamp and the existing timeout signal without extending the three-second duration. Controlled EOF at/after the budget and a valid at-budget response produced three failing-before assertions, while five earlier-close/valid-response/cancellation/disposal/malformed-data controls already passed. The original real-time test remains unchanged. All 69 pipe cases and 112 affected catalog/presentation cases passed after repair; [exact evidence and protocol limits](PIPE-DEADLINE-REPAIR.md). These focused passes do not count as either required full gate.

## Live-follow-up candidate and incomplete full pass

`final-pass-9` on `bf193748` is not an accepted gate. Its Sessions TRX has run-summary **Failed** after the WPF test host crashed with `Dispatcher processing has been suspended, but messages are still being processed`. The 3,413 recorded passing cases are only partial execution, not a full-suite pass. The native stderr also caused a PowerShell CLIXML error; the parent runner was cancelled after its children exited, so `checks.json` remains an incomplete running record. Preserve both the record and TRX; do not reinterpret the console's partial count as success. The exact crash trigger was not established and no fixture, assertion or skip was changed to conceal it.

Independent source review also found the queue producer-history race in that candidate. [The deterministic failing-before test and scheduling repair](LIVE-TRIAL-20261108.md#queue-ordering-found-by-independent-review-078-queue) precede the replacement's full gates. A new successful pair must execute all suites on that clean replacement commit; this interrupted run never counts toward the pair.

## Repeated WPF abort and fixture repair

`final-pass-10` on `295c049` failed with the same WPF host abort after 3,202 partial Sessions passes. The other five suites, warning-as-error builds, release-tool publish and Luau passed; packaged acceptance was refused. The separate full Sessions diagnostic run passed 7,170, failed two and retained one existing opt-in skip. It exposed the targeted-input fixture cancelling before cleanup and one five-second sign-in-race initialization timeout. Neither run counts as acceptance.

[TEST-HARNESS-LIFECYCLE-REPAIR.md](TEST-HARNESS-LIFECYCLE-REPAIR.md) records concrete dispatcher-context/retirement defects and the cleanup precondition repair. Five executed baseline failures precede 400 affected passes without skips. Every original assertion and the sign-in-race timeout remain. The exact intermittent crashing fixture was not proven; a new final pair must establish the integrated result. Gates 9 and 10 remain retained failed evidence.
