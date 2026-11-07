# Multiple account groups — 0.22 verification

Personal **0.22.0-preview** passed **1,894/1,894 tests**, zero failed/skipped. The 110-case changed-path/storage-race set passed **two further runs**. Final packaged binaries passed **27/27 checks twice**; all **25 manifest files** matched size/SHA-256, with zero extra files or private/test filename-pattern matches. Forced final builds reported zero warnings/errors. Nothing was installed; **ReleaseReady:false**.

## Implemented behavior

Account metadata retains its existing primary Group and gains optional additional memberships. Names are trimmed, case-insensitively distinct using ordinal Unicode comparison, retain their first spelling/order and cannot repeat the primary group. A profile supports up to32 memberships, each60 characters; the additional-membership editor accepts at most2,048 characters. These are per-record/input bounds, not a saved-account product limit.

Profile editing accepts one additional group per line. Account rows show the complete membership summary, with trimming and a tooltip. Both normal and compact Accounts layouts provide Add to group for checked accounts on the current page. The dialog pins the selected profiles when opened. Apply pins their exact revisions and group name before the existing serialized profile lane can wait. Changing visible selection cannot redirect the operation.

The repository checks every selected persisted revision, including already-member no-ops, and validates resulting membership limits before writing. Its one SQLite transaction adds membership to up to100 profiles without replacing their primary/existing groups. Any conflict, limit failure or cancellation before commit rolls back the entire batch. No-op rows retain revision/timestamp. A cancellation after commit cannot undo metadata: the controller reloads saved state while preserving Panic/Lock feedback. Shutdown awaits actual ownership completion. No credential, process, queue or input operation is authorized by group membership.

Filters match either primary or additional memberships exactly, without substring/wildcard behavior. SQL parameters and the same ordinal case-insensitive collation cover literal and Unicode names. A review correction retains the current filter when its displayed spelling changes from Raid to raid. Reads remain paged at100 rows; the group-name picker remains bounded at1,000 entries. Group queries validate bounded per-row JSON and can scan the library. No measured latency improvement is claimed.

## Compatibility and recovery

Account library schema advances from3 to4. Versions1/2/3 receive a verified pre-migration SQLite backup before a transactional upgrade. Existing metadata, revisions and pending credential cleanup are preserved. Stored additional memberships are a validated JSON array; malformed/noncanonical values fail with recovery feedback and are not silently repaired. Existing credential envelopes, generation state, queue, settings and macro formats are unchanged.

Close older previews before using the upgraded library. Older schema3 applications must reject schema4; do not downgrade or mix writers. A rollback uses the verified pre-update copy rather than deleting the new library or rewriting it on load. This increment changes isolated development/test data only.

Metadata exports advance to version3 so older importers cannot silently discard memberships. Versions1/2 still import when additional memberships are absent or explicitly empty. Nonempty raw membership text under an old version—including whitespace or a duplicate-primary name—is rejected before any row is created. The bounded decoder accepts UTF-8 BOMs and rejects unknown/duplicate fields, duplicate source GUIDs, unsupported versions, invalid shapes and invalid profiles. Computed group views are excluded. Recognized historical IdentityState text remains inert. Imports create new local IDs, clear revision and claimed verified UserId, and include no authorization material. Read/decode limits remain16MiB/100,000 profiles per operation, with cancellation during reads and row validation.

Group colors, independent group records/order, shared rename/delete management and a complete group-management presentation remain unfinished. Named recipient bundles retain their existing separate reference semantics. No native group launch is enabled.

## Executed checks and corrections

The final suite adds35 backend membership cases,19 controlled STA application cases and22 metadata-decoder cases. Coverage includes verified schema3 backup/migration, older migrations, corruption preservation, literal/Unicode membership, revision/no-op/batch atomicity, cancellation, projection/favorite/edit/export/import preservation, pinned recipients, late Lock/Panic, disposal and10,000-profile paging. These are controlled tests, not production desktop rendering or Roblox results.

The first full run passed1,888/1,894. Four failures requested useful version3 guidance for legacy imports; the decoder now provides that precise fixed message. Two older fixture observations failed while reading coordination files: a sidecar was temporarily absent during replacement, and a ready-marker read received a sharing violation. The original marker writer already published closed unique temporary files atomically; the exact competing handle causing the sharing violation was not established. Test-only observers now allow rename sharing and retry only absence/sharing/lock observations within existing deadlines. Parent signals also use unique closed temporaries. Actual process joins, CAS assertions and final protected-store checks remain intact; no production storage checks were weakened. The failed run is retained. An earlier compile attempt found a missing namespace import, corrected before final verification.

The 110-case repeat set includes all76 new cases,29 prior snapshot-storage cases and five actual cross-process cases. Test processes use synthetic credentials in marked temporary fixtures. The worker is not distributed. No real browser, account, native Roblox process or production window was opened or controlled.

Final tests finished **2026-11-06 03:37:13.3757619 UTC**. Packaged passes finished **03:37:35.9182019 UTC** and **03:38:25.8272907 UTC**, using .NET10.0.12 and SQLite3.53.4.

- App SHA-256: `F902C2033E1B60510939E75F517EB2709709A06BB5F84A4DEE744F98F6BAE240`.
- Windows SHA-256: `5944ED088BB15C2852E0DD27076BEC4134F19D2AD2481D8BA894CC808CEEE5B0`.
- Core SHA-256: `69F7F58B68ABAEF144674B72CA4F5D63C6A85BDFC61BDFBF0E1353F7839E03CE`.
- Manifest SHA-256: `47D1E0F053590AE2857ADDA357CBC8213494406F789AB859E3F998871D0C06E9`.

[Verification record](../../artifacts/sessions-manager-verification-0.22.json), [full tests](../../artifacts/sessions-manager-tests/manager-core.trx), [repeat1](../../artifacts/sessions-manager-tests/manager-0.22-owned-repeat-1.trx), [repeat2](../../artifacts/sessions-manager-tests/manager-0.22-owned-repeat-2.trx), [failed run](../../artifacts/sessions-manager-tests/manager-0.22-before-fixture-fixes.trx), [first packaged pass](../../artifacts/sessions-distribution-check-0.22-first.json), [repeat package](../../artifacts/sessions-distribution-check/distribution-check.json), [manifest](../../artifacts/sessions-manager-review/preview-manifest.json).

Rule18: no new dependency, capture/input lane, persistent service or per-account worker. Metadata work remains outside the UI thread on the existing serialized owner. Rule20: incremental field/schema migration, verified rollback copy and strict old-version rejection. The fixed review staging folder is reused. Public/Linux/installed Personal applications and real user data remain untouched.

## Remaining live-test threshold

Native account selection/arrival/reconnect, background macros/broadcast, host integration, owned-game acknowledgments, full appearance/mini mode, production UI/DPI/performance and real2/4/8/16-client tests remain unfinished. The source provider remains unavailable. No parity, efficiency-superiority or finished-release claim is made. Continue implementation and notify the owner when an integrated two-account Coloseum test build is actually ready; this update does not meet that threshold.
