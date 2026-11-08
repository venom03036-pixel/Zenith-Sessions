# Optional Windows EFS: design and verification

Scope: R-accounts-12, VA-03-01, VA-03-04, VA-08-02, VA-08-05 and PROD-QA-07. The owner's decision selected built-in Windows EFS rather than SQLCipher. This operation is opt-in and separate from the master-password and portable-secret-backup proposals. Existing DPAPI records, SQLite schema, metadata-only exports, themes and layout stay compatible.

## What the action does

Settings → Protected-file recovery now contains an encryption action, cancellation action and result line. Confirmation explains Windows/certificate requirements, possible loss of access if the certificate's private key is lost, current-user access, copies/exports, recovery agents, partial completion and no automatic decryption. The confirmation is one-use, belongs to that shell/library, and is invalidated by Panic, Lock or shutdown. Starting the app, loading a saved setting or opening a sample library never enables EFS automatically.

The Windows service uses `File.Encrypt`. It preflights the initialized data root and at most 10,000 entries with a maximum relative depth of 32, then applies encryption in parent-before-child order. Marked directories allow newly created files to inherit encryption. It re-enumerates the tree and reads the encrypted attribute afterward. It does not read file contents, copy plaintext, decrypt credentials, migrate SQLite, reserialize vault records, add a dependency or save a boolean claiming that encryption remains effective forever.

Root checks require a local ordinary path, the current Windows SID as directory owner and an existing non-linked `accounts.db` marker. Whole drive/profile/system-data folders are rejected. Every existing ancestor and target is opened without following reparse points; reparse points and file hard links are rejected. File identity consists of volume serial and file index, checked before and after the path-based call and during final readback. Root identity is also compared between entries. Unexpected native failures are converted to bounded typed results rather than copying exception text or paths into the status.

## Native API boundary

The first filesystem regression disproved a proposed locking assumption: a `READ_ATTRIBUTES` handle without delete sharing does **not** prevent renaming the file on this Windows system. The corrected implementation keeps attribute handles only for identity comparison and makes no pinning or race-proof claim. A data-read handle would conflict with `EncryptFile`'s exclusive-access requirement.

Consequently, pre/post inspection detects normal replacement and static links; it cannot guarantee confinement against an attacker already executing as the same Windows user and actively racing the path-based native call. Such an attacker also normally has access to the user's decrypted EFS data. The operation runs under the current user and never requests elevation. Parent review explicitly accepted this limitation for the optional current-user attribute operation.

`Verified` means the bounded enumeration at the end found encrypted attributes on the inspected entries and the observed path identities matched. It is a point-in-time filesystem observation, not cross-user proof, certificate-recovery proof, a forever-encrypted promise or a portable-backup guarantee. External replacements/moves and signed-in copies/exports can change protection. The confirmation and result text state those limits.

Microsoft references: [EncryptFile requirements and exclusive access](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-encryptfilew), [File.Encrypt platform support](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.encrypt?view=net-10.0), [encrypted-file copy behavior](https://learn.microsoft.com/en-us/windows/win32/fileio/handling-encrypted-files-and-directories), [Windows certificate backup through cipher](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cipher). Certificate backup is a user-managed Windows operation; Zenith does not export keys or run cipher automatically.

## Interruption, recovery and compatibility

There is no record/schema migration. Partial encryption leaves each completed filesystem change in place. Failed, locked or unsupported entries remain explicit; no successful-looking flag masks them. Re-running rechecks existing encrypted entries and attempts remaining ones. No code path calls `File.Decrypt`, rolls back encrypted files or removes a recovery copy.

One service operation runs at a time. Work occurs off the UI thread with bounded enumeration, one worker and no timer. Cancellation is checked between native calls; the synchronous EFS call itself is not interruptible. Panic/Lock invalidate publication immediately and signal cancellation. Shutdown awaits the actual owned task, including a native call still returning. This does not promise a native call finishes within a fixed deadline.

Files held open by this app or another process can prevent encryption; a result may remain partial while those files are open. The app does not close/reopen all its database, log and vault owners to force completion. Directory inheritance can protect later replacements, but only a later check establishes their actual attributes. Do not delete damaged originals or infer that restarting completed the operation.

Deleting this application feature does not decrypt previously encrypted files. Restoring the old binary does not restore a lost EFS key. Windows certificate/recovery procedures are required if the OS cannot access the data. Existing DPAPI machine/user dependence remains even if an EFS certificate is separately restored.

## Executed offline evidence

- `artifacts/sessions-tests-0.51-d4-efs/efs-focused.trx`: 15 passed, 1 failed. The failure demonstrated that attribute-only handles did not prevent rename; the assertion was replaced by the required actual replacement detection, and the false locking claim was removed.
- `artifacts/sessions-tests-0.51-d4-efs/efs-corrected.trx`: **18 passed, zero failed, zero skipped**. The compile also completed without reported warnings/errors.
- `artifacts/sessions-tests-0.51-d4-efs/efs-regressions.trx`: **742 passed, zero failed, zero skipped**, covering the affected view-model, preview, Panic and shutdown tests. `efs-final.trx` then passed **18/18** against the final canonical local-drive/root-location refinement. Neither run reported compiler warnings/errors.
- Filesystem tests use owned temporary fixtures, native junctions/hard links and real file identities with an injected encryption adapter. They cover directory-before-file ordering, unchanged bytes, idempotent retry, unavailable platforms, sharing failures, misleading success/readback, new unencrypted entries, cancellation, duplicate operations, path replacement, static root/child junctions, hard links and traversal limits.
- Presentation tests use the real shell with a controlled service: no startup encryption, explicit one-use review, stale review after Panic, cancellation/Lock/Panic late-result fences, sample-mode refusal, reentrant command notification and shutdown joining a deliberately delayed owner.
- An initial broader-test invocation was not executed because automatic approval review hit its usage limit. After the user continued and the service resumed, the same approved route executed the successful 742-case run above; no approval bypass occurred.

No real EFS was applied, no real user-data files or accounts were read, and no Roblox or paid-cloud test ran. Native EFS compatibility, post-restart inheritance on real SQLite/log/vault writers, certificate recovery and other-Windows-user denial remain attended fixture trials. They are not claimed from the synthetic adapter tests.

## Rule 18 and review points

CPU and memory are bounded by the 10,000-entry/32-depth inventory; native work is on one explicit worker rather than per-entry tasks. The UI only starts/observes the service. Cancellation is owned and shutdown joins it. Errors are typed and preserve partial work. Tests exercise Windows path metadata independently of the EFS adapter. Existing metadata and secret formats do not change. Review the current-user root check, path/API race limitation, exact status wording, lifecycle owner integration and the two new controls. Master-password mode and portable secret export require separate reviewed designs and are not implemented by this change.
