# Saved macro host link — Personal 0.34 verification

Verified November 6, 2026. This increment connects Sessions to the Personal editor's actual saved-macro metadata through a bounded local pipe. It does not execute macros, select a Roblox account, prove background input, or complete the companion release. **ReleaseReady=false; LiveTestReady=false.** No installed edition or real account/session was changed.

## Implementation and review

The [incremental design](MACRO-HOST-LINK-DESIGN.md) received independent review before implementation under Constitution Rule20. The new shared Contracts and Windows libraries target .NET8 so the existing Personal editor and .NET10 companion can communicate without upgrading the installed host or adding NuGet dependencies. The Personal build compiles in the publisher and endpoint; builds with Personal features disabled compile them out.

The editor captures immutable display metadata before each encrypted save and publishes it only after that save succeeds. Opaque revisions and session/load/save generations fence late callbacks, deletion, reset, newer saves and imports. Dirty drafts and failed saves cannot replace the last saved descriptor. Library loading stays unavailable until the complete bounded snapshot is valid; damaged/overflowing metadata cannot appear as a healthy partial or empty library. Optional publisher/pipe errors cannot change the underlying document-save result. Querying the publisher never opens, decrypts or serializes macro documents.

The pipe uses CurrentUserOnly, one retained FirstPipeInstance server, one active request, bounded deadlines, strict framed JSON and exact generation/cursor validation. It rejects malformed UTF8, duplicate/unknown fields, impossible pages and mismatched request/product/version identities. Shutdown joins actual work rather than abandoning a slow source. This is a current-Windows-user boundary, not protection from malware already controlling that user. Macro bodies, templates, credentials and executable commands do not cross this metadata protocol.

Automation now has an explicit Refresh/Stop request saved-macro card, immutable paged rows, separate host/receive status, accessible row labels and collapsed technical references. Failed/cancelled/changed connections retain the last page as stale without renewing its timestamp. No polling or automatic host launch was added. Reentrant Stop/Lock/disposal from bindings sees the owned request before client work can start, and disposal joins it. Sample mode prevents an actual host connection unless an owned fixture client is explicitly supplied. The guide explains these limits. There is no success-looking Run button for unavailable background execution.

Review corrected an import token invalidated by selection-triggered auto-save, and two request-publication/reentrant-cancellation races. The publisher records only actual saved values; save-result isolation remains intact. These are controlled source/controller findings, not bugs observed in live Roblox sessions.

## Executed verification

- Forced source build and suite: **3,238/3,238 passed**, zero final warnings/errors. After improving the fixture's runtime reporting, a warning-free test rebuild and the full suite against **all five exact staged first-party DLLs** also passed **3,238/3,238**.
- Catalog/contracts/real owned-pipe/presentation cases: **96/96**, then **96/96 twice against those exact DLLs**. They exercise bounds, generations, immutable saved snapshots, framing, broken connections, cancellation, joined shutdown, stale pages, reentrant bindings and metadata-only behavior.
- The cross-process fixture reports `Environment.Version.Major`, and the .NET10-targeted test client verified **actual .NET8** worker operation over its own GUID pipe in the full final suite and both focused repeats. The earlier literal marker was replaced because a fixed string was not runtime-version evidence. This worker cannot serve the production endpoint or read a user library and is excluded from the manager stage.
- Actual Personal editor/view-model publication cases: **16/16 passed**, then **16/16 again with the exact staged shared Contracts/Windows DLLs**. The broader reviewed editor class passed **264/264**. Fixtures use their own temporary encrypted documents, fake input/capture, and tracked STA dispatchers. Some broader editor checks use owned hidden native windows/read-only desktop observations; this is not an interactive foreground or real-account acceptance run.
- Personal-disabled build: zero warnings/errors, **zero catalog dependency entries, catalog DLLs, assembly references or catalog properties**. Metadata was inspected without executing that app. This is a conditional-build exclusion check, not full Public-edition release acceptance.
- Packaged candidate: **38/38 checks twice**, including an actual isolated CurrentUser pipe and generation-refresh round trip. Recorded totals were 5,371 and 5,312 ms; these are self-check durations, not UI-latency or game-performance benchmarks. Production-window composition passed; no new interactive panel, keyboard, DPI or foreground acceptance is claimed.
- Manifest: **27 entries plus manifest = 28 files**. Every length/hash matched, with zero extra files, reparse entries or private filename-pattern matches. This bounded manifest/filename audit is not a comprehensive secret-content audit.

