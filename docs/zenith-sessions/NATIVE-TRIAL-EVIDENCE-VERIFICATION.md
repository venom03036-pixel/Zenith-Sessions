# Native trial evidence, pacing and window composition — 0.27

November 6, 2026. Personal development scope. No real sign-in, native Roblox start, game input, reconnect, installation or reference application execution occurred. Read-only desktop resource/process observations were used. Ordinary and group native launching remain blocked; the configured provider remains **Unverified**. This is not a finished-release or live-test-readiness claim.

## Executed evidence and build identity

The final forced source build passed **2,479/2,479 tests**. The complete suite passed again against the **exact staged App, Core and Windows assemblies**. The affected pacing/evidence/query cases passed **95/95 twice more** against those same staged assemblies. Both final builds reported zero warnings/errors. The final packaged self-check passed **31/31 twice**, including a new full compiled-window composition check, on .NET 10.0.12 and SQLite 3.53.4.

The [verification record](../../artifacts/sessions-manager-verification-0.27.json) records actual report timestamps, hashes and the final build. All 25 manifest entries plus the manifest itself (26 files) matched their lengths/hashes, with zero extra files and zero matches against its explicitly recorded private/test filename patterns. This bounded filename check is not a proof about all binary contents.

Final assembly SHA-256:

- App: `D550E1FB9F82370EA3A3D03014699EB132EB60998EF693F00159AA00EB652D8C`.
- Core: `7E3022EEB0828437612F09197CCD427FE4D7204EAA94763F7B8DFAFD8F5749F7`.
- Windows: `D0817D46BCE8A6D54BCDC29F892B0840C8DBC6369298406BBBCAB9CE5AE0DF66`.
- Manifest: `324CC7DB8D4256C67A9A37CD9C9B6B9B7754FE61F861A1B44D4EAC3BAC468790`.

Fixtures use synthetic accounts, process receipts, browser/publisher/start callbacks and log facts. Temporary SQLite/DPAPI and export files are real, isolated operations. They do not establish native account identity or destination arrival.

## Monotonic launch pacing

The existing launch coordinator now exposes one immutable pacing snapshot, using its existing time provider's monotonic timestamps. No timer, worker or persisted permission is added. An unverified process-start receipt arms a hold for the longer of that start's captured stabilization/stagger settings. A settings edit cannot shorten an active hold. The verified-provider path retains its existing stabilization and later spacing behavior.

A backward/failed timing observation permanently holds this coordinator instance. The final preparation/dispatch boundary checks pacing again, including when a provider is waiting to emit. Ready means that this one delay elapsed; it does not grant resource admission, authorization, queue resume or account/arrival verification. Panic, existing unresolved-start barriers, final capacity checks and owned cleanup remain in effect. Startup-peak/growth calibration and real client pacing/performance remain unverified.

## Pinned requested versus reported evidence

`NativeTrialEvidence.Project` is a bounded pure projection (1,101 queue operations, 4,096 process observations, 128 evidence snapshots). It retains the requested player/place/server and original receipt independently of UI selection. Exact PID/start presence requires a fresh complete inventory. Missing, reused, duplicated, stale, partial or conflicting instances cannot become current account evidence.

Current log reports additionally require original fact times, eligible correlation/lease, unambiguous stream identity and the observed process. Teleport, disconnect and expired evidence clear current destination claims; separately timestamped last-known facts remain labeled. Even a reported match has `CanAuthorizeAccountAction`, `ArrivalVerified` and `CanRouteInput` fixed false. Existing log-reader/correlation provenance is not upgraded to writer/account proof.

Automation adds at most 32 selectable request rows and a requested/reported panel. It refreshes on existing queue/monitor presentation paths, with no row timers. Show process selects the exact current Sessions row; it sends no input and does not focus/close a game. Lock clears the selected details immediately.

Copy/Save evidence is explicit. The report uses a fixed typed allowlist of IDs, original timing, queue problem and pacing. It excludes aliases, usernames, notes, credentials, launch URLs/tickets, installation paths and raw logs/errors. File exports are bounded, tracked, cancellation/stop-generation fenced and atomically committed outside application/data roots. A second cancellation check after final path validation prevents a stopped export from committing. Actual temporary-file tests cover failed replacement, unchanged prior bytes, explicit retry, no leftover temporary file and refusal to overwrite the owned settings file. No upload occurs.

## Reproduced failures and corrections

- A first pacing regression fixture advanced beyond the independent 90-second startup timeout while testing a 20-second hold. Its advancement was corrected to stay within that other deadline; zero-emission and terminal-state assertions were retained. This was a fixture correction, not evidence of a production timing fix.
- A complete exact-binary repeat exposed two UI races: a delayed filter request could supersede a newer explicit refresh, and a refresh could restore a selection read before later asynchronous lookups. Query generations are now reserved when intent changes, obsolete delayed work skips I/O, and the selected ID is read immediately before publication. Three controlled-gate regressions cover both group-case variants and the final selection lookup. Ordinary new queue entries no longer trigger unnecessary account reloads; only an existing operation's changed authorization generation requests that reload.
- The first interactive preview failed with `XamlParseException`. Two new trial panels referenced nonexistent `Card` instead of the established `Panel` style. The full window therefore could not load despite passing controller and partial-template tests. Both references were corrected. The new packaged check loads actual shipped BAML, application resources, the real Shell binding context and baseline layout without showing a window, tray or global shortcuts.
- The initial layout diagnostic attempt was ended after hanging and is not passing evidence. A second attempt's retained managed stack showed ordinary `App.OnStartup` opening its error dialog in the check's dispatcher. Self-check now explicitly suppresses ordinary visible startup, tracks its STA operation/cleanup and completes normally. The retained failed composition report identifies `StaticResourceExtension.ProvideValueInternal`. The final packaged check ran twice without that dialog.

