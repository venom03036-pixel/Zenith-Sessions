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
