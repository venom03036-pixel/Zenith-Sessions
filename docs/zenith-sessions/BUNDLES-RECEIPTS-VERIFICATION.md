# Account bundles and launch process receipts — 0.18

The Personal development preview passed **1,488/1,488 controlled tests**, with zero failed/skipped tests and zero forced-build warnings/errors. The exact staged binaries passed **22/22 non-interactive package checks twice** against the same isolated fixture. This is an uninstalled development increment, **not a completed native launcher or release**. No real account, Roblox client, reference executable, desktop UI or Sandbox was exercised.

## Implemented behavior

- Named account bundles store ordered saved-profile GUID references. Create from checked accounts, extend from another page, rename, review, explicitly replace/remove recipients and delete the bundle without deleting accounts. Current-page checkboxes remain page-scoped; changing pages does not redirect a saved bundle.
- Review shows last-known verified username/player ID where available. Missing profiles remain explicit references; they block the entire bundle request until an explicit edit resolves them. Metadata is not current execution authority.
- Queue bundle freezes its destination and recipients before asynchronous lookups. Existing admission, authorization-generation and native-provider checks remain in force. Capacity advice uses the frozen target, so a new browser destination cannot lend it another server's open-slot count.
- Stop, Lock, Panic and shutdown cancel preparation and join its actual owner. Pending requests created by that operation are cancelled; an already-started or uncertain client remains reviewable. Late callbacks cannot repopulate a locked dashboard or declare arrival.
- Queue format 4 adds a nonsecret receipt: PID, process creation UTC ticks and coordinator recording time. **Client started · unverified** retains its reservation and requires review. This is provider-reported process-instance evidence, not fresh liveness, correct-account proof or arrival.
- Late receipts after cancellation/timeout attach only to their original intent and cannot undo Panic or release reservations. Rejection with a process claim remains uncertain. Invalid creation times cannot enter arrival confirmation even when a provider claims matching account/place/server values.

The in-app Guide, bundle controls, member review and state-aware queue controls are integrated. XAML compilation and dispatcher/view-model tests passed; production rendering, accessibility, interaction and DPI acceptance remain unverified.

## Bounds and compatibility

Recipient storage has one durable writer, bounded cross-instance locking, four admitted operations per store, 512-KiB files, 100 bundles, 1,000 references per bundle and 10,000 total references. These operational metadata bounds add no saved-account product limit. Strict validation, revision/change IDs, atomic writes, last-valid backup and preserved damaged/future-version originals prevent silent reset or stale overwrite.

Review reads requested profiles and nonsecret summaries in batches of at most 200, without decrypting a credential inventory. Presentation owns one active operation; disposal joins it. Existing queue owners are reused. No dependency, persistent worker, per-account timer or input/capture path was added.

Queue readers retain formats 1–3 as paused review data; saves write format 4. Unknown future formats cannot silently select an older backup or be overwritten. Existing enum values and macro/vault/session schemas remain unchanged. Nothing was installed or migrated in real user folders. Older development binaries remain available; do not downgrade a live queue without reconciling potentially started clients.

## Executed evidence

The [verification record](../../artifacts/sessions-manager-verification-0.18.json) pins the build. Regression finished **2026-11-06 01:05:28.7080351 UTC**. Package passes finished **01:05:45.7246959 UTC** and **01:06:41.901918 UTC**, using .NET 10.0.12 and SQLite 3.53.4. All **25 manifest entries** matched sizes/hashes: zero extra files and zero private-material filename-pattern matches. A filename audit does not prove complete secret absence or malware safety.

- App SHA-256: `C9DB7C07D29393989C49867BB8336A0897FC75F796457C567F028B6B470F7575`.
- Windows SHA-256: `8816E74A70FB574AF7322EC5FEA04BA821C702120A583600FC82CE503B21247E`.
- Core SHA-256: `A45CE7FC69718840CF6CA99F432A0924903B0F6D028D7C84A15EA27CC7C55C9F`.
- Manifest SHA-256: `A047C4802EF1D1A0AA329A86B14A2BF9765B84A297F5F63A45A868C994031C24`.

[Full regression](../../artifacts/sessions-manager-tests/manager-core.trx), [first package check](../../artifacts/sessions-distribution-check-bundles-receipts-first.json), [repeat](../../artifacts/sessions-distribution-check/distribution-check.json), [manifest](../../artifacts/sessions-manager-review/preview-manifest.json).

The retained [two-case before-fix run](../../artifacts/sessions-manager-tests/invalid-process-arrival-before.trx) reproduced impossible process timestamps reaching Arrived. Requiring a valid receipt before stabilization corrects that defect. Nine older scheduler fixtures then failed because their fabricated timestamp was `1`; they now supply valid UTC ticks without weakening runtime checks. That [intermediate run](../../artifacts/sessions-manager-tests/process-time-fixture-before.trx) remains recorded. An earlier bundle fixture selected an obsolete row after refresh; selecting the current row by stable bundle ID preserves the runtime stale-selection guard. Final full regression passes.

## Research and remaining requirements

The supplied RoALT checkout remains read-only. The [653-file catalog and bounded review](ROALT-SOURCE-COVERAGE-SUPPLEMENT.md) cover accounts, vault, launcher, tracking, resources, UI and distribution with explicit limits. Inventory is not exhaustive semantic/runtime review. No reference program, source script, build or dependency ran; no real vault/browser data was read or implementation copied.

[Official-page handoff research](NATIVE-WEB-HANDOFF-RESEARCH.md) identifies an always-cancelled owned-browser capture adapter as the next native-launch investigation. It does not prove native account selection, process association or simultaneous clients. Existing sign-in navigation guards remain intact; the native provider stays unavailable.

Native account selection/arrival/reconnect, independent background input, live broadcast, owned-game bridge, host integration, complete custom appearance/mini mode, production UI performance and 2/4/8/16-client acceptance remain unmet. No comparative efficiency, sustainable capacity, complete RoALT parity or release readiness is claimed. Public, Linux, installed Personal and actual Roblox sessions remain unchanged.