Earlier failing TRX reports, the failed composition report and the isolated diagnostic stack are retained and linked from the verification JSON. Microsoft's `dotnet-stack` 9.0.661903 was installed only in the task's diagnostic-tools artifact folder to inspect that owned diagnostic process. It is not an application dependency or a redistributed component.

## Limited interactive view check

The final staged app opened with `--sample-profiles` and an explicit isolated data root, containing 16 synthetic profiles. At an observed 1320 × 840 capture size, Overview, compact account rows without selection, Automation top/trial/evidence sections, Settings top and Guide top rendered. Sidebar navigation, one scrollbar-thumb drag, the … menu and Exit manager were exercised. The preview exited normally. Global shortcuts, sign-in, native trials, previews and real client controls were not invoked. [Observed scope](../../artifacts/sessions-ui-verification-0.27.json).

This is not every page/dialog, all themes, mixed-monitor/DPI, populated launch evidence, timing/scroll stress or focus acceptance. Follow-up polish includes the empty account inspector, dense technical explanations and an empty evidence selector. No comparative performance or absence-of-lag claim follows from these few interactions.

## Compatibility, ownership and next work

Macro documents, queue writer version 6, protected account formats and installed editions are unchanged. The new evidence/pacing state is runtime-only; explicit exports are separate files. Existing source/prompt fingerprints still match the requirements ledger. This increment adds no production dependency, capture loop, per-account worker or automatic restart.

Rule 18 review: bounded projection/rows/export, shared presentation cadence, original freshness, explicit cleanup/cancellation, no secret-bearing logs and no new privilege are covered by the described tests. Full allocation/latency and native compatibility measurements remain outstanding. Rollback remains the previous staged development code; personal libraries and owner identities were not migrated or installed.

Next work must address remaining UI states and complete native account/process/arrival and provider integration, then validate background capture/input and correct-account reconnect. The integrated two-account Coloseum invitation has **not** been issued. Sixteen-client operation, host connection, owned-game bridge, complete utility parity, DPI/soak/competitive benchmarks and final protected delivery remain unfinished.

## Backend 0.53 log coverage

The earlier worker filled its content-reader slots with the newest candidate paths. A log outside that initial slice never got a holder query, and a later discovery could discard an active reader and its cursor. The worker now rotates through bounded candidate metadata separately from its content readers. Active readers and unexplored files share the existing pass, byte and deadline limits. An attached reader outside the discovery window remains under the existing physical-file, process and lease checks. The default permits 64 discovered entries plus at most 16 attached entries and 16 content readers. There is still one worker and no extra polling timer.

The worker indexes exact process-instance claims once per publication. A second file claiming the same process makes the relationship ambiguous even if it could not get a content reader. A unique match waiting for a slot reports the budget limit. Readers are released when their process leaves the inventory. A valid read now checks process identity twice, once before and once after reading, instead of repeating a check immediately after a successful holder query.

- **101 focused tests passed twice** with zero build warnings or errors. Four new coverage cases and the native validation-count assertion failed against the earlier staged Windows assembly. The final runs use the current Windows assembly.
- A controlled 40-file fixture resolved 16 separate player and place values within 24 seconds on its simulated clock and made at most four holder queries per pass. This verifies scheduling and data isolation for that workload.
- The [verification record](../../artifacts/sessions-tracking-verification-0.53.json) has SHA-256 `73AE2D652F07551E8C64B841FA8A40FD9723017F153EE4C91BE7BC047CDE576B`. The staged manager remains **0.50**. No real Roblox session or UI was exercised.

The scan covers the bounded candidate list returned by discovery. A file-use match remains diagnostic, and a partially read log can have newer transitions beyond its read cursor. Verified native account, exact arrival and live capacity acceptance remain open.

## Backend 0.54 log tail coverage

The reader now numbers successful reads within its ownership, and the correlator records the last fact dropped from its bounded replay window. The reducer tracks those values alongside the byte and complete-line offsets. It rejects earlier batches, uses only validated coverage, and keeps parsing its existing session state while a log tail is unread or its last line is incomplete. Public snapshots withhold current player and destination during that period. Historical values retain their original times.

A missed replay window clears the affected current facts so a dropped disconnect cannot leave an old server active. A later join can rebuild the current destination. An unpaired connection marker does not establish that a client is in game. A conflicting player ID on the same bound stream still invalidates the relationship. All checks reuse the existing reader, replay limits and worker schedule. There is no additional file read, polling loop, dependency or persisted state.

Three temporary-file tests reproduced stale server links on the **0.53** backend. They cover unread bytes, a partially written disconnect, and a missed disconnect that fell out of the bounded replay window. The same tests passed with **0.54**, including recovery to a different place and server after a new complete join. The integration test exercised the existing link projection directly. **122 focused checks passed twice**, with a warning-free build. The [verification record](../../artifacts/sessions-tracking-verification-0.54.json) has SHA-256 `7084707D9F4AE6A127D06C3960E47B60351194E8B4A35F0D28FBA2C219E2BBFC`.

This change applies once a reader has observed the incomplete tail. Events written after its last read are subject to the existing scan interval. The staged preview remains **0.50**. Real Roblox identity, arrival, reconnect and capacity were outside these controlled tests.
