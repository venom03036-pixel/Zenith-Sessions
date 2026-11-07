# Native launch stage diagnostics design

Cycle0.42, November6,2026. Development-only observability repair. This does not qualify native account launching, grant new permission or establish a real Roblox failure. The current preparation/provider paths discard fixed trust/browser/dispatch reasons before the existing trial evidence view can show them.

## Small incremental change

Add an optional synchronous `INativeLaunchDiagnosticSource.GetDiagnostic(Guid operationId)` returning a detached immutable `NativeLaunchDiagnosticSnapshot`. Use **LaunchIntent.OperationId**, not the separate protected mutation NativeHandoffContext.OperationId. Keep at most128 transient operation snapshots. No queue, profile, vault, export-of-credentials, macro or settings schema changes; old queue history simply has no current-run stage diagnostics.

The snapshot contains only an operation GUID, monotonic revision, actual start/update timestamps, fixed stage and cleanup enums, sticky stopped flag, and nullable existing trust status/reason, native preparation status, browser outcome/shape status and native dispatch status. A null outcome means not reached or not recorded, never success. There are no free-form strings, exceptions, installation/file/signer objects, credentials, user/destination IDs, grants, candidates or process handles. Expected/reported/process facts remain in their existing independent evidence model. A snapshot is never input, account or arrival authority.

Return optional trailing `NativeBrowserPreparationFacts` on NativeHandoffPreparationResult to preserve the browser's fixed outcome/shape after disposal. Copy these facts before cancellation/current-owner checks can throw. Preserve them through cleanup failure. Do not retain the source capture or preparation object in diagnostic history.

The configured provider owns the bounded history across child disposal/configuration. Read/copy the immutable child snapshot before disposing the child; merge the wrapper's cleanup result only after actual awaited cleanup. Record actual stage times and do not restamp historical facts on reading. Preserve active entries, evict only the oldest completed entries. Fence each write with its exact preparation owner and generation; late cleanup can finish only its original attempt, not a replacement, even if the operation GUID is reused. Diagnostics never participate in authorization, validation, admission, cancellation or reuse decisions. Existing real cleanup/poisoning behavior has priority.

## Presentation

Read only the selected trial's diagnostic snapshot on selection and the existing shared evidence refresh/export paths. Verify exact operation identity and current selection/lock/epoch before publishing. A small expander under existing trial evidence shows fixed readable trust/browser/preparation/dispatch/cleanup labels and original times. Missing/evicted/restarted information is explicitly unavailable. No new worker, timer, event subscription or request per row.

The explicit JSON report adds an allowlisted stage object and advances its diagnostic report format. Never serialize a full trust, browser, exception or provider result. Existing false account/arrival/input flags stay false. A prevented browser handoff must not be described as proof that native dispatch never occurred.

## Migration, regression and rollback

All new result arguments are trailing and optional; legacy callers remain valid. No file is rewritten on load. Existing queue lifecycle, receipts, uncertain-start reservations, explicit per-operation trial consent, publisher checks, protected-grant mutation ownership, stop generations and ordinary/group gates remain unchanged. Reverting this increment removes transient feedback only; persistent documents and settings need no rollback migration.

Tests cover actual fixed outcomes end to end through provider/wrapper, untouched null stages, rejected final boundaries, stopped/configured/late attempts, same-operation reuse, cleanup failures, history capacity and detached immutability, original time preservation, selected-operation publication and report field/redaction guards. Run source and exact-staged regressions, repeated affected cases, final packaged checks and production markup checks. Controlled checks cannot establish real browser/native/account/arrival or performance acceptance.

## Rules18/20 review

CPU and allocations are bounded by fixed-size value snapshots and128 entries, outside capture/input hot paths. No dependencies, threads, timers, diagnostic file writes or event stream are introduced. Each existing preparation owner adds one cancellation-token registration solely to record a prompt fixed stopped fact; it is joined/disposed outside the provider gate before that owner's token source is disposed. Existing tracked operations own cancellation and actual cleanup. Errors retain fixed reasons without secret text. Backward compatibility is additive and transient. Root and the independent read-only reviewer approved this design and the scoped cancellation registration before implementation; App/backend owners reviewed their existing paths. Final independent source review and executed checks remain required.
