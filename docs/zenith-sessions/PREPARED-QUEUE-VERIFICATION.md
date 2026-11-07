# Preparation-aware queue and native provider composition — 0.25

Personal **0.25.0-preview** passed **2,222/2,222 source-build tests** and the full suite against the **exact staged first-party assemblies**. The149 queue/provider/store cases passed twice more. Packaged checks passed **29/29 twice**, with25 matching manifest files, zero extras and zero private/test filename-pattern matches. Final forced builds reported zero warnings/errors. Nothing was installed; **ReleaseReady:false**.

## Implemented queue integration

The existing `ILaunchProvider` path is retained. Optional `IPreparingLaunchProvider` returns a queue-context-owned disposable handle before any native dispatch. `Preparing` is appended to the state enum and excluded from `NeedsReservation`. A fresh resource preflight can refuse opening the browser, but it allocates no capacity. While preparation or final validation awaits user/browser/storage work, the state remains Preparing and no client reservation is claimed.

The queue snapshots a handle's final intent once and accepts only its original context object. Operation/profile/player/place/job/kind, priority, request time and explicit-destination flag must remain identical. Only profile revision0/+1 and authorization generation0/+2 are accepted; the native provider requires the specific +2 pending/final transaction. The final selected profile/authorization is revalidated before admission. Adoption changes both stored and local intent so subsequent updates and late process receipts still match the exact owned request.

Under the existing serial queue owner, cancellation/generation, expiry and fresh admission are checked again before durable Launching state reserves capacity. A disposable dispatch boundary supplies exact-intent validation, current stop/generation checks and cached fresh-resource admission to the actual native grant boundary. A denied or expired preparation is disposed rather than queued indefinitely or refreshed into another grant. The cheap preflight prevents repeatedly reopening pages while resource pressure persists.

One actual preparation task remains owned after its observation deadline. Late handles are disposed without adoption or dispatch. Final validation and native dispatch retain their actual tasks too; a late native receipt is persisted against the adopted intent without erasing uncertainty, releasing its reservation or claiming arrival. New failures use fixed messages. Per-profile deduplication, queue limits, priorities, aging, performance policy snapshots and the legacy provider path remain.

Preparing-aware shutdown joins the actual runner instead of caching a failed five-second disposal. Two gated regression cases hold preparation/cleanup beyond5.3 seconds and verify the same disposal task remains pending until release. This is controlled ownership evidence, not a native browser cancellation-time guarantee. An unfinished native API cannot be promised forced cancellation.

## Native provider composition

`WindowsNativeAccountLaunchProvider` composes the held executable verifier, selected-account owned-page preparation and existing exact-grant coordinator. Installation configuration is pinned and copied, not hardwired to the user's current version directory. The held native executable is prepared before a short-lived candidate is captured. The native handle checks original intent, pending/final generations, mutation owner, exact protected snapshot and destination before dispatch.

The provider owns its coordinator, lifetime and prepared handles; injected verifier/preparer/browser/vault remain caller-owned. Every failure clears owned candidate/file handles. Dispatch uses the exact queue boundary, current grant and unmodified opaque handoff; direct legacy LaunchAsync rejects, and ConfirmArrivalAsync returns false. It can report only unverified process-start receipts or uncertainty. No process guessing, focus manipulation, termination, singleton takeover, client modification or credential harvesting was introduced.

Unconfirmed cleanup permanently blocks reuse of that provider instance. The queue likewise pauses and becomes unhealthy when an actual late preparation or handle disposal reports cleanup failure, even if its first observation was cancellation. Pre-start cleanup failures hold no fictional client reservation; possible native starts retain their uncertainty/reservation.

**Production remains disabled.** The new provider advertises **Unverified**, and the App still constructs **UnavailableLaunchProvider**. No product UI command has been switched to the new native composition. No actual WebView, Roblox account, native process start, game arrival, reconnect or background input was tested.

## Persistence and presentation

Queue writer format is now **5**, with read compatibility for1–4 and existing pre4 receipt/migration rules. Version5 stores Preparing exactly; coordinator startup forces pause and converts it to Blocked/PreparationInterrupted. That state cannot replay automatically or demand confirmation that a client was closed. Its row reads “Review preparation,” exposes Cancel, and excludes client-closure review. Active preparation reads “Preparing account” and can be cancelled.

