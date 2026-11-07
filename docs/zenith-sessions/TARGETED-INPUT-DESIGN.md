# Targeted input ownership — incremental design, 0.33

This increment supplies the missing bounded owner for target-directed input. It does not qualify Roblox background automation or replace the existing foreground executor. Macro dispatch/host IPC, a macro-compatible target-frame source, verified account association, broadcasting hooks and real-client acceptance remain separate unfinished integrations. The owner is exercised with controlled transports and registered windows belonging to the diagnostic process only.

Rule20 review completed before implementation: the independent reviewer approved the additive scope, requiring an explicit native commit point, target exclusivity across incarnations and one upgradeable transition owner. Subsequent correctness review and corrections are recorded in [verification](TARGETED-INPUT-VERIFICATION.md).

## Contract and migration

- Add platform-independent signal, pinned-target, result and run-owner contracts to Sessions.Core. No macro document format, SDK player signature, installed host, account/vault or saved setting changes.
- A target contains a registration incarnation, process ID/start time, HWND and physical content geometry. Registration is not account authorization. A transport must independently validate identity and geometry before ordinary submission and identity before cleanup. A revoked incarnation cannot be rebound to a reused HWND.
- One coordinator serializes transport calls across its bounded active runs. One batch may be outstanding per run; callers receive explicit Busy/Capacity results instead of creating an unbounded queue. Batch/hold/run limits are operational budgets, independent of saved profile counts. No new polling thread, capture loop or dependency.
- A run pins its purpose and target. The same target cannot be claimed by another run, including while paused or cleaning up. Thus a future broadcaster cannot acquire a target still owned by a macro.
- Transport results distinguish queued submission from rejection/uncertainty; queued Windows messages do not prove receiver processing, physical key state or game behavior. Keep logical unfinished holds separate from submitted/possibly submitted holds. Cleanup releases only this run's submitted holds.
- Pause/Stop/Panic close admission and advance a generation before awaiting the transport lane. Recheck at each event and immediately inside the native transport boundary. Already submitted messages cannot be recalled. Stop/Panic share a tracked finalization task; no UI, credential or license dependency.
- Pause releases submitted holds while retaining unfinished logical holds. Explicit resume revalidates the pinned target and restores those holds in original order. Completed events are not replayed. Failed/uncertain release or partial restoration remains faulted and retains the target lease for explicit cleanup; it never reports Ready or silently permits a replacement run.
- Cancellation stops future dispatch and joins cleanup. Cleanup has a cancellation deadline supplied to the transport, but the owner awaits the actual operation; it never treats a timed-out observation as completed native work. A non-cooperating transport can therefore leave shutdown pending and the target unavailable, which is reported truthfully.
- Extend the existing owned-window probe to use this owner rather than its direct helper. Register/revoke own HWND incarnations, record counts/sequence acknowledgments only in the cooperating receiver, and retain NativeControlledExercise provenance. Preserve all real-client gate requirements.

## Regression risks, verification and rollback

The changes are additive. Main risks are cancellation races, partial batches/holds, cleanup error masking, cross-run target reuse, resource growth and conflating queued with consumed input. Deterministic controlled tests cover each boundary, duplicates, backpressure, pause/resume, stop during validation/emission/restoration, uncertain outcomes, cleanup retries and disposal. Owned-window checks cover actual standard message delivery without global input or real clients. Existing full source, exact staged binary, packaged and capability suites remain required. No test result upgrades live capability.

Rollback removes the additive owner/probe integration; no persistent migration is required. Existing MacroSuite SendInput/foreground checks remain unchanged. No PostMessage fallback to SendInput, focus cycling, virtual HID, client modification or external process registration is introduced.

## Rule 18 review

CPU and memory are bounded by configured active runs, one outstanding batch per run, batch length and held controls. Awaitable serialization uses no dedicated worker or timer. Hot emissions avoid LINQ and bulk allocations except a bounded caller snapshot per batch. All async paths are tracked and cancellation-aware; provider failures preserve uncertainty. Tests need no real screen/keys. The owner handles no secrets or stored files, and introduces no third-party package. Performance and Roblox support remain unmeasured.

Reference: Microsoft [PostMessageW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew) documents asynchronous queue submission, UIPI and queue limits; [raw input](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input) is a distinct input path. Standard posted messages alone are not evidence that Roblox accepts the requested actions.
