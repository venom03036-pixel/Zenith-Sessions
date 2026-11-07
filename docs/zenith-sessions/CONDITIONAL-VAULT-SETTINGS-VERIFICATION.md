# Conditional saved-session updates and Settings search — 0.20

Personal **0.20.0-preview** passed **1,704/1,704 tests**, zero failed/skipped, with zero forced-build warnings/errors. The 38 conditional-vault tests passed **two additional runs**; the final binaries passed **24/24 packaged checks twice**. All 25 manifest entries matched their sizes and hashes, with no extra files or private/test filename-pattern matches. This remains an uninstalled development preview, **ReleaseReady:false**.

## Saved-session ownership

The official-page inspection now uses `IConditionalAuthorizationVault` for both its pre-browser SignInRequired transition and its final same-player replacement. One private per-vault file lease excludes writers and readers in other Zenith processes while the store compares the entire expected protected record and authoritative generation, reserves its exact successor, and commits. Conflict changes no credential, claim or generation data. No network or browser operation runs under that lease.

Each attempt has a fresh random mutation ID. A failed or cancelled final write fences only that ID's latest durable reservation, including an interrupted write whose caller received no commit receipt. A later allocation, deletion or replacement clears ownership, making obsolete cleanup a no-op. Fencing preserves encrypted payload and claims; reads expose SignInRequired with empty reusable fields rather than returning retained secrets. It does not manufacture a credential at the fence's newer generation.

The store bounds admitted calls to four and lock acquisition to approximately two seconds, with cancellable 20-ms contention delays. DPAPI, ACL and local synchronous file work is moved off the caller's UI context; the Windows calls themselves have no hard cancellation deadline. The persistent zero-byte `.writer.lock` is coordination metadata and is deliberately not deleted while other instances might use it. Callers must cancel and join their work before disposal.

Generation metadata reads strict version 1 or 2 without rewriting on load; mutations write version 2 with ownership. Encrypted credential envelopes remain version 2. **Close older Zenith writers before the first version-2 generation mutation.** Older readers reject the new format, but an already-in-flight older writer cannot honor a lease introduced after it started. Existing installed editions were not updated. Ordinary Link/Check still use their earlier read/allocate/save sequence; their full cross-process intent race and a future native final-dispatch authority boundary remain separate unresolved work.

The nonsecret account-projection journal can remain pending after a conflict. Completion rechecks identity, generation and Linked health; it cannot turn the conflicting record into usable authority. No previous cookie is restored after uncertainty.

## Settings navigation

Settings adds bounded local search over 32 authored entries for real controls or explicit pending-capability explanations. It accepts at most 160 characters/eight folded words and returns at most ten immutable metadata results. Invalid Unicode/control input yields no results; no query is logged, persisted, sent over the network or used as an operation argument.

Ctrl+F applies only while unlocked Settings is open. Opening a result scrolls/focuses the existing control, preserves invalid drafts, and never invokes Save, refresh, sign-in or a provider command. Custom timing does not select Custom automatically. Specific timing/memory results reach their corresponding fields; performance-transfer results reveal the existing collapsed transfer section without applying anything. Recovery navigation also works after library startup failure. Lock clears search and shutdown removes its subscriptions. Failed focus is reported instead of claiming successful keyboard activation.

The UI review corrected three integration gaps before the final rebuild: readiness wrongly blocking recovery navigation, a nonfocusable installation heading, and transfer results leaving their controls collapsed. These have source/view-model/production-XAML coverage. Real desktop interaction, rendered alignment/DPI, accessibility and latency remain unexecuted.

## Executed checks and corrections

The full run finished **2026-11-06 02:14:36.7140222 UTC**. Packaged passes finished **02:14:57.0676567 UTC** and **02:15:43.7423885 UTC**, using .NET **10.0.12** and SQLite **3.53.4**.

Four tests actually started the repository's test-only helper in separate Windows processes against newly marked synthetic Temp vaults. They verified held-lease exclusion/cancellation/busy reporting, one winner among competing exact-record updates, preservation of a later foreign grant, and owner-only invalidation. The helper accepts no credential or arbitrary profile, has bounded operation deadlines and joins only its owned process. It references no App project and is absent from the distributed files. This is real interprocess file/DPAPI evidence, not a Roblox or desktop input test.

Thirty-four focused store cases cover partial reservation/payload interruption, claims, version-1 reads/version-2 strictness, overflow, disposal, bounded contention and cancellation. Inspection regressions additionally cover a foreign replacement after the final pending read and loss of a postcommit receipt. Search tests cover ranking/bounds/immutability, unchanged drafts, Lock/page gating, corrupt-database recovery navigation and production anchors.

The initial build missed a test-only System.IO import. Subsequent failing fixtures exposed persistent-lock cleanup expectations, a sharing-denied observation during atomic replacement, an overly narrow search-result assertion, and a readiness signal published while its writer was still open. The tests now distinguish coordination metadata, retry only transient observation sharing errors, select the exact authored entry, and publish helper signals atomically after close. Failed TRX evidence is retained as `manager-0.20-before-fixture-fixes.trx` and `manager-0.20-before-signal-fix.trx`. Production assertions were not relaxed to call a conflict or incomplete write a success.

- App SHA-256: `16294700285031BA4E14E5618ADB464DC22453693476A313F66CCB1EDB583BC8`.
- Windows SHA-256: `DDDDD6ED720D00F4DA17BC8A70315A8351D6D38E6C4934EC77C86ECAF7948972`.
- Core SHA-256: `D89B8936D5FD2FB74C57CF475DE0AB0CCA139E5C32D9C2DB5924A598D01F2F57`.
- Manifest SHA-256: `5E6B44BC54989488E1FA00FEC212B3A0D7E44A155ADF75751644F40E77FD17D1`.

[Verification record](../../artifacts/sessions-manager-verification-0.20.json), [full tests](../../artifacts/sessions-manager-tests/manager-core.trx), [repeat 1](../../artifacts/sessions-manager-tests/vault-cas-repeat-1.trx), [repeat 2](../../artifacts/sessions-manager-tests/vault-cas-repeat-2.trx), [first package check](../../artifacts/sessions-distribution-check-conditional-first.json), [repeat package check](../../artifacts/sessions-distribution-check/distribution-check.json), [manifest](../../artifacts/sessions-manager-review/preview-manifest.json).

Rule 18: no new production worker, timer or dependency; search and vault queues are bounded, owned, cancellable and independently testable. This is the incremental migration required by Rule 20, preserving macro, settings, queue and encrypted credential formats. Keep current protected data for explicit recovery rather than downgrading generation metadata or restoring obsolete secrets. A filename audit is not a comprehensive secret/malware scan.

## Remaining work and live-test readiness

No RoALT source script, binary, installer or dependency was executed. No actual account/browser data was read, no Roblox client was launched or controlled, no GUI was exercised, and no installed Personal/Public/Linux edition changed. The earlier [source review](ROALT-LOCAL-SOURCE-REVIEW.md) remains static evidence, not a native-provider pass.

Correct-account native launch/arrival/reconnect, isolated background macros, broadcasting, host integration, the owned-game bridge, complete custom appearance/mini mode and real 2/4/8/16-client acceptance remain unfinished. No parity or performance-superiority claim is made. Continue implementation and controlled checks; notify the owner when an integrated build is ready for the requested two-account Coloseum live test. That later test must establish behavior that automated fixtures cannot prove; it is not permission to label the present preview complete.