Older envelopes containing preparation state/problems fail closed before unknown fields or duplicate properties can cause an unsafe older-backup fallback. Unknown future versions remain non-overwritable. Normal corruption recovery and verified backup behavior are retained. No candidate, preparation handle, credential or mutation owner enters queue persistence.

Older queue4 writers must be fully closed before adopting format5. They reject its envelope rather than silently ignore the new state. Downgrade requires a verified pre-update queue/data backup; no automatic downgrade is claimed. Existing installed editions and actual user files were not migrated. Credential/profile4/export3/macro formats are unchanged.

## Reproduced defect and corrections

Read-only review found that provider cancellation was checked before classifying a returned CleanupFailed. The new strict regression reproduced this: **0/1 passed**, with OperationCanceledException masking the failure. [Before-fix TRX](../../artifacts/sessions-manager-tests/manager-0.25-before-cancel-cleanup-fix.trx) is retained. Classification now precedes cancellation; cleanup failure survives and poisons provider reuse. A separate queue regression verifies that a late CleanupFailed overrides its earlier Cancelled observation and blocks further queue use. Both pass in the final complete suite and repeats.

The reviewed shutdown ownership issue and final-intent getter snapshot were corrected with targeted regressions; no pre-fix runtime reproduction is claimed for them. One initial test analyzer warning was corrected before final forced builds, which had zero warnings/errors. Earlier2,220-case positives lacked the new cancellation/late-cleanup regressions and do not establish those behaviors.

## Evidence and limits

The focused149 cases comprise37 queue lifecycle/presentation cases,44 synthetic native-composition cases and68 queue storage cases. Publisher positives, browser/HTTP responses, admission and all native starters are fake. DPAPI, temporary file identities/sharing, SQLite and applicable helper-process contention tests use owned fixtures. No real authentication material or reference executable was accessed. Launch-row checks are view-model evidence, not a rendered desktop/UI/DPI acceptance pass.

The packaged synthetic preparation case traverses the actual queue and format5 store with fake dispatch, then verifies forced-paused, reservation-free preparation recovery. The separate owned-session/final-handoff packaged case remains synthetic. Exact-stage tests replace only Core/Windows/App assemblies in the already-built test runner and use no-build/no-restore; their hashes matched stage before/after. These are unobfuscated development binaries, not final protected release evidence.

Source suite finished **2026-11-06 05:35:40.4060169 UTC**. Exact-stage suite finished **05:36:51.2906568 UTC**. Packaged checks finished **05:36:03.0722490** and **05:37:42.1381758 UTC**. Runtime: .NET10.0.12 / SQLite3.53.4.

- App SHA-256: `F7035B2D56EB408A457DD80D31C2EADEC6EA6045FD99B81C4A350F78C64B77B8`.
- Core SHA-256: `94B0ACED74030FE6372B2CB8288B2757531E07B18817D8E5B66EAB7B6A58C45D`.
- Windows SHA-256: `11C9A7849647982BAB0E113C255C670BBAEF62CDD72CEAE2277A98BF69202BC4`.
- Manifest SHA-256: `16FC8E7CCE6BCF4D256481B71215CE550A2F3C7EF5DCDB770B6F320F84163C11`.

[Verification record](../../artifacts/sessions-manager-verification-0.25.json), [source suite](../../artifacts/sessions-manager-tests/manager-0.25-source.trx), [exact-stage suite](../../artifacts/sessions-manager-tests/manager-0.25-final-binaries.trx), [repeat1](../../artifacts/sessions-manager-tests/manager-0.25-preparation-repeat-1.trx), [repeat2](../../artifacts/sessions-manager-tests/manager-0.25-preparation-repeat-2.trx), [first package](../../artifacts/sessions-distribution-check-0.25-first.json), [repeat package](../../artifacts/sessions-distribution-check/distribution-check.json).

Rule18: bounded queue, one actual preparation/native owner, no new dependency, service or capture/input loop. Rule20: incremental optional interface and shared dispatch helper preserve the legacy path; format5 protects old readers and unchanged installed data provides the current rollback boundary. The existing stage is reused; no extra release tree was installed. Filename-pattern audit is not a comprehensive secret/malware scan.

Next: explicit selected-installation/native trial controls and account/process/arrival observations, preserving the unverified capability gate. Actual browser grammar, native account selection, multi-client support, background input/broadcast, reconnect, host integration, full UI/performance and2/4/8/16-client acceptance remain unfinished. Notify the owner only when the integrated two-account Coloseum trial is actually ready.
