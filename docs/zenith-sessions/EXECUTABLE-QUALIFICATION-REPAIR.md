# Executable-bound native-launch qualification

Checkpoint: `0.51-d1-executable`, 2026-11-08. Source baseline: D1 `4d0d91a1e36e9912222dcb74ad81d55c4f263ef3` plus this repair. Scope: CAP-02-03, VA-06-03, PROD-CORE-03, PROD-BOOT-02 and the owner's standard-player-first / retrial-on-executable-change decisions.

## Behavior and migration

New `NativeLaunchVerification` records carry an optional `Executable` object: SHA-256 of the held file bytes, an opaque digest of its normalized path, and an opaque digest of its exact Windows file identity (volume, file ID, size and creation/write times). The identity originates in the existing Authenticode preparation that held the executable used by the successful explicit trial. Recording does not hash a replacement file and attach that new file to an older trial.

Schema 1 settings remain readable. Older mode/product-only records are retained for history/removal but never authorize ordinary launches. A new explicit standard-player trial replaces that mode/product's old record. Missing, unreadable, redirected or replaced files fail closed. A same-path replacement needs a trial even if its bytes are identical. The selected launch mode remains part of the acceptance key.

Capability projection performs bounded local file-identity reads rather than rehashing a large player in UI property getters. Every ordinary preparation compares its newly verified held identity and digest with the saved trial. Dispatch checks that the same acceptance still exists, and the native starter rehashes its held file before invoking the process. A byte change that preserves metadata can therefore leave the display provisional until preparation detects it, but cannot obtain an ordinary start from the stale digest. No persistent executable-trust cache is introduced.

The queue marks preparation contexts that require qualification; explicit trial contexts keep their existing one-operation consent and stop fences. Trial identity history shares the diagnostic owner's existing bounded retention and contains no secret or local path. Existing exact process receipt, expected player, requested place, authorization-generation, publisher, cancellation and cleanup checks remain required.

Bootstrapper publisher inspection and persisted signer choices remain available. The shell offers only standard-player trials in this build, and bootstrapper records cannot enable ordinary launching. A future bootstrapper implementation must prove its exact child-player association before those controls are enabled. No filename/PID heuristic replaces the current identity checks.

Suggested native preselection now states when it pauses a running queue and tells the user how to resume. XAML changes are six existing text/tooltip lines; layout and themes are preserved. The ledger's original CRLF line endings are restored, reducing the whole D1 ledger difference against main to the actual requirement changes.

## Executed evidence

Final focused command used `dotnet test ... --no-restore -m:2 -nr:false`, the affected launch, installation and `ClientWindowIntegrationTests` classes, and the paid-cloud-test exclusion. Result: **458 passed, 0 failed, 0 skipped** in `artifacts/sessions-tests-0.51-d1-executable/qualification-accepted.trx`.

Earlier retained runs: 32/32 initial; 117/117 focused; 193 pass / one new fixture failure; 455 pass / one obsolete wording failure. The mapping fixture initially expected a held file, but Windows already rejects read leasing while a writable mapping exists. Its corrected assertion verifies this earlier access-denied refusal and zero starts. The wording failure was repaired without changing the queue-pause assertion. The final run includes both regressions and the legacy-settings cases.

New coverage includes legacy records loading without enabling launch; malformed identities; no record without the trial identity; same-path identical-byte replacement; fresh prepared digest mismatch including metadata-preserving changes; withdrawal/mode change/file replacement between preparation and dispatch; retaining only the trial's held identity; bootstrapper exclusion; actual owned-file identity reads; and the visible suggested-selection pause reason. Existing held-file and shell integration tests remain in the final set.

`git -c core.whitespace=cr-at-eol diff --check` passed. No real Roblox account, executable launch, network endpoint, installed application, or reference app was used. The signature-positive fixture injects a policy over harmless owned files; it is not real Roblox publisher evidence.

## Rule 18 and remaining gates

CPU and allocation work is bounded to explicit preparation and small identity projections; no timer/thread/capture/input pipeline is added. Full-file hashing remains bounded by the existing 512 MiB verifier cap and cancellation token. The existing owner controls disposal and joins actual preparation/dispatch. Exceptions return fixed failure states without paths, tickets or credentials. Persistence remains backward-readable; returning to an older build would restore its weaker mode/product interpretation, so do not downgrade a qualified installation to that build.

The integrated full suites, Release build and package self-check remain the root integration task. Native account/place arrival, real update replacement and one-/two-account launching still require attended trials. Bootstrapper child attribution is deferred. This repair does not declare live-test or release readiness.
