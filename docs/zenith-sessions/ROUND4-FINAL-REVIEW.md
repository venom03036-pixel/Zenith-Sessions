# Round-4 review coverage and completion evidence

Review baseline: `3cfa4c6`. The final subject is the immutable descendant commit identified by both successful full-gate records and the external patch-risk assessment. An exact-head review result is retained outside tracked source so recording it does not change the reviewed commit. This file records scope and known residuals, not a substitute for that result.

## Whole-change review scope

| Area | Required production trace and counterexamples |
|---|---|
| D1 launch and D5 game links | Account authority, ticket/official-page preparation, actual mode, held executable, one-use dispatch, per-executable qualification, same-player 401 handling and private destination isolation |
| D2 public/signed-in data and D3 sign-in | Exact identity and account revision, provider navigation/profile cleanup, media correlation, bounded parsers/requests, rotation, cancellation and private-data clearing |
| D4 vault/backup | Migration and password ownership, authenticated archives, consent/identity verification, fresh import identities, excluded authority and stopped dispatch |
| D6a process controls and D6b input policies | Process-incarnation ownership, CPU/priority/audio controls, external helper lifetime, target/input admission, keep-awake scheduling, stop precedence and cleanup |
| D7 presentation/localization and D8 Roblox maintenance | Existing UI/theme contracts, catalog and accessible controls, saved settings, initial network guard, update offer trust, stale generations and retained stops |
| D9 distribution/lifecycle and integration | Signed source/stage/apply provenance, installer rollback, prerequisite refusal, complete test/build/package inventory, exact source identity, packaged timeout/disposal and fixture corrections |

Independent reviewers must read the final source/diff for their assigned scope, not accept a repair author's green tests alone. The final combined result must cover every lane and integration delta. Findings need a concrete affected caller and counterexample. Source-visible critical/important defects require repair plus genuine failing-before and merged passing evidence; unavailable native acceptance is recorded as a trial, not invented as either success or a defect.

Important repairs and evidence are linked from [QUALITY_STATUS.md](QUALITY_STATUS.md). In particular, startup/settings activation cannot clear a newer Panic, initial public-network locking precedes scheduled update work, actual preparation mode/executable govern launch qualification, portable import drops authority, and upload-limit mutations retain the external helper's ownership after cancellation. A UTC clock correction cannot establish that an unresolved helper disappeared.

## Retained minor findings

1. **Cached password-mode dashboard state.** A shell that cached Disabled before another store instance enabled a master password can omit the password prompt on dashboard Unlock. Actual credential reads refresh on-disk control and fail closed. This is a presentation/control mismatch; no credential-access bypass was established. Location: `ShellVaultPasswordFeatures.cs` Unlock path and `ProtectedCredentialVault.Password.cs` current-control read. Retain as follow-up under the owner's minor-only stop rule.
2. **Network coalescing comment overstates its bound.** `WindowsPublicNetworkMonitor.ReadFolded` describes at most two reads, while its `_pending` loop drains until quiet. The comment should say that. A real continuous-notification starvation counterexample was not established; do not label this a proved important availability defect or claim a fixed two-read runtime bound.
3. **Bootstrapper publisher guidance is stale.** `ClientExecutableTrustResult.Summary` for `PublisherNotAccepted` still suggests accepting a publisher enables launching. Bootstrapper launching is deferred in this build; the provider/trial gates still refuse it. This is misleading guidance, not a launch-authority bypass.

## Evidence to inspect before handing off

- Two distinct full-gate directories, each with `checks.json`, six actual TRX results, all zero-warning Release builds, Luau checks, release-tool publish and 43 successful packaged stages. Both must bind the same clean commit/content digest throughout.
- The completion audit must reject duplicate runs, stale or dirty source, missing/empty steps, wrong lane ancestry, non-final item states and missing named regression passes. Narrative red-proof and independent-review semantics require review in addition to machine checks.
- The immutable patch-risk JSON/Markdown must bind the exact base, head and diff SHA-256, document platform/live gaps, and be schema-validated. Its workflow label is advisory; it is not a claim of production release readiness.
- Main must equal the checked commit. Historical failures and intermediate records remain failed evidence; they never count toward the successful pair.

No live Roblox account/client, paid cloud AI test, production installer or release publication forms part of this offline acceptance. The precise later attended work is in [ROUND4-LIVE-TRIALS.md](ROUND4-LIVE-TRIALS.md).
