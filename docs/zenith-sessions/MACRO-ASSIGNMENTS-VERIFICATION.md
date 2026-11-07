# Durable shared macro references — Personal 0.35 verification

Verified November6,2026. **ReleaseReady=false; LiveTestReady=false.** Assignments now work through the actual companion presentation, stable-ID metadata resolution and separate durable storage. They do not start macros or establish a client's account, arrival, capture or input capability. No installed edition, owner library, real account or Roblox session was changed.

## Implemented behavior

The independently reviewed [design](MACRO-ASSIGNMENTS-DESIGN.md) adds one macro-ID reference per saved Account, Group or Bundle identity. Names are bounded last-observed hints. Accounts.db remains schema5; macro formats and encrypted documents are unchanged. The separate schema1 `macro-assignments.db` contains only target kind/ID, macro ID, display names and a generation/revision stamp, with100-row pages,10,000-reference and16MiB operational budgets. These budgets do not cap saved profiles. No credential, document body, host revision token, native handle or active run is stored.

Read/Save/Remove have bounded owned work, cancellation and actual joined disposal. SQLite commits use exact generation/revision comparisons under the cooperative lease and writer transaction; paging rejects either part changing. Existing foreign, future, oversized, redirected or damaged data is preserved and reported. No-op changes retain their stamp. Successful commits return definite success even if cancellation follows; indeterminate commits remain explicit. A safely identified, empty, definitely uncommitted first creation can be removed by its verified native handle after real SQLite/pin cleanup, while replacement/modified/committed/uncertain files remain preserved.

Automation has an assignment panel with explicit account/group/bundle target capture, assignment refresh/paging, stopped-request feedback and confirmed reference-only removal. Assign resolves the chosen target by ID, revalidates the exact received Personal-host page/session/revision/descriptor, then saves with the reviewed assignment stamp. Changes in visible selection cannot redirect it. Lock/Panic fences pending work; shutdown joins assignments before disposing the borrowed host or bundle store. A commit followed by failed refresh says saved-but-refresh-failed, and a cancelled request does not compensate by deleting possibly committed data. Off-page macros remain unreviewed rather than falsely missing. Guide text explains last-known names, references, conflict review and no automatic playback.

## Executed checks and corrections

- Final forced source build and full suite: **3,321/3,321 passed**. Full suite against **all five exact staged first-party DLLs:3,321/3,321 passed**. Final builds had zero warnings/errors.
- **83 focused cases passed twice more** against the exact candidate:39 Core/storage cases,33 assignment-controller cases,7 host-selection review cases and4 actual Shell integration cases. Coverage includes real SQLite reopen/concurrent writers/ABA stamps/paging, interrupted first creation, replacement/modification preservation, deterministic transient journal races, native junction rejection, pinned account/group/bundle references, stale host pages, reentrant cancellation/disposal, committed-result feedback, manager restart and missing same-name replacement targets.
- **39/39 packaged checks twice**, including the new durable assignment round trip, foreign-generation rejection and reference-only removal. Durations were5,097 and4,660ms, not UI or game-performance benchmarks. Production-window composition passed; interactive panel layout, keyboard/DPI and latency acceptance remain unverified.
- **28 staged files**, every manifest length/hash matching, zero extra/reparse entries or private filename-pattern matches. This is a bounded manifest/filename audit, not a comprehensive secret-content audit. The stage cleanup guard now preserves assignment databases/sidecars if they appear in a build folder.

Independent source review found the first-create cancellation defect and prompted the owned-file cleanup repair. A later exact-binary run exposed a real intermittent writer race: pre-lease primary/sidecar observations could see a concurrently appearing/disappearing journal and incorrectly report damaged data. Consistency classification moved under the lease, with three deterministic regressions; the strict one-Applied/one-Conflict assertion was retained. The failing **3,317-pass/1-failure** run is preserved as `manager-0.35-prelease-race.trx`; preceding3,318-pass source evidence is historical.

Initial test compilation needed explicit System.IO imports and corrected assertion forms. A restricted-runner attempt aborted before testing because its local testhost could not connect; its specific process was confirmed gone before retrying outside that runner. A subsequent **79-pass/1-failure** run could not create a file symbolic link without Windows privilege. No Windows policy was changed and no case silently skipped: an actual owned directory-junction fixture now tests native redirection instead. `manager-0.35-assignment-first.trx` and `...assignment-owned.trx` retain these failures. Abrupt-process hot-journal recovery and the precise post-Commit/pre-return cancellation timing have not been independently reproduced; successful-result durability and precommit boundaries were exercised.

## Candidate identity

Fixed reused stage:`artifacts/sessions-manager-review`. Machine artifact:`artifacts/sessions-manager-verification-0.35.json`, SHA-256 **2B952887D1939099C552080F25C0D0D39A037B06DDA777CE39CD4AB30F8C6AFF**. It pins TRX/report identities and all staged files.

- App:`DA78C1DE3C055B39D7ACEBA01CE04DCD0B7DABCB92B5F9CE02504E8C9AB672A4`.
- Core:`CBC73DD9C84EF30EB6A9AC91010AAFE1B51BB1198254AFB52EE333562A7C386C`.
- Windows:`B2A5B7D85F03874585B7DBF13AF1DF01891D3CDE20B6B6D70EA6F814BA30E288`.
- Manifest:`4E9580ADC189A9DD1FC0AD1AF05470AC8463D00F64F5C3A3ED810AC07EA29887`.

Final test artifacts under `artifacts/sessions-manager-tests`: `manager-0.35-source.trx` (15:36:45UTC), `manager-0.35-final-binaries.trx` (15:39:07UTC), `manager-0.35-assignment-repeat1.trx` and `...repeat2.trx`. Packaged reports:`artifacts/sessions-distribution-check-0.35-final.json` and `...repeat.json`.

## Remaining scope and next action

Actual macro execution IPC, per-client engine/image integration, atomic group-membership resolution at run start, qualified Roblox background input, physical broadcasting, verified native account/process binding/arrival/reconnect, sustained2/4/8/16-client load, competitive benchmarks and full UI acceptance remain unfinished. Existing host hooks are unchanged from0.34; no new Personal-editor or Public acceptance is implied.

The owner's sixteen new UI concepts are recorded as CHAT-22 and audited in [the media/windows design](UI-MEDIA-WINDOWS-DESIGN.md). Existing manual public-avatar loading is verified in source as headshots; automatic visible-row loading, the ordered game gallery, all-client in-app Windows overview and broader visual refresh are **not implemented by0.35**. Next implement that media/layout workflow with shared bounds, then continue engine/provider and account integrations. No owner sign-in or two-account live-test invitation is requested yet.
