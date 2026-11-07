# Native trial and capacity integration — 0.26

November 6, 2026. Personal development scope only. This increment does not establish native account selection, actual arrival, multi-client compatibility, unattended reconnect, background input, performance superiority or finished-release acceptance. No real account, browser, Roblox client, production window or installed edition was used or changed.

## Executed evidence

The final source build passed **2,386/2,386 tests**, followed by the same full suite against the **exact staged Core, Windows and App assemblies**. The affected queue, configured provider, reservation projection, queue store and sampling cadence cases passed **256/256 twice more** against those staged assemblies. Both final forced builds reported zero warnings/errors. The final packaged self-check passed **30/30 twice**, on .NET 10.0.12 and SQLite 3.53.4.

The [verification record](../../artifacts/sessions-manager-verification-0.26.json) contains timestamps, report hashes, final first-party assembly hashes and the manifest identity. All **25 manifest entries plus the manifest itself (26 files)** matched their expected sizes/hashes; there were zero extra files and zero matches against the explicitly recorded private/test filename patterns. This bounded filename audit is not a general proof about all binary contents.

The final App hash is `F40F917BD5267061A75E18E3AB939C04F05D9B3CCFA4709E3DEA7713FE01D0E2`; Core is `D955452C47E354536CDE2C9CC5B685C59EE7102ACA4CA1A677EEE15BD0D12AF5`; Windows is `D0817D46BCE8A6D54BCDC29F892B0840C8DBC6369298406BBBCAB9CE5AE0DF66`. The final manifest hash is `82E2057C001FC296CCF86272800803AB3AED63ACA68504D42A3138494154684F`.

Fixtures use synthetic account/session values, callback-only native starters and synthetic publisher positives. Isolated SQLite/DPAPI/files, owned helper-process tests from the regression suite and read-only Windows counters are real operations. They do not imply a real Roblox test. The new production confirmation dialog and native trial button were compiled and their binding/guard paths checked; the dialog was not clicked and the production views were not visually exercised.

## Explicit trial permission and installation ownership

The configured native provider always advertises **Unverified**. Ordinary account, group and bundle queue commands still produce blocked native requests. `EnqueueTrialAsync` is a separate, explicit request requiring a preparing provider, selected saved-account binding and explicit destination. Permission exists only in memory for that operation ID and current stop generation. Pause, Panic, Lock, installation changes and restart revoke unstarted permission; Resume cannot recreate it. A trial cannot turn a provider's arrival claim into verified arrival.

The Automation trial card names the expected player, requested place/server and chosen native executable separately. The UI asks for confirmation before queuing a real-client trial. A fresh private Roblox page provides its one-use handoff; the final saved grant, publisher-held file, expiry, cancellation and capacity checks remain in the existing dispatch path. The URI and reusable credentials are not exposed in queue records or trial feedback. No browser's unrelated stored sign-in is used.

The selectable provider owns one shared trust checker and one child operation at a time. Selection changes cancel/fence an old owner but do not free its slot before actual preparation, dispatch and cleanup end. Cleanup failure permanently blocks reuse of that provider instance. Borrowed account/capture/vault services are not disposed by the child. Shell shutdown independently attempts capture disposal and continues protected-store cleanup even when the configured provider reports failed cleanup, preserving a fixed failure indicator.

`WaitForActiveCleanupAsync` covers the actual queue cycle, including late Ready handles and their disposal. Profile removal and disconnect stop the queue and wait before changing the relevant saved state. A queued trial's stale continuation cannot overwrite the stop indicator after Panic/Lock/cancellation.

## Resource forecasts and process observations

`LaunchReservationProjection` is a pure bounded calculation: at most 1,101 queue rows and 4,096 observations, no timer, process operation or account association. A valid retained receipt remains reserved until explicit closure acknowledgment. Missing, stale, duplicate, wrong-start-time or incomplete evidence keeps conservative forecasts. The default unfamiliar-client budgets remain 2.5 GiB physical and 3 GiB commit.

