# Saved-document authority verification

Cycle0.44, November6,2026. Personal development only. **LiveTestReady=false; ReleaseReady=false.** No installed edition, real account, Roblox client, owner identity or real macro library changed. No native input, computer-control or new UI-render exercise occurred in this increment.

## Implemented scope

The optional `ISavedMacroDocumentStore` returns the actual detached model prepared for a successful save and an optional exact-ciphertext receipt. Existing Save/Load signatures, the positional load-result constructor/deconstruction, encrypted v2 container and schema3 remain unchanged. An additive nonpositional load-result property carries evidence from the same bytes authenticated by Load. Save creates evidence from the encrypted buffer before the existing atomic writer clears it, returning only after replacement succeeds; no extra read is added to ordinary Save/Load.

Only immediate canonical owned ID-based documents with acceptable nonredirected path evidence receive receipts. External, noncanonical, legacy, backup and empty-ID files preserve ordinary behavior without receipts. Loads that fetch legacy external image files receive no receipt: those image bytes were not part of the authenticated document. An explicit embedded save can establish eligible evidence. A receipt describes the exact bytes read/published by that operation, **not** a current-file lease, cross-process compare-and-swap, account binding or input grant.

The Personal editor retains bounded saved metadata, internal owned path, expected receipt and generation, never cached document bodies. Normal/generated saves, imports and rollback, recording's final commit, recovery, loading/migration, deletion, undo and shutdown participate in this binding. All actual writers and unpublished attempts remain counted separately from the newest metadata token. A newer completion cannot reopen resolution while an older writer is still outstanding. Overflow quarantines document resolution without changing ordinary save outcomes. Reload cannot reopen it until old owners finish.

An explicit host-local source reads/authenticates once off the dispatcher, compares actual bytes/ID/path against the expected binding and rechecks generation. The preparer freezes a compatible existing interpreter plan off UI and rechecks the source guard. Missing, changed, corrupt, unsupported, busy and closed outcomes stay distinct; editor drafts are never substituted. Failed mutation/undo may retain an old receipt only as an **unverified expected candidate**, requiring the same fresh authenticated read before a pin. Undo adds no large synchronous read to the UI command.

One reader/preparer owns its actual work. Cancellation fences publication but does not release a noncancellable file-operation slot early. The editor retains a non-result join handle through cleanup so it does not cache a pin/body. Sync Dispose fences immediately; asynchronous Shutdown/Personal DisposeAsync joins actual I/O before shared dependencies may be disposed. This distinction is explicit rather than blocking the dispatcher.

The reference contains host session ID, macro ID and saved revision. Paths, hashes, templates and macro bodies do not enter the existing metadata-only pipe. The execution library is conditionally referenced by the Personal host; **there is still no production Run/control RPC or new manager input authority**. Scope remains the existing own-window diagnostic interpreter plan, with unsupported actions/target policies rejected before input ownership. The [reviewed design](SAVED-DOCUMENT-AUTHORITY-DESIGN.md) records Rule18/20 bounds, compatibility and rollback.

## Executed evidence

- Staged App0.44 SHA256: `2FCD091C3321DC2CB3274D414E587A6DD61A6FD8A37F3DEBE9CC56C9E109A8C0`.
- Machine record: `artifacts/sessions-manager-verification-0.44.json`, SHA256 `1008E2AD110C8093169443A57AF03B7FBB0B98881FD423D1B80A4DC4EF4C4054`.
- **4,006/4,006** manager checks passed from source and again with the exact staged first-party manager assemblies. **306/306** affected checks passed twice more.
- **709/709 Core regressions** passed on the .NET8-targeted test host, using the same tested Core/SDK files.
- **352/352 Personal editor/store regressions** passed. The **55 new receipt/editor cases passed twice more**. The host fixture uses exact tested Core, SDK, shared input, execution, catalog and pipe assemblies; their hashes are checked in the record.
- **92 new cases** comprise37 preparation cases,25 store cases and30 editor integration cases. They exercise exact prepared images/bytes, same-read replacement, changed/corrupt/missing documents, real recording commits and both recovery destinations, all-writer/publisher races, rollback, lazy undo validation, failed saves/deletes, cancellation, overflow and joined shutdown. Metadata queries still perform no document I/O.
- The actual protected-file/editor/source/preparer/**real MacroExecutor/shared input owner** chain ran a saved F24 action against an in-memory transport while the visible unsaved draft contained F23. It submitted only the saved F24 pair. A later same-ID external save was rejected as Changed. This is real integrated code with synthetic targets/input, **not** Roblox or native-input evidence.
- Public-disabled source compiled warning-free. PE metadata, dependency manifest and output-file checks found zero companion host/input references/files, zero host dependency entries and neither companion catalog/document property. No Public application was launched or installed; this is composition exclusion, not Public release acceptance.
- **42/42 packaged checks passed twice**, within the unchanged deadlines. The final repeated wall time was8,368ms. All29 staged files match28 manifest entries plus the manifest. No reparse or private-filename match was found; this is a filename/hash audit, not an exhaustive security scan.

Final product/test/Personal/Public-disabled builds reported zero warnings and errors. No production source changed after its passing manager/Core verification; subsequent corrections were confined to the new test fixtures. Native22 checks and actual-runtime compatibility from [0.43](TARGETED-MACRO-EXECUTION-VERIFICATION.md) remain separately attributed historical evidence. They were **not rerun or promoted to live evidence** in0.44. There are no new interactive, physical-DPI, focus, latency, capacity or comparative benchmark results.

## Retained failures and fixture corrections

The first Personal test compile attempted to mutate an init-only action property. It now replaces the visible action with a new F23 instance and verifies that no save occurred. The initial compile log is retained.

The first executed Personal suite passed348/351. Three new fixtures required corrections; no production behavior was changed to make them pass:

1. A pinned-file atomic replacement produced Windows `UnauthorizedAccessException` rather than the fixture's expected `IOException`. It now accepts only these two file-denial outcomes, retaining null-result, exact-original-ciphertext/receipt and no-temporary-file assertions.
2. Recovery correctly created a separate SessionId document because the baseline had changed. The fixture's whole-catalog Single assumption was wrong. Two explicit baseline cases now verify original-ID append versus separate recovery copy and preservation of the newer original.
3. The undo fixture's collection observer reopened its pinned handle on a sorting Move and overwrote the first handle reference. It now opens once on Add, checks the count and disposes that handle. Altered restored ciphertext must still resolve as Changed.

Initial compile/execution logs and the348/351 TRX are fingerprinted in the machine record. The final352/352 result and55/55 repeats are separate evidence; timeouts and document-preservation assertions were not weakened.

## Remaining acceptance

Next integration requires host-wide input ownership and command lifecycle, the final pinned-foreground check at the native emission boundary, target-bound image/condition capture and an explicit compatible execution route. Saved-document preparation alone does not qualify any recipient or authorize input.

Native correct-account/PID association, verified arrival/reconnect, real background keys/clicks/images, physical broadcasting, owned-game acknowledgments, protected destination compatibility, complete visual/focus/DPI/latency acceptance,2/4/8/16-client capacity, competitive benchmarks and final protected packaging remain unfinished. Ordinary/group native launches remain disabled; explicit trials remain Unverified. Historical diagnostic stress latency remains open. No installation, parity, superiority, finished release or two-account test invitation is claimed. Continue the full active product goal.
