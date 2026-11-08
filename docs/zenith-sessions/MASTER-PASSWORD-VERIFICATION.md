# Optional master-password verification

Scope: R-accounts-10; VA-03-01/02/03, VA-08-01/05/06, VA-10-02 and PROD-QA-07. The reviewed [migration and portable-backup design](PASSWORD-VAULT-AND-PORTABLE-BACKUP-DESIGN.md) separates this layer from EFS and portable export. Password mode remains off by default. No existing real library, password or Roblox session was used.

## Implemented behavior

`ProtectedCredentialVault.Password.cs` adds a password-wrapped random data key. PBKDF2-HMAC-SHA512 uses exactly 600,000 iterations and a 32-byte random salt; AES-256-GCM uses a 12-byte nonce and 16-byte tag. The key control is itself protected with CurrentUser DPAPI. In password mode there is no parallel DPAPI-only data-key copy. Existing record DPAPI ciphertext gains an authenticated AES wrapper; its inner provider/player/profile/generation validation remains authoritative. The bounded control parser rejects unsupported versions, lengths and KDF values before derivation.

Enable/resume holds the existing writer/process lease. It validates current records, writes an authenticated migration marker, atomically converts each record, removes recognized obsolete copies, and verifies the bounded inventory before marking ready. A mixed v2/v3 interrupted conversion remains blocked for ordinary secret use in every store instance until explicit same-password resume. A new instance also refuses a mixed inventory whose key control is missing. Snapshot digests change; profile identities and authorization generations do not. Unknown copies keep migration incomplete. There is no automatic restore or downgrade.

Unlock uses an epoch fence and a short key lock. Manual, Windows and idle lock, Panic, cancellation and shutdown clear the mutable owned key and prevent a delayed unlock from installing it. Already acquired authorization strings cannot be guaranteed erased; their existing operation owners are separately stopped and joined. The password modal uses `PasswordBox.SecurePassword` and owned mutable UTF-8 buffers, clears them on every exit, and keeps an in-progress operation owned until it returns. It never persists a password or sends it to diagnostics.

Settings gains only explicit enable/resume, disable and recovery controls with status. Disabling requires the current password and rewraps only the data key. Forgotten-password recovery is a separately confirmed destructive removal of local sign-ins. It retains account metadata and generation tombstones, advances invalidation before deletion, removes the key control only after the protected inventory is gone, and never decrypts or rolls back old credentials. Unknown or undeletable copies retain the control for another explicit recovery attempt. Running clients are not closed or remotely signed out. A corrupt key control leaves metadata/recovery available at startup; projection journals are preserved until authenticated unlock.

## Executed evidence

- `password-focused.trx`: initial nine storage fixtures passed.
- `password-expanded-fixed.trx`: 23 storage/presentation fixtures passed. An earlier failed run exposed a recursive Panic notification path; the owned stop guard fixes it and the regression remains.
- `password-regressions-fixed.trx`: 872 affected vault/storage, view-model, preview, shutdown and Panic cases passed, zero failures/skips. The preceding run exposed lost cleanup reporting for a damaged coordination file; normal cleanup reporting is preserved.
- `password-final.trx`: all 24 final focused storage/presentation cases passed, including a genuinely mixed v2/v3 interrupted fixture, zero failures/skips.

Artifacts are under `artifacts/sessions-tests-0.51-d4-password/`. Commands use `dotnet test tests/Zenith.Sessions.Tests/Zenith.Sessions.Tests.csproj -m:2 -nr:false --no-restore` with focused filters. These are temporary CurrentUser DPAPI fixtures with synthetic records/passwords, fake public directories and fake client monitoring. No real sign-in, network authentication, paid cloud test or live encryption of user data was performed.

## Remaining evidence and limits

Independent sensitive-code review, integration regression, packaged-build checks and attended password-dialog accessibility/theme checks remain required. Different-Windows-user denial, crash/power-loss durability and real-library migration need controlled fixture trials. No live release readiness is claimed. Same-user malware, administrators, memory capture and restoring an entire earlier external filesystem backup remain outside the protection claim; this is not anti-rollback storage or secure media erasure.

Portable password-protected backup remains the next independent implementation. Existing metadata export remains unchanged and excludes credentials. Its future import must issue fresh local identities/generations, verify reusable sessions, preserve newer local sign-ins, and never trust launch qualifications, executable paths, publisher approvals, automatic launch or webhook secrets.