A unique exact PID/start observation may credit already consumed **private working set** and **private commit** only when fresh, clean and coherent with the system sample. Shared working set is never treated as private physical credit. Missing private working set retains the physical forecast even if independently valid commit can be credited. Impossible aggregate claims restore that channel's full budget. Remaining growth saturates at zero; a zero forecast does not erase occupancy or prove account/arrival.

Process sampling now precedes a simultaneously due system-memory sample. It retains actual timestamps and the independent 1-Hz essential cadence; it does not fabricate coherence by changing cached observation times. Cached, partial or failed inventories cannot establish absence.

The Shell uses these reservations in displayed capacity and final admission. A start's own not-yet-emitted reservation is excluded when asking whether **one** additional client fits; other forecasts remain charged. A freshly and exactly observed `ClientStartedUnverified` may clear the serialized uncertainty barrier while retaining its forecast. That evidence is rechecked after browser preparation and at the native boundary. This also holds after a restart: startup keeps a `ClientStartedUnverified` row that has a valid receipt and pinned budget in that state (the queue still restarts paused and the row stays reviewable), so the same exact observation can clear the barrier; identity is still not reconciled. Uncertain/review work, including restarted Uncertain or unbudgeted rows, remains blocked. A normal Supported provider's completed arrival retains its existing independent queue behavior.

These are controlled arithmetic/lifecycle results, not measured Roblox client capacity. Native unverified-start warm-up/stabilization policy still needs an explicit acceptance review before multiple live trial starts; a process observation alone is not load stabilization.

## Queue compatibility and explicit review

Queue writer **6** reads formats 1–5 without rewriting merely on load. Preparation markers require format 5 or later; the explicit `ClientClosureAcknowledged` marker requires format 6. Raw preflight rejects incompatible markers/future formats before backup fallback or replacement. Existing generation/profile/macro formats are unchanged.

Only explicit client-closure confirmation produces Cancelled plus that marker. Historical receipts remain visible. An OS process still present is counted independently even after acknowledgment. Legacy Cancelled rows with an unresolved valid receipt remain reviewable and cannot silently disappear through history trimming or profile deduplication. Older writers must be closed before adoption; rollback uses verified pre-update files rather than an unsupported downgrade of the live queue.

## Failures retained and corrected

1. The strict launcher-selection regression failed **0/1** before the fix: selecting an inventory-valid `RobloxPlayerLauncher.exe` row threw from the native-only provider selector. The Shell now leaves that inventory row visible, clears the trial slot and reports unsupported selection. The original failing TRX is retained.
2. The first full run passed 2,384/2,386. One new XML assertion incorrectly expected a direct binding instead of the existing ancestor binding; the other old assertion expected every Cancelled receipt to stop being reviewable, conflicting with the new explicit-review contract. The assertions were corrected to check the actual binding and require the acknowledgment marker. The failed full report is retained; neither is claimed as a separately reproduced runtime defect.
3. A repeated package run failed at the new synthetic trial fixture because prior synthetic history produced more than one row. The self-check now resets only its marker-owned fixture queue before running. The failed package report is retained. Both final package runs passed after this correction.

Source review also identified stale trial feedback and cleanup-order handling, which were corrected before final tests. No pre-fix runtime reproduction is claimed for those review findings.

## Remaining work and readiness

No live-test invitation is issued by this increment. Finish the native-trial evidence controls, warm-up/admission review and integrated UI checks, then validate actual selected-account/process/arrival association and controlled two-account behavior. Background input/capture isolation, independent macros, broadcasting, reconnect, host integration, owned-game acknowledgments, full appearance/mini-mode parity and sixteen-client acceptance remain unverified or incomplete. The owner will be notified for the Coloseum test only when its integrated procedure is ready. No performance or release-ready claim follows from the passing checks.