Retained initial evidence includes a zero-test filter error (`personal-0.34-catalog-first.trx`) and an 11-pass/5-failure fixture run (`personal-0.34-catalog-actual.trx`). The filter used the test-file name rather than its partial class/method names. The failures exposed fixture continuations leaving their WPF dispatcher and undo tests allowing subsequent auto-save to overwrite the assertion baseline. Fixtures were corrected with owned STA execution and deliberate file-access denial while keeping actual save/restore assertions. No production workaround, live gate or assertion removal was used to make them pass. Initial compilation also exposed missing namespace imports and an unchecked test read; final builds are warning-free.

## Reproducible identity and evidence

Fixed reused stage: `artifacts/sessions-manager-review`. Verification artifact: `artifacts/sessions-manager-verification-0.34.json`, SHA-256 **5DBCA7DA6A0CC685984177A32620065465EC4F25732321B2EA11F5EA711DEFD3**. It pins file identities, every final TRX, both packaged reports, Personal host test binary and the separate conditional-build audit.

- App DLL: `119C35B9112D97BE8D990511F1D94DA9081B6400B5065D268F48F064391C1AF7`.
- Core DLL: `5680D50CBE5F7A195C20E4DB3C3A39051DE9D2D712B058BAEB05024F30534024`.
- Windows DLL: `A37CFDA0B318C5900B95EDC4D56CBA01FE3AC2AE966859C78624082F1BB157BF`.
- MacroHost.Contracts DLL: `31259A6004D8F2BC0FB1CFFC60C769EF9CC354156EE16E769AC713361DF88218`.
- MacroHost.Windows DLL: `FCFB29ABEFE5F599149CA306BD112A69316FF10884EBED969BCFCA972BF477D2`.
- Manifest: `B8FA8497D342ACDCC69F7A2D0E48484157AB25AF9A7802C44FD074C8290FA0DE`.
- Personal test host DLL: `7F34E0DA489C0B5BED463B06720529C757EB0FEED7743D2D194505827638B518`.

Source TRX: `manager-0.34-source.trx`, finished 14:15:49 UTC. Exact-binary TRX: `manager-0.34-final-binaries.trx`, finished 14:23:26 UTC. Focused repeats: `manager-0.34-host-repeat1.trx` and `...repeat2.trx`. Personal results: `personal-0.34-catalog-ui.trx`, `personal-0.34-catalog-final-shared.trx`, `personal-0.34-editor-regression.trx`. All are under `artifacts/sessions-manager-tests`. Packaged reports: `artifacts/sessions-distribution-check-0.34-final.json` and `...repeat.json`. Conditional-build audit: `artifacts/macro-host-public-guard-0.34.json`.

## Remaining work and next gate

The Personal test host remains **2.5.12/.NET8**; the required supported-host upgrade and installation are still pending. Metadata IPC is now real, but macro-control IPC, persisted shared assignments, engine adapters, host-wide input arbitration, target-aware image conditions and physical broadcasting remain unimplemented or incomplete. The cooperating-window target owner from 0.33 is still a diagnostic transport, not a qualified Roblox background provider. Its Core/Windows DLLs are unchanged in this candidate; its historical native checks are not relabelled as new live tests.

Native account/process association, verified arrival, unattended correct-account reconnect, two-account live isolation, 2/4/8/16 sustained load, full utility parity, interactive panel/DPI/focus/performance acceptance and competitive benchmarks remain outstanding. Test counts cannot establish any of those capabilities. The owner does not need to sign in or prepare accounts yet. Continue the engine/provider and account-observation integrations, then issue a concrete two-account Coloseum test invitation when its prerequisites are actually met.
