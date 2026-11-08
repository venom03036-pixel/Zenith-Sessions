# Round-4 integration evidence

This is incremental evidence, not the final twice-on-one-commit acceptance record. All accounts, credentials, network handlers and license files in these checks are synthetic. The paid cloud AI test is excluded.

## Merged suite checkpoints

- `artifacts/completion-integration/sessions-integration-second.trx`: 6,934 passed, no failures, one existing opt-in Windows fault-report test skipped after the account, password, signed-server and shutdown merges.
- `artifacts/completion-integration/all-checks-preflight/checks.json`: correctly failed because this new worktree lacked restore assets for five projects and the release tool. No stale test assemblies or packaged success were accepted. Its Sessions Release suite passed 6,963 cases, with the same one native crash skip.
- `artifacts/completion-integration/all-checks-restored/checks.json`: the local NuGet-cache restores, all six warning-as-error Release builds, the release-tool publish and Luau checks passed. Core 762, SDK 33, LicenseIssuer 11 and CapabilityHarness 40 passed. Sessions passed 6,963 with one native crash skip. Infrastructure passed 1,224, failed three license-view-model expectations, and skipped three test-defined cases. Package validation was refused after the failures. This is a failed gate and cannot count toward final acceptance.

## Public versus developer license fixtures

The three Infrastructure failures expected developer-only activation/text in a public Release build. Production correctly refused the administrator command. The test project now follows the App project's compile-time developer flag. The administrator command test retains all developer-edition entitlement assertions and, in Release, requires the fixed refusal message, Free state, no administrator authority, all paid features locked and no license file. The clear-license fixture explicitly seeds its own synthetic license so the clear operation is exercised in both profiles; it additionally verifies removal of the file and root authority. Invalid unsigned keys still cannot unlock any gated feature.

No production licensing code, build gate or entitlement was loosened. The original three failures are retained in the restored Infrastructure TRX. `licensing-release-after.trx` and `licensing-debug-after.trx` each pass all nine view-model cases, without skips. Checkpoint `0.62-releasefixtures` records the source review and result hashes. The full merged gate must run again after the remaining changes.
