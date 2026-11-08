# Upload-limit external-helper ownership repair

Reviewed base: integration 11883ef (including immutable review of 3cfa4c6..21354e980637cfbf97517565446b96f6ce7d0968 and startup delta 11883ef).

## Important finding and behavior

Previously, cancelling or timing out Apply finished the operation task while an approved PowerShell could still write Windows QoS policy. A later Remove could report success first, then the abandoned Apply could recreate the policy. Disposing a Process handle did not establish process exit. A prompt approved after cancellation had the same race.

The controller now writes and flushes an unresolved-helper marker before invoking ShellExecute. The exact in-memory operation stays the owner until a definite Declined/NotStarted result or actual process exit. Cancel, Panic, Lock, timeout and wait failures do not permit another mutation while that owner remains unresolved. A rejected retry does not replace its operation revision. Duplicate and stale callbacks cannot overwrite a newer result. The runner retains an approved process through actual exit without killing it; ReleaseAsync stays cancellable for bounded application shutdown. Late completion after controller disposal may reconcile the saved record but cannot publish into the closed shell.

The persisted record includes helperUnresolved. Reopening Zenith during the same Windows session refuses another mutation if the owner was lost. Windows restart clears that session's active-store policy; the follow-up repair below clears the record only when monotonic uptime proves that restart. Legacy unconfirmed records without the field are conservatively unresolved; legacy confirmed records remain readable. Unreadable, oversized or unwritable ownership records fail closed before any helper starts. The existing per-data-root application owner is assumed; this repair does not arbitrate unrelated administrator scripts or different Zenith data roots changing the same Windows policy.

The existing controls are disabled while an unresolved or unreadable owner exists, and reconcile when the owner resolves. Only status/Guide copy and those existing bindings changed; there is no UI redesign or automatic policy removal.

## Exact offline evidence

All commands used Release, -m:2 -nr:false and excluded UploadLimitNativeTests and the paid AIVision cloud test. Helper starts, process waits, UAC outcomes, Windows boot time and shell controls were synthetic. No Roblox account/client, real administrator prompt, network policy change, shutdown or paid AI call was used.

Artifacts under artifacts/sessions-tests-0.69-uploadowner/:

- upload-owner-red.trx: 4 failed, 0 passed. Old-owner overlap after Cancel/timeout, same-session reopening, and failure to persist ownership reproduced before repair.
- helper-exit-red.trx: 3 failed, 0 passed. Cancelled/timed-out approved helpers and late-approved prompts were released before actual exit. The only prior source change was an injectable fake wait seam.
- upload-owner-ui-red.trx: 2 failed, 0 passed. Existing controls remained enabled with unresolved/unreadable ownership.
- upload-owner-final.trx: 86 passed, 0 failed, 0 skipped. Includes all new regressions, prior policy/control/presentation/runner lifecycle tests, failed initial/final writes, legacy records, Windows restart, callback duplication, cancellation-boundary no-start, closed-shell reconciliation, and wait failure without a callback subscriber.

Earlier intermediate runs are retained: upload-owner-first-green.trx (7 passed), upload-owner-integration-first.trx (48 passed, 18 failed), upload-owner-integration-second.trx (76 passed, 2 failed), upload-owner-integration-green.trx (85 passed). The intermediate failures were prior assertions that allowed immediate retry or assumed synchronous runner entry, plus outdated status copy. They were updated to require the new ownership boundary, not removed.

Requirement checkpoints: Before/After 0.69-uploadowner, CAP-11-03, CAP-13-04, PROD-QA-03. The after checkpoint hashes the red and final TRX artifacts. No release readiness is claimed.

## Independent source review scope and remaining gaps

Read-only checkpoint 0.68-updatemode covered configured versus actual launch mode, held executable qualification and trial revocation, release-update check/configure/Panic/Lock generations, installer trust and bounded staged archives, and startup public-network guard ordering. The D6a process-control and D7 localization changes were also read independently. No other critical/important finding was established in those scopes; see artifacts/sessions-review-0.68-updatemode/source-review.txt for exact source scope. This is static review, not service/native acceptance.

An earlier vault/portable review found one minor cached master-password-mode UI issue: if another store instance enables password mode after the first shell cached Disabled, Lock followed by Unlock can open its dashboard without showing the password prompt; actual credential reads refresh control and fail closed. Parent explicitly retained this as a minor follow-up, not part of this repair.

Attended Windows trials remain required for real UAC decline/late approval, policy readback and removal, cancellation while a helper is running, restart behavior, and actual process-handle release. No throughput or gameplay benefit is asserted. If a marker is unreadable, recovery requires reviewing that record; deleting it blindly could lose an outstanding helper and is not automated. LiveTestReady remains false pending the integration gates and owner trials.

## Clock-correction follow-up (0.70-uploadboot)

Fresh review of 8767e7d found that UTC minus uptime was not a stable boot identity. Moving the Windows wall clock by more than the two-minute tolerance made an unresolved helper appear to belong to an earlier boot, so reopening Zenith could allow a newer mutation while the old helper still ran.

New records persist observed monotonic uptime ticks alongside the non-authoritative UTC metadata. Only a strictly lower current uptime proves a reboot after that observation. Equal or greater uptime, whatever the UTC shift, keeps the record and hold. An ambiguous later boot is intentionally not guessed: reopen Zenith promptly after restart, and retain the hold for recovery review if a reset cannot be established. Legacy records without an uptime observation are upgraded while preserving their existing unresolved state; a subsequent demonstrated uptime reset can recover them. Malformed uptime is unreadable and fails closed. The UTC helper remains only a metadata comparison and is no longer used to admit upload-limit changes.

Four regressions failed against immutable 8767e7d before production edits: forward/backward wall-clock correction, ambiguous greater uptime, and legacy unresolved metadata with an older UTC stamp. Artifacts: artifacts/sessions-tests-0.70-uploadboot/upload-boot-red.trx (4 failed, 0 passed), upload-boot-green.trx (90 passed), and upload-boot-final.trx (93 passed, 0 failed/skipped). The final run also covers equal uptime, legacy observation followed by a proven reset, malformed uptime, and all prior ownership/presentation regressions. The existing confirmed-record and unresolved-owner restart tests were preserved; only the strict JSON-field assertion was extended to include the new uptime field.

Before/After 0.70-uploadboot checked CAP-11-03, CAP-13-04 and PROD-QA-03. Clocks, uptime and helper results were injected; the Windows clock was not changed, Windows was not restarted, and no UAC/QoS, Roblox or paid AI operation ran. Native acceptance remains attended.
