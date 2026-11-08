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

## Completed minor follow-ups

The owner explicitly requested these four repairs after the previous minor-only stop. The original accepted build and its review remain historical evidence; the follow-up candidate requires its own two complete gate runs and final review binding.

1. **Current password-mode dashboard admission.** Unlock now reads protected control through the vault's writer lease before deciding whether to prompt. The operation is owned, cancelled and joined; captured stop generations reject Lock/Panic/cancellation during command admission, the read and result publication. Credential reads retain their independent fail-closed checks. See [unlock repair evidence](VAULT-UNLOCK-MODE-REPAIR.md).
2. **Accurate network coalescing comments.** Both `WindowsPublicNetworkMonitor` comments now describe coalescing notifications during each read and repeating until none is pending. The executable loop is unchanged. Independent source review, rather than an invented behavioral test, verifies this documentation-only correction; no fixed two-read runtime bound is claimed.
3. **Deferred bootstrapper guidance.** Accepted, unaccepted and changed-publisher summaries now state that acceptance does not enable launching and direct the user to the standard Roblox player. Existing provider/trial gates are unchanged. See [guidance repair evidence](GUIDANCE-MINOR-REPAIRS.md).
4. **Conditional leftover CPU-cap guidance.** Startup notices distinguish confirmed job membership from an unreadable rate, denied process queries and an unreadable ledger. They describe possible caps and conditional recovery without asserting a crash or a successful cap mutation. Native control and recovery ownership are unchanged. See [guidance repair evidence](GUIDANCE-MINOR-REPAIRS.md).

The immutable follow-up review must establish whether any new finding remains. These repairs do not change the 93 item dispositions or qualify any attended trial.

## Deadline regression exposed by the repeat gate

The attempted second pass on the four-minor candidate found a connected macro-host request reported Disconnected after its deadline. The host/client timer callbacks can run in the opposite order to their due times. [PIPE-DEADLINE-REPAIR.md](PIPE-DEADLINE-REPAIR.md) records the production repair and deterministic failing-before checks for delayed cancellation callbacks and late valid replies. The three-second budget remains unchanged; earlier EOF, local disposal, caller cancellation and malformed-response outcomes remain distinct. Busy describes a missed connected-request budget, not proof that a remote host still lives. The failed pass remains in [integration history](ROUND4-INTEGRATION-VERIFICATION.md); the replacement commit needs fresh independent review and a new successful pair.

## Evidence to inspect before handing off

- Two distinct full-gate directories, each with `checks.json`, six actual TRX results, all zero-warning Release builds, Luau checks, release-tool publish and 43 successful packaged stages. Both must bind the same clean commit/content digest throughout.
- The completion audit must reject duplicate runs, stale or dirty source, missing/empty steps, wrong lane ancestry, non-final item states and missing named regression passes. Narrative red-proof and independent-review semantics require review in addition to machine checks.
- The immutable patch-risk JSON/Markdown must bind the exact base, head and diff SHA-256, document platform/live gaps, and be schema-validated. Its workflow label is advisory; it is not a claim of production release readiness.
- Main must equal the checked commit. Historical failures and intermediate records remain failed evidence; they never count toward the successful pair.

No live Roblox account/client, paid cloud AI test, production installer or release publication forms part of this offline acceptance. The precise later attended work is in [ROUND4-LIVE-TRIALS.md](ROUND4-LIVE-TRIALS.md).
