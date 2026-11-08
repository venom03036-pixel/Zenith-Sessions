# Protected portable backup — controlled evidence

Scope: R-accounts-11/R-shell-18, VA-03-01, VA-08-05/06, VA-10-02 and PROD-QA-07. Design: [password vault and portable backup](PASSWORD-VAULT-AND-PORTABLE-BACKUP-DESIGN.md). Metadata-only export remains the default separate action. This implementation has not passed independent sensitive-code review or live acceptance.

## Format and authority

`PortableBackupCodec` uses a 64-byte fixed header, 32-byte random salt, 12-byte nonce, PBKDF2-HMAC-SHA512 at exactly 600,000 iterations, AES-256-GCM and a 16-byte tag. The whole header is authenticated. Parsing rejects unsupported header/KDF values, inconsistent/trailing lengths and payloads above 32 MiB before derivation. Decoded JSON has depth/collection limits, rejects duplicate and unknown fields, and owns each decoded mutable cookie buffer even when parsing later fails. Profile/group limits are 10,000; favorites retain the existing limit. Output temporary files contain ciphertext only. Owned plaintext, cookie, password and derived-key buffers are cleared. Existing managed authorization strings and serializer/runtime buffers do not support a claim of perfect memory erasure.

The payload carries profiles, groups (including empty groups), favorite destinations, allowlisted preferences and unexpired reusable linked Roblox sessions. It carries no native launch receipts/tickets, launch qualifications, publisher acceptances, browser state, executable paths, webhook secrets or automatic actions. Preferences are theme, density, capacity/performance settings, tile parameters and close-button behavior. Importing these is a separate choice; local language, display identity, arrival action, security/idle/Panic, OAuth and Discord settings remain local. Main-account preference is remapped to a newly imported profile or cleared. No installed client or running process identity is restored.

Capture uses bounded paged metadata plus optimistic revision/snapshot checks. Observed concurrent changes require retry; this is not a cross-store filesystem transaction or a guarantee against a malicious same-user writer. Export refuses protected app/data destinations, device paths and observed linked ancestors. The current-user filesystem and native API limitations documented for the vault still apply.

Import gives new profiles and favorites fresh local IDs and revisions; source IDs only map proposed sign-ins. Existing label/username profiles and exact place/job favorites are preserved. Existing group configuration takes precedence. An identity already saved in the destination is retained without sending the older backup cookie to Roblox. A candidate must validate as exactly the exported player through the existing owned link service, then commit through its generation/snapshot fences. Source generations are never adopted. Mismatch, expiry, conflict or unavailable verification leave imported metadata needing sign-in. There is no secret rollback. Partial results report confirmed completed counts; a cancellation at a storage boundary can require reviewing the actual library before retrying.

## UI and lifecycle

Only two items were added to the existing account-library menu. Export's confirmation and filename identify that the file contains reusable sign-ins; a separate password is mandatory. Import decrypts first, then requires a content-count confirmation before any library mutation or Roblox request. The preference choice is separate. Reviews bind owner, epoch, action, path and preference choice and are single use. Sample, locked, busy and unavailable vault states cannot start a transfer.

Import pauses the owned launch queue, macro work, arrival layout and keep-awake work without relocking the authenticated destination vault. It never resumes those operations. Global Panic/Lock still cancel the transfer, clear the vault key and drain the tracked task; a stopped content-confirmation cannot continue. Settings commits share the shell settings writer; persisted allowlisted changes are reconciled even after a partial cancellation. Password buffers and decoded documents are disposed on every owned exit. Shutdown joins this owner before disposing its repositories/vault.

## Executed evidence

Artifacts: `artifacts/sessions-tests-0.51-d4-portable/`.

- `portable-service.trx`: six initial service failures exposed use of a 1,000-item account page where the repository supports 100. The corrected service uses 100-item pages; group pages retain their supported limit.
- `portable-service-fixed.trx`: 20 codec/service cases passed, including independent-library round trip of all categories, new profile IDs/generation 1, existing-player preservation without validation, exact-player mismatch, cancelled late validation, competing destination generation, locked vault and protected output paths.
- `portable-presentation.trx`: 28 passed and one assertion failed only because the existing queue status begins with capitalized “Paused”. The assertion now compares that state case-insensitively.
- `portable-affected.trx`: **660 passed, zero failed/skipped**. Includes nine new dispatcher cases for one-use/stale reviews, notification reentrancy, Cancel/Panic/Lock drain, post-decryption confirmation, master-vault state, imported preference publication, sample/lock gating, plus existing vault/link/settings/shutdown/metadata/favorites/XAML regressions.

Command: `dotnet test tests/Zenith.Sessions.Tests/Zenith.Sessions.Tests.csproj -m:2 -nr:false --no-restore` with an affected-test filter. All records/passwords were synthetic and directories temporary. No real account, live Roblox sign-in validation, paid cloud test or user-data encryption/export/import ran.

## Remaining checks

Independent source review must inspect format authentication, parser bounds, buffer lifetime, explicit consent, settings merge, import identity and late-write fences. Integrated full suites, Release/self-check, packaged keyboard/screen-reader/theme dialog checks and attended export/import with disposable test accounts remain open. A different Windows account or machine is needed to demonstrate portable restore and destination DPAPI rebinding in practice. Damaged storage and crash/power-loss behavior require controlled fixtures. **LiveTestReady=false; release acceptance=false.**

## Direct-service allowlist follow-up

The file codec already excludes per-account extra launch arguments through the account model's `JsonIgnore`. A direct service caller can still construct a document carrying that field. The import service now clears it explicitly when issuing fresh destination profiles, so the authority restriction does not depend only on serialization. `portable-arguments-before.trx` records the actual prior failure (expected no argument, received `--imported-argument`). `portable-arguments-after.trx` passes all **66** selected portable/argument fixtures with no failures/skips. No real launch occurred. Checkpoint: `0.51-d4-portable-review`.
