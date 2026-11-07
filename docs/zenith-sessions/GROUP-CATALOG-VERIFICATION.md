# Independent saved group catalog — development scope

Status: Personal development **0.32.0-preview**, verified within the controlled and limited interactive scope below. Previous0.31 was concrete progress with repaired recovery and executed evidence. The full native/automation/benchmark objective remains intact and unfinished; ReleaseReady and LiveTestReady remain false.

## Incremental design and migration review

Current membership-only group discovery cannot retain empty groups, their colors or their order. Add independent definitions in the same `accounts.db`, with stable IDs, names, nullable #RRGGBB color (null follows the theme), deterministic order, revisions and update time. Catalog order affects lists only; it never changes account priority, primary/additional membership order, queued recipients or authorization.

Use schema5 with a verified schema4 pre-update backup and one migration transaction. Seed every validated legacy membership rather than the bounded first1000 picker rows. Preserve profile fields, revisions, credential references, grants and recovery metadata. Older known schemas still migrate incrementally; future schemas fail without rewriting. Existing old writers must close before rollout because an already-running old process cannot retroactively learn catalog upkeep. This increment changes only development fixtures/stages, not the installed library.

Hold the migration writer reservation while a separate read-only connection creates and verifies the pre-migration backup. Do not run the backup through the connection that already owns the write transaction: SQLite documents that writing through a backup source can return SQLITE_LOCKED. [SQLite backup contract](https://sqlite.org/c3ref/backup_finish.html). No compatibility triggers are added; requiring old writers to close is an explicit rollout prerequisite, not a guarantee that a database version retroactively disables old code.

Profile save/import/bulk-membership paths ensure missing neutral definitions in their existing transaction after revision checks. Deleting accounts or editing membership leaves intentional empty definitions. Rename/remove update catalog and profile memberships atomically with the reviewed preview; previews bind global/catalog source/destination facts, not just matching accounts. An empty existing source may change with zero profile rows. Rename-to-new retains source appearance/order/identity; explicit merge keeps destination appearance/order/identity. Removal materializes a fallback only when needed for primary memberships. Color/order-only edits never revise profiles.

The manager gains paged/searchable group rows, Create empty group, explicit Save color and Move up/down. It retains reviewed rename/removal, merge consent, fixed actions and owned cancellation/close. Shell filters and profile suggestions use catalog order/colors and exact lookup to preserve a selected group outside the bounded picker. Page bounds are not a profile or catalog storage cap. Color supplements readable names and accessible labels.

Current version3 profile export/import carries profiles and membership names only. This increment will label that exclusion clearly; empty definitions, colors and order need a later consistent-snapshot catalog-aware format rather than being falsely claimed portable.

Rule20 risks include schema/version regression, incomplete membership seeding, stale previews, ghost definitions from failed profile writes, premature empty-source no-ops, selection loss beyond a page, changed revision semantics and existing modal close regressions. Tests will use actual SQLite migration/transactions and controlled Shell/production views, retaining failed evidence. Rollback uses the verified pre-schema5 copy and preceding development build; do not downgrade a live updated database. Rule18: paged/bounded readers, existing serialized owners and cancellation, no extra worker/timer, credential access, native dispatch or dependency. UI and controller remain separate from database operations. No release or live-test readiness is implied.

## Implemented paths and findings

`AccountGroupCatalog.cs` defines stable nonsecret definitions and bounded queries. `AccountRepository.Catalog.cs`, `AccountRepository.cs` and `AccountRepository.GroupChanges.cs` implement schema5, catalog/profile transactions and current-schema checks after obtaining writer ownership. A future-version check on opening a connection alone leaves a writer gap; bulk deletion and cleanup completion now also recheck inside their transactions. Existing-process future-schema negatives preserve complete database bytes; they are not a reproduced concurrent schema-change race. Older binaries still must close before adopting schema5.

`ShellGroupCatalogFeatures.cs` uses the existing profile-operation owner and cancellation generation, then refreshes bounded choices. Review exposed a missing production route beyond the first1,000 suggestions: Manage groups now has an explicit **Show accounts** action. It resolves the selected stable ID, pins the exact definition through account publication and rejects a rename/name-reuse redirect. A controlled actual-SQLite/Shell test makes that namespace change between lookup and loading, proves the other accounts never publish and restores the preceding filter. Query-generation guards preserve a newer user filter. No group operation confers launch or credential authority.

`AccountGroupManagerViewModel.cs` and `AccountGroupManagerDialog.cs` retain one owned operation and joined modal close, add explicit100-row search/pages, empty creation, color/order, exact-ID selection, feedback and the account-filter route. Profile/bulk pickers preserve plain membership names through optional bounded catalog snapshots. Empty catalog changes remain reviewable even with zero member rows. Existing destination identity, color and order survive explicit merges.

No new dependency, background polling or timer is introduced. Reads/page caches and import batches are bounded; the catalog itself has no1,000-group storage limit. Rename/removal still traverses the complete library in bounded pages. No comparative CPU/memory/speed result is claimed.

## Retained failures and visual correction

The initial affected run was **242/243**, retained at `artifacts/sessions-manager-tests/manager-0.32-groups-first.trx`. One new presentation fixture called `LoadContent` on an unprepared factory template, which WPF rejects. It now instantiates through a real `ContentPresenter`; no production assertion was removed. The corrected243-case run passed.

The first forced build, full3,079 exact-binary tests and243-case repeats passed, yet an actual sample window exposed unreadable native-white TabControl content/headers and light-gray selected rows. That candidate was rejected. Its app hash was `34FAB5BF3FDA82C17CCBF4AD78143830A7712C2BB5EE36BC74921FC1AC6EFEA8`; reports are retained under `*-pretheme.trx` and `sessions-distribution-check-0.32-pretheme.json`.

Local keyed tab/control/item templates now use the existing theme/selection/focus resources. Both row text elements inherit the selected container foreground, and accessibility names use readable group names instead of a record dump with IDs/revisions. Twenty-four new cases render the actual dialog content under five presets plus a light palette at100%,125%,150% and200% bitmap scales. They check painted surfaces/glyphs, contrast, both selected and normal headers/rows, names and reachable fixed footer. These renders are not actual per-monitor DPI/activation acceptance.

## Final executed evidence

The final forced build reported zero warnings/errors. **3,103/3,103** source tests passed, then **3,103/3,103** against exact staged App/Core/Windows assemblies, followed by **267/267 affected cases twice**. A separate69-case theme/presentation/controller pass also passed. Exact reports, timestamps and hashes are in [the machine record](../../artifacts/sessions-manager-verification-0.32.json), SHA-256 `F2FAD2A63171B9166FC653DF6114DF93F001C4B35AFB846C192DA591834D354B`.

The final packaged self-check passed **36/36 twice**, at `2026-11-06T12:12:15.2632716Z` and `2026-11-06T12:14:00.4146800Z`, in5,092ms and4,599ms respectively, using .NET10.0.12/SQLite3.53.4. These times are isolated check durations, not launcher/client performance benchmarks. The existing independent catalog package case performs create/color/order/remove with exact profile preservation.

Final stage: `artifacts/sessions-manager-review`. All25 manifest entries matched lengths/hashes;26 actual files including the manifest, zero extras and zero matches for the bounded private-material filename pattern. That filename check is not exhaustive content proof.

- App SHA-256: `C121D51F773548938063F7CC9AF7FBFC90097671EAF15C8F17341FF488C67D20`.
- Core: `1D2B8EDB5500FFD1B668A1C3FE4C44C0C0C7BA11E22F681D53DA9B93184D9C72`.
- Windows: `A37CFDA0B318C5900B95EDC4D56CBA01FE3AC2AE966859C78624082F1BB157BF`.
- Manifest: `6CC2E0AA783457F613ECF093DA9B4283E6A8E2F49105C35F7208948E94930489`.

## Limited actual interface check

The final staged app opened with16 explicitly synthetic profiles in the existing isolated `artifacts/sessions-ui-0.27-sample` folder. The observed nominal main/dialog sizes were1320x840/620x740. The Graphite Mint tabs and selected/ordinary rows were readable; the scrollbar thumb reached the bottom with status/Close fixed. Creating `UI check empty 0.32` with `#FFAA00`, moving it up, closing/reopening the manager and choosing Show accounts retained its order/color and displayed a colored empty filter with16 library profiles and zero matches. The sample group was retained; no deletion or real sign-in occurred. Escape closed the dialog and Exit manager completed. Test PIDs3568(pretheme) and42000(final) exited; the older independent PID8856 was untouched.

Ctrl+Tab moved focus to Search/reload rather than switching tabs. This remains a keyboard-shortcut acceptance item; mouse tab selection and Escape do not establish a complete keyboard matrix. No UI latency measurement or full actual DPI/multimonitor run was performed.

## Remaining acceptance and next work

Catalog-aware atomic export/import is deferred; version3 exports correctly disclose that empty definitions, colors and order are excluded. Individual account colors, complete UI/keyboard/DPI/latency acceptance and remaining utility parity still need work. Native account selection/launch/arrival, background input/capture, broadcast, reconnect, host integration and real2/4/8/16-client benchmarks remain unproved or unfinished. No reference software was executed, no real account or native Roblox input/launch was used, no installed edition or published experience changed, and no live-test invitation is issued. Notify the owner for two-account Coloseum testing only after integrated prerequisites are ready.
