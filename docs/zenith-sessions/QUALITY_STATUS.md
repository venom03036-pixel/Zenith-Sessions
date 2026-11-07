# Zenith Sessions — quality status (authoritative completion checklist)

This is the one checklist for the completion, verification and optimization loop started on 2026-11-07. Requirement IDs and their
evidence stay in [REQUIREMENTS-LEDGER.md](REQUIREMENTS-LEDGER.md); this file tracks the defects, gaps and improvements found by the
loop, and their verification. Every row moves only on evidence: a failing test that now passes, a measurement, or a recorded reason.

**Status values:** Open · In progress · Verified (test or measurement cited) · Blocked (what is needed) · Needs decision (owner's call) ·
By design (cited) · Excluded (forbidden technique, cited).

## Starting point (2026-11-07, before the loop's fix lanes)

| Check | Result | Evidence |
|---|---|---|
| Zenith Sessions tests | 4,405 / 4,405 pass (first fully green run) | `artifacts/quality-loop/suite-after-workers.txt` |
| — earlier the same day | 4,379 pass / 13 fail; then 4,399 / 6 after the game-links fix pass | `artifacts/game-links/final-fix-suite.txt` |
| Worker-process tests (vault cross-process ×5, .NET 8 pipe ×1) | Were "environment"; real cause was a test bug (worker apphost copied without its DLL); fixed by `TestWorkerLocator` | `artifacts/quality-loop/workers.txt` |
| MacroSuite.Core.Tests | 759 / 759 | `artifacts/quality-loop/baseline-MacroSuite.Core.Tests.txt` |
| MacroSuite.Infrastructure.Tests | 1,155 pass / 9 fail / 3 skipped (items H1–H4) | `artifacts/quality-loop/baseline-MacroSuite.Infrastructure.Tests.txt` |
| MacroSuite.LicenseIssuer.Tests | 11 / 11 | `artifacts/quality-loop/baseline-MacroSuite.LicenseIssuer.Tests.txt` |
| MacroSuite.SDK.Tests | no tests discovered (item H5) | `artifacts/quality-loop/baseline-MacroSuite.SDK.Tests.txt` |
| Zenith.Sessions.CapabilityHarness.Tests | 34 / 34 | `artifacts/quality-loop/baseline-Zenith.Sessions.CapabilityHarness.Tests.txt` |
| Release build of Zenith.Sessions.App | 0 warnings, 0 errors | build log in the session scratchpad |
| Packaged self-check (`--self-check`) | 42 / 42; release readiness false | same |
| Running app, sample mode, scratch data folder | window in 1,109 ms (median of 3); idle CPU 0.32 % mean / 1.65 % max over 60 s; working set ≈224 MB; private ≈205 MB; 21 threads; ≈762 handles | `artifacts/quality-loop/perf-baseline.json` |

## How the loop finds and fixes work

1. Seven read-only readers mapped the app, Core, Windows, macro host, tests and requirements; a completeness critic named 16 areas
   they missed; 16 more readers mapped those. 228 findings in total.
2. One skeptic per finding tried to refute it against the current code: 95 confirmed, 73 partially confirmed, 35 by design,
   4 refuted, 3 already fixed. Severity was re-graded by effect on a real user.
3. A read-only reverse-engineering pass over the RoALT reference (MIT, used for facts only; no code reused) compared every
   area. Adoptable capabilities are listed below as `R-…` items; RoALT techniques Zenith excludes are listed and never adopted.
4. Each lane is fixed in its own isolated copy with a failing test first, reviewed independently, then merged and re-verified
   on the full suites.

## Out of scope for unattended work (recorded constraints)

Real Roblox sign-ins, live clients, the published experience and Open Cloud keys are never used unattended. Browser-secret
scraping, client injection or modification (including writing the client's settings or FFlag files), singleton-enforcement
workarounds, shared sign-in file locks, credential extraction and anti-cheat or detection evasion are excluded techniques.
Joining private servers stays deferred by the owner's choice.

## Blocked or held (not part of the fix lanes)

| ID | Item | Why it is held | What unblocks it |
|---|---|---|---|
| Z6, Z1702, Z49, Z50 | Macro control IPC, production input transport, "Launch and run", Automation playback | Gated by design until background input isolation and verified Roblox target binding are proven (HANDOFF next work #3–#4) | A live-client isolation run on owned accounts |
| Z1152 | Overview capture sessions with more than four clients | Needs live clients to measure | Live clients |
| Z1412 | Launch forecast calibration | Needs live startup-peak and growth samples | Live clients |
| Z1500 | Owned-experience bridge composition (key store, poller, UI) | Verification needs Open Cloud keys and the published experience | Keys and an owned-account trial |
| Z1756 | 10 GB WebView2 sandbox profile preflight | Environment-sized | A larger sandbox disk |
| Z42 | Thirty duplicated test fakes | Touches files every lane edits | Redundancy pass after the lanes merge |
| — | Native launch providers (Unverified), private-server join, live presence, signed-in server sorts | Need live accounts or an owner decision | Owner decision / live trial |

## Checklist by lane

### L1a-accounts-vault — Accounts, vault, credential cleanup and saved-session checks (16 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| Z1200 | Credential cleanup lets InvalidDataException escape; one malformed deleted-profile vault file stops the library from opening | high | verified finding #1200 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| R-accounts-0 | Check many saved sign-ins at once, on demand, with a per-account result (valid, needs sign-in, or challenged). | medium | RoALT comparison (compare:accounts #0), adoptable=yes | Open | — |
| R-accounts-1 | A network blip, refused connection or HTTP 429 during a check does not throw away a working saved sign-in. | medium | RoALT comparison (compare:accounts #1), adoptable=yes | Open | — |
| Z1202 | Cleanup stops at the first failing record, so later deleted accounts' protected sign-ins are never removed | medium | verified finding #1202 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| Z1204 | One inconsistent vault record makes the whole account page lose its link status | medium | verified finding #1204 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| R-accounts-2 | Adding an account that is already saved refreshes its sign-in instead of failing after the user finished signing in. | low | RoALT comparison (compare:accounts #2), adoptable=yes | Open | — |
| R-accounts-3 | A session cookie that Roblox rotates during any signed-in request is kept, so the saved sign-in does not go stale. | low | RoALT comparison (compare:accounts #3), adoptable=yes | Open | — |
| R-accounts-5 | Moving to another PC keeps group colours, group order, empty groups and favourites, and importing the same file twice does not duplicate profiles. | low | RoALT comparison (compare:accounts #5), adoptable=yes | Open | — |
| Z1203 | A busy vault is shown as 'Link index unavailable — Sign in again' on every row and drops their player bindings | low | verified finding #1203 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| Z1205 | Crash-orphaned atomic-write temp files keep a protected session-cookie copy after deletion or Disconnect | low | verified finding #1205 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| Z1207 | Removing a profile or a saved sign-in pauses the whole queue and cancels another account's active launch without saying so | low | verified finding #1207 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| Z1208 | Profile dialog reports 'could not be saved' when only the refresh after a committed save failed | low | verified finding #1208 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| Z1209 | Account page and cleanup reads take a SQLite write reservation (BEGIN IMMEDIATE) | low | verified finding #1209 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| Z1210 | Remove profile has no in-flight guard; a second click queues a duplicate delete that ends in a false conflict error | low | verified finding #1210 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| Z1211 | Vault repeats directory permission checks per call while holding the cross-process lock | low | verified finding #1211 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| Z27 | A 401 on resolve-link leaves the saved session marked Linked | low | verified finding #27 (map:windows) | Open | — |

### L1b-signin-browsers — Sign-in and inspection browser owners (WebView2), sign-in folders (13 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| Z1201 | Partial sign-in folder removal deletes the marker and lock, so every later sign-in fails until manual cleanup | medium | verified finding #1201 (Area #5: Protected vault and account lifecycle (Protecte | Open | — |
| Z1552 | The sign-in _cleanupFailed latch trips too easily, never clears, and its kept lease breaks the other two browser owners | medium | verified finding #1552 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |
| Z1554 | A partial SignInProfileDirectory.Remove deletes the marker and lock file, blocking all private browsers even after restart | medium | verified finding #1554 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |
| Z1560 | No test drives the real sign-in or probe owner past folder admission, so the latches, cleanup order and lease interplay are untested | medium | verified finding #1560 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |
| R-accounts-13 | Leftover sign-in browser folders are cleaned up at startup. | low | RoALT comparison (compare:accounts #13), adoptable=yes | Open | — |
| Z1550 | A refused inspection or native preparation still downgrades the account's saved sign-in to SignInRequired | low | verified finding #1550 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |
| Z1551 | The sign-in window does not guard Roblox client launches started from a frame | low | verified finding #1551 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |
| Z1553 | Two hand-written WebView2 owners have diverged; the sign-in copy lacks the probe's cleanup protections | low | verified finding #1553 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |
| Z1555 | The 15-minute sign-in deadline ends silently on the Link path, leaving a notice that points to a vanished window | low | verified finding #1555 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |
| Z1556 | Every WebView2 process failure, including recoverable ones, ends the sign-in or preparation | low | verified finding #1556 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |
| Z1557 | A user-cancelled or timed-out inspection is logged as OperationFailed | low | verified finding #1557 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |
| Z1558 | Folder and lease errors are reported as WebView2 or data-folder-permission problems | low | verified finding #1558 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |
| Z1559 | App exit can hang without limit on an inspection or native browser that never reports its exit | low | verified finding #1559 (#12 Sign-in and handoff probe internals (VA-04, CHAT-20) | Open | — |

### L2-launch-queue — Launch queue, recovery, trials and batch enqueue (13 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| Z1250 | Cancelling a bundle or trial enqueue mid-save leaves the launch queue unhealthy until restart | high | verified finding #1250 (Area #6: launch-queue persistence and restart reconcilia | Open | — |
| Z1251 | Restart turns never-started Queued/Blocked requests into reserving "possible clients" that stall every start | medium | verified finding #1251 (Area #6: launch-queue persistence and restart reconcilia | Open | — |
| Z1253 | Fail-closed queue recovery states have no in-app resolution; a crash during any write disables launching on every start | medium | verified finding #1253 (Area #6: launch-queue persistence and restart reconcilia | Open | — |
| R-launch-1 | RoALT adapts its pacing when Roblox refuses: each gap gets ±1 s of jitter, and each refusal doubles the delay up to 60 s. The backoff is forgotten after 10 minutes or once a client gets through. A refused account is retried once,  | low | RoALT comparison (compare:launch #1), adoptable=yes | Open | — |
| Z1252 | Restart never reconciles a recovered receipt against the exact running process; only a false "no client" confirmation releases it | low | verified finding #1252 (Area #6: launch-queue persistence and restart reconcilia | Open | — |
| Z1254 | A capacity-blocked request rewrites launch-queue.json with fsync and rebuilds the queue UI on every monitor tick | low | verified finding #1254 (Area #6: launch-queue persistence and restart reconcilia | Open | — |
| Z1255 | ResumeTrialAsync can overwrite a concurrent Panic's pause (missing the re-check TryResumeAsync has) | low | verified finding #1255 (Area #6: launch-queue persistence and restart reconcilia | Open | — |
| Z1256 | Two enqueue implementations: the App uses per-recipient EnqueueAsync while the fenced one-write EnqueueBatchAsync is unused | low | verified finding #1256 (Area #6: launch-queue persistence and restart reconcilia | Open | — |
| Z1257 | Per-tick launch-evidence refresh copies the whole queue once per candidate row on the UI thread | low | verified finding #1257 (Area #6: launch-queue persistence and restart reconcilia | Open | — |
| Z13 | ResumeTrialAsync can leave the queue unpaused after a concurrent SignalPanic; TryResumeAsync has the guard, the trial path does not | low | verified finding #13 (map:core) | Open | — |
| Z1400 | A request that admission keeps refusing is durably rewritten and republished on every sample | low | verified finding #1400 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |
| Z1403 | Join step 3 capacity advice counts checked accounts, not the bundle that 'Queue bundle' enqueues | low | verified finding #1403 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |
| Z1408 | Overview and footer 'queued' counts leave out capacity-waiting rows that the runner still starts automatically | low | verified finding #1408 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |

### L3-diagnostics — Diagnostics journal, exports and report files (18 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| Z1300 | Export path validator rejects OneDrive-redirected Documents/Desktop, breaking Save report, Save launch evidence and performance import/export on this PC | medium | verified finding #1300 (Diagnostics and the secret-free export path (CAP-14-01,  | Open | — |
| Z1350 | Export path check refuses every OneDrive-backed folder, so Documents/Desktop saves and imports fail on this PC | medium | verified finding #1350 (#8 Diagnostics and the secret-free export path (CAP-14-0 | Open | — |
| R-shell-6 | The support report includes Windows version and build, plus the active performance settings. | low | RoALT comparison (compare:shell #6), adoptable=yes | Open | — |
| R-shell-7 | Failure records say which subsystem failed. | low | RoALT comparison (compare:shell #7), adoptable=yes | Open | — |
| Z1301 | One transient IOException permanently stops manager.jsonl writing for the rest of the session | low | verified finding #1301 (Diagnostics and the secret-free export path (CAP-14-01,  | Open | — |
| Z1302 | AuthorizationLinked diagnostic is recorded for a new link, a saved-sign-in check and a handoff inspection alike | low | verified finding #1302 (Diagnostics and the secret-free export path (CAP-14-01,  | Open | — |
| Z1303 | Three copies of validate/temp-write/replace export have drifted: no retry on a locked old file and no Lock/Panic fence on the diagnostics export | low | verified finding #1303 (Diagnostics and the secret-free export path (CAP-14-01,  | Open | — |
| Z1304 | Native-stage diagnostic reader failures are silently swallowed and shown as 'evicted or never recorded' | low | verified finding #1304 (Diagnostics and the secret-free export path (CAP-14-01,  | Open | — |
| Z1305 | Diagnostics status and the self-refreshing view depend on the telemetry loop and freeze after a startup or monitor failure | low | verified finding #1305 (Diagnostics and the secret-free export path (CAP-14-01,  | Open | — |
| Z1307 | Deferred #10 confirmed: no end-to-end scan of diagnostics outputs for private-server codes | low | verified finding #1307 (Diagnostics and the secret-free export path (CAP-14-01,  | Open | — |
| Z1351 | One transient disk error stops the diagnostics journal for the rest of the session | low | verified finding #1351 (#8 Diagnostics and the secret-free export path (CAP-14-0 | Open | — |
| Z1352 | Diagnostics export ignores Lock/Panic and overwrites the stop notice | low | verified finding #1352 (#8 Diagnostics and the secret-free export path (CAP-14-0 | Open | — |
| Z1353 | Shutdown record and journal flush are skipped if any earlier dispose throws | low | verified finding #1353 (#8 Diagnostics and the secret-free export path (CAP-14-0 | Open | — |
| Z1354 | Diagnostic events label a sign-in check and a handoff inspection as an identity link, and Unlock is never recorded | low | verified finding #1354 (#8 Diagnostics and the secret-free export path (CAP-14-0 | Open | — |
| Z1356 | Same temp-file save code copied three times, with different retry rules and a misleading failure message | low | verified finding #1356 (#8 Diagnostics and the secret-free export path (CAP-14-0 | Open | — |
| Z1357 | Native-stage diagnostics hide read faults and invalid snapshots as 'unavailable' without recording them | low | verified finding #1357 (#8 Diagnostics and the secret-free export path (CAP-14-0 | Open | — |
| Z1359 | No end-to-end test scans the data folder and diagnostics for a private-server or share code (deferred #10) | low | verified finding #1359 (#8 Diagnostics and the secret-free export path (CAP-14-0 | Open | — |
| Z1360 | Diagnostics view copies the 512-event ring on every presentation tick, even when nothing changed | low | verified finding #1360 (#8 Diagnostics and the secret-free export path (CAP-14-0 | Open | — |

### L4-shell-lifecycle — Startup, single instance, exit, shortcuts and shell conveniences (18 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| R-shell-0 | The window's size, position and maximized state are restored at the next start. | low | RoALT comparison (compare:shell #0), adoptable=yes | Open | — |
| R-shell-1 | The saved theme is applied before the first frame, so a non-default palette does not flash. | low | RoALT comparison (compare:shell #1), adoptable=yes | Open | — |
| R-shell-10 | The tray restores on a single click, and the user can choose what the close button does. | low | RoALT comparison (compare:shell #10), adoptable=yes | Open | — |
| R-shell-11 | Optional idle-timeout lock. | low | RoALT comparison (compare:shell #11), adoptable=yes | Open | — |
| R-shell-2 | Notices carry severity, an error stays visible until it is dismissed, and screen readers announce both. | low | RoALT comparison (compare:shell #2), adoptable=yes | Open | — |
| R-shell-3 | A page change is announced and shown in the window title. | low | RoALT comparison (compare:shell #3), adoptable=yes | Open | — |
| R-shell-9 | The user can choose the global panic shortcut. | low | RoALT comparison (compare:shell #9), adoptable=yes | Open | — |
| Z1100 | No process-wide exception handlers: anything that escapes an async-void entry point ends the app with no diagnostic record | low | verified finding #1100 (Startup and composition root: src\Zenith.Sessions.App\Ap | Open | — |
| Z1101 | One unexpected error stops the single-instance restore listener for good, and the stored error then crashes Exit | low | verified finding #1101 (Startup and composition root: src\Zenith.Sessions.App\Ap | Open | — |
| Z1102 | Single-instance ownership means 'created the mutex', not 'acquired it', so a leftover second copy can keep the app from opening | low | verified finding #1102 (Startup and composition root: src\Zenith.Sessions.App\Ap | Open | — |
| Z1103 | Windows sign-out or restart skips RequestExitAsync, so ShellViewModel.DisposeAsync and the final diagnostics never run | low | verified finding #1103 (Startup and composition root: src\Zenith.Sessions.App\Ap | Open | — |
| Z1104 | The single-instance scope and the 'test modes exclude the personal library' guard both compare path text, so aliases get around both | low | verified finding #1104 (Startup and composition root: src\Zenith.Sessions.App\Ap | Open | — |
| Z1105 | EntryPoint does not catch coordinator construction or access-denied restore errors; a second launch can crash with no message | low | verified finding #1105 (Startup and composition root: src\Zenith.Sessions.App\Ap | Open | — |
| Z1106 | Every instance's tray icon reads 'Zenith Sessions · Personal', so tray Panic, Lock and Exit can target the wrong manager | low | verified finding #1106 (Startup and composition root: src\Zenith.Sessions.App\Ap | Open | — |
| Z1107 | Startup and single-instance behaviour have only happy-path tests | low | verified finding #1107 (Startup and composition root: src\Zenith.Sessions.App\Ap | Open | — |
| Z1755 | Lock overlay copy describes a reconnect feature that does not exist | low | verified finding #1755 (#16 Tools and benchmarks: tools\Zenith.Sessions.Capabili | Open | — |
| Z55 | Ctrl+1–7 no longer match the sidebar (Windows was inserted fourth); no shortcut reaches Windows or Join a game; Guide text has drifted | low | verified finding #55 (map:app) | Open | — |
| Z59 | RequestExitAsync has no exception handling, so a failure inside DisposeAsync becomes an unhandled async-void exception on exit and skips the priority-restoration warning | low | verified finding #59 (map:app) | Open | — |

### L5-previews-telemetry — Previews, window tiling, telemetry, pressure and overview (20 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| Z1401 | With a partial GPU counter provider, an established GPU hold can never recover | medium | verified finding #1401 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |
| R-instances-14 | Trim now and periodic working-set trim, with before and after numbers. | low | RoALT comparison (compare:instances #14), adoptable=yes | Open | — |
| Z1150 | Every overview roster change blanks all capturable tiles and shows a false 'Previews are off' message | low | verified finding #1150 (Area #4: the Windows page, tiling and live previews. Fil | Open | — |
| Z1151 | Pinned Sessions previews: the 3rd and 4th large windows can never get a capture source | low | verified finding #1151 (Area #4: the Windows page, tiling and live previews. Fil | Open | — |
| Z1153 | Monitor list is never filled automatically, so 'Tile clients' is disabled on every start | low | verified finding #1153 (Area #4: the Windows page, tiling and live previews. Fil | Open | — |
| Z1154 | Moving a window to a monitor with a different scale is posted once; a client that resizes itself on DPI change is reported as 'size constraints' | low | verified finding #1154 (Area #4: the Windows page, tiling and live previews. Fil | Open | — |
| Z1155 | Tiled clients show gaps and offsets because invisible window borders are not compensated | low | verified finding #1155 (Area #4: the Windows page, tiling and live previews. Fil | Open | — |
| Z1156 | Each preview frame is copied into three new large-object-heap arrays | low | verified finding #1156 (Area #4: the Windows page, tiling and live previews. Fil | Open | — |
| Z1157 | Overview roster is reconciled on the UI thread every second even when previews are off and the page is hidden | low | verified finding #1157 (Area #4: the Windows page, tiling and live previews. Fil | Open | — |
| Z1158 | Invalid layout preferences make 'Tile clients' report a misleading failure and record OperationFailed | low | verified finding #1158 (Area #4: the Windows page, tiling and live previews. Fil | Open | — |
| Z1159 | 'Focus client' button state lags the tile's real focus eligibility, and a stale click does nothing silently | low | verified finding #1159 (Area #4: the Windows page, tiling and live previews. Fil | Open | — |
| Z1160 | Preview provider and cleanup failures are swallowed without recording even the exception type | low | verified finding #1160 (Area #4: the Windows page, tiling and live previews. Fil | Open | — |
| Z1402 | One unidentifiable process row discards every client's telemetry and keeps admission refused | low | verified finding #1402 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |
| Z1404 | Every app start holds admission and Overview previews for about 30 s because the first CPU sample is null | low | verified finding #1404 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |
| Z1405 | Any presentation exception permanently stops the 1 Hz safety sampler, with no restart path | low | verified finding #1405 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |
| Z1406 | The safety sampler runs on UI-thread continuations and awaits a Background dispatcher frame inside its serial section | low | verified finding #1406 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |
| Z1409 | Dead duplicate resource properties recomputed every tick; the policy's expiry setting is disconnected from enforcement | low | verified finding #1409 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |
| Z1410 | Per-tick O(n^2) session reconciliation allocates InstanceKey strings on every comparison | low | verified finding #1410 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |
| Z1411 | Measured history of a client that has ended is retained but cannot be viewed | low | verified finding #1411 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |
| Z1413 | Two user-facing strings are missing a space ('Up to120', 'up to100') | low | verified finding #1413 (Telemetry, capacity and pressure admission (CAP-05/06/07 | Open | — |

### L6a-game-links-app — Join a game: share links, private servers, favorites, recently played, search (20 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| Z1000 | Late game share-link result overwrites the user's newer destination and wipes the Find-box draft | medium | verified finding #1000 (Post-fix evidence (final-fix-suite.txt, final-control.tx | Open | — |
| G10 | Saved private servers show the place ID instead of the game name; sponsored search cards lost their 'Sponsored' label | low | game-links ledger | Open | — |
| G11 | GameCardArtLoader keeps every completed run until its Active getter is read | low | game-links ledger | Open | — |
| G13 | Join same game/server stay enabled on a stale session row (the click-time check refuses it) | low | game-links ledger | Open | — |
| G14 | The same private server can be saved twice | low | game-links ledger | Open | — |
| G8 | Changing the selected account does not cancel a running share-link check; the result is shown under the new selection | low | game-links ledger | Open | — |
| G9 | Ctrl+Z can restore a cleared share link in ShareLinkInput or DestinationInput (IsUndoEnabled not set) | low | game-links ledger | Open | — |
| Z1001 | Panic's cancel of Recently played is undone by the next 1 Hz desktop sample | low | verified finding #1001 (Post-fix evidence (final-fix-suite.txt, final-control.tx | Open | — |
| Z1002 | Recently played is refreshed again for every history row change, partly mid-sample with stale log data | low | verified finding #1002 (Post-fix evidence (final-fix-suite.txt, final-control.tx | Open | — |
| Z1003 | A share link pasted in the Find box is dropped silently while a cancelled check is still unwinding | low | verified finding #1003 (Post-fix evidence (final-fix-suite.txt, final-control.tx | Open | — |
| Z1004 | PrivateServerStore.StoredServer is a positional record whose generated ToString prints the clear code | low | verified finding #1004 (Post-fix evidence (final-fix-suite.txt, final-control.tx | Open | — |
| Z1005 | Store re-implements the private-server code shape rule and reports a failed check as a retryable generic error | low | verified finding #1005 (Post-fix evidence (final-fix-suite.txt, final-control.tx | Open | — |
| Z1306 | Deferred #6 confirmed latent: StoredServer's generated ToString includes the full private-server code | low | verified finding #1306 (Diagnostics and the secret-free export path (CAP-14-01,  | Open | — |
| Z1358 | PrivateServerStore.StoredServer's generated ToString prints the full private-server code (deferred #6) | low | verified finding #1358 (#8 Diagnostics and the secret-free export path (CAP-14-0 | Open | — |
| Z22 | Local two-slot busy gate is surfaced as a Roblox cooldown and never retried, leaving favorites and card art empty | low | verified finding #22 (map:windows) | Open | — |
| Z39 | Today's share-link, private-server, favorites and recently-played wiring has no shell-level integration test | low | verified finding #39 (map:tests) | Open | — |
| Z53 | Minimizing or hiding the window wipes completed search results and card pictures on Find a game | low | verified finding #53 (map:app) | Open | — |
| Z54 | A share link pasted into the Find box is silently discarded if the Private tab is busy (e.g. its saved-list load is still running) | low | verified finding #54 (map:app) | Open | — |
| Z57 | Two parallel 'reported destination → Join page' paths and a leftover 'Use selected' path survive only for tests | low | verified finding #57 (map:app) | Open | — |
| Z58 | AccountRow.Destination hardcodes four place-ID→game-name strings; every other preferred place shows 'Place {id}' | low | verified finding #58 (map:app) | Open | — |

### L6b-public-http — Anonymous Roblox HTTP: rate limits, proxy policy, batched avatars, user lookup (6 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| R-api-11 | RoALT's game info includes visits and whether private servers are allowed, takes 2 sequential stages instead of 3, and is cached. | low | RoALT comparison (compare:api #11), adoptable=yes | Open | — |
| R-api-3 | RoALT reads Roblox's rate-limit headers. It throttles before sending a request that would fail and waits for the real window reset. The anonymous server list allows only 3 requests per 60 s. | low | RoALT comparison (compare:api #3), adoptable=yes | Open | — |
| R-api-4 | RoALT fetches avatar headshots in batches (up to 100 ids per request) and reuses headshot URLs for 6 hours. | low | RoALT comparison (compare:api #4), adoptable=yes | Open | — |
| R-api-5 | RoALT can look up users by username (anonymous, up to 200 names per call). Zenith can look up only by numeric id. | low | RoALT comparison (compare:api #5), adoptable=yes | Open | — |
| R-research-4 | An unknown or invalid place ID gets a clear 'no such game' answer instead of a generic failure. | low | RoALT comparison (compare:research #4), adoptable=yes | Open | — |
| Z28 | Proxy policy differs between the anonymous public directory and every other Roblox client | low | verified finding #28 (map:windows) | Open | — |

### L6c-links-parsers — Destination and share-link parsing, share-link client, display-name rules (9 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| R-api-0 | RoALT resolves share links whose payload carries only universeId by falling back to the universe's root place. Zenith requires both placeId and universeId, yet Roblox's own web code reads only status, universeId and (for private s | medium | RoALT comparison (compare:api #0), adoptable=yes | Open | — |
| R-api-1 | RoALT supports more share-link kinds, including experience invites that point to an exact server (instanceId), profile links and v2 deeplinks. | medium | RoALT comparison (compare:api #1), adoptable=yes | Open | — |
| R-api-10 | RoALT reuses CSRF tokens per account, so POSTs normally take one round trip. | low | RoALT comparison (compare:api #10), adoptable=yes | Open | — |
| R-launch-5 | RoALT's link parser understands far more of what users paste. It accepts web. and m. hosts, a locale path prefix, roblox://experiences/start and the navigation deep links, ro.blox.com AppsFlyer wrappers (unwrapped one level, never | low | RoALT comparison (compare:launch #5), adoptable=yes | Open | — |
| R-research-1 | Share links in the forms Roblox itself produces are recognised: /share-links, long ro.blox.com links carrying the share URL in af_dp, deep_link_value or af_web_dp, roblox://navigation/share_links, and a type in any letter case. | low | RoALT comparison (compare:research #1), adoptable=yes | Open | — |
| R-research-2 | Game URL and deep-link forms users actually copy are accepted: roblox://experiences/start, locale-prefixed /xx/games/ID, the #!/game-instances fragment, and the web.roblox.com host. | low | RoALT comparison (compare:research #2), adoptable=yes | Open | — |
| Z14 | RobloxShareLinkParser re-implements RobloxDestinationParser's host, escape and query validation with weaker query rules | low | verified finding #14 (map:core) | Open | — |
| Z17 | Four copies of the 'no links, codes or credentials in a display name' rule with diverging marker lists; SavedPrivateServer's is the weakest | low | verified finding #17 (map:core) | Open | — |
| Z29 | Six bounded-body readers, five duplicate-key validators and two anonymous-client validators implement the same rules differently | low | verified finding #29 (map:windows) | Open | — |

### L7-logs-processes — Client log correlation, desktop sampling, hang and disconnect detail (9 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| R-instances-1 | Hang detection. RoALT flags clients that stop responding (IsHungAppWindow) and lifts their limits. Zenith cannot tell a frozen client from a healthy one. | medium | RoALT comparison (compare:instances #1), adoptable=yes | Open | — |
| Z21 | Log correlator holder lease (10 s) cannot be kept for 16 bound readers at 4 reads per pass | medium | verified finding #21 (map:windows) | Open | — |
| R-instances-3 | Log reading keeps up with many clients. RoALT reads every bound log to the end each second. Zenith's fixed budget is at most about 64 KB/s across all clients, roughly 4 KB/s each at 16 clients. A chatty client falls behind, and Ze | low | RoALT comparison (compare:instances #3), adoptable=yes | Open | — |
| R-instances-4 | Fuller disconnect detail. RoALT recognises six disconnect markers, keeps the reason, and separates the player's own leave (reasons 285 and 273) from real drops. It also flags a human check. Zenith recognises three markers and keep | low | RoALT comparison (compare:instances #4), adoptable=yes | Open | — |
| R-instances-5 | End cause and crash evidence. RoALT records whether a client ended through a hang, disconnect, crash or memory limit. Its post-mortem lists crash dumps created since launch and Windows Application Error events for that pid. Zenith | low | RoALT comparison (compare:instances #5), adoptable=yes | Open | — |
| R-instances-9 | Cheaper process inventory. RoALT takes at most one process snapshot per poll and opens one limited handle per entry. Zenith takes three full snapshots per sample, one per executable name. It also makes several handle opens and an  | low | RoALT comparison (compare:instances #9), adoptable=yes | Open | — |
| Z24 | One unexpected probe exception kills the log-correlation worker for the rest of the session | low | verified finding #24 (map:windows) | Open | — |
| Z25 | Desktop sampler takes three full process snapshots and one EnumWindows per client every tick | low | verified finding #25 (map:windows) | Open | — |
| Z35 | WindowsClientLogProbe discovery, path safety and process-variant checks have no tests | low | verified finding #35 (map:windows) | Open | — |

### L8-installations-trial — Installation discovery, publisher trust and the native trial (18 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| R-research-13 | Shows whether the installed Roblox client is current (live client version). | low | RoALT comparison (compare:research #13), adoptable=yes | Open | — |
| Z1600 | Panic/Lock trial notice is overwritten by a false 'installation not supported' status | low | verified finding #1600 (Area #13: Installation discovery and executable-trust UI | Open | — |
| Z1601 | Choosing a row in Settings sets the trial provider and pauses the queue, contrary to the Settings text and the ledger | low | verified finding #1601 (Area #13: Installation discovery and executable-trust UI | Open | — |
| Z1602 | Roblox players in a bootstrapper's folder show as 'Roblox', can be publisher-verified, then are rejected without a reason | low | verified finding #1602 (Area #13: Installation discovery and executable-trust UI | Open | — |
| Z1603 | The real registry read (the only source of protocol evidence) never runs in any test or self-check | low | verified finding #1603 (Area #13: Installation discovery and executable-trust UI | Open | — |
| Z1604 | Publisher status stays on 'Waiting for Windows to finish' after a cancelled check ends | low | verified finding #1604 (Area #13: Installation discovery and executable-trust UI | Open | — |
| Z1605 | Length limits differ between layers, so one long path or hint fails the whole scan | low | verified finding #1605 (Area #13: Installation discovery and executable-trust UI | Open | — |
| Z1606 | Native-player path and filename rules are copied in four places and have drifted | low | verified finding #1606 (Area #13: Installation discovery and executable-trust UI | Open | — |
| Z1607 | Computed installation metadata is never shown | low | verified finding #1607 (Area #13: Installation discovery and executable-trust UI | Open | — |
| Z1650 | Panic/Lock with an installation selected replaces the trial-stopped guidance with 'installation not supported' | low | verified finding #1650 (#14 Installation discovery and executable-trust UI (PROD | Open | — |
| Z1651 | Refresh installation or a row click during a native trial cancels it silently and pauses the whole queue | low | verified finding #1651 (#14 Installation discovery and executable-trust UI (PROD | Open | — |
| Z1652 | Settings text says selecting a row chooses no provider, but the selection sets the native trial's executable | low | verified finding #1652 (#14 Installation discovery and executable-trust UI (PROD | Open | — |
| Z1653 | A cancelled publisher check leaves 'Waiting for Windows to finish' showing after Windows has finished | low | verified finding #1653 (#14 Installation discovery and executable-trust UI (PROD | Open | — |
| Z1654 | Real PE version parsing and real HKCU hint reads have never run in any test or self-check | low | verified finding #1654 (#14 Installation discovery and executable-trust UI (PROD | Open | — |
| Z1655 | Version-folder cap of 8 counts files and non-player folders and keeps folders in name order, not newest first | low | verified finding #1655 (#14 Installation discovery and executable-trust UI (PROD | Open | — |
| Z1656 | Paths found through the uninstall registry are labelled 'standard location' | low | verified finding #1656 (#14 Installation discovery and executable-trust UI (PROD | Open | — |
| Z1657 | Issue lines drop product and source; ProductName and TooLarge are never shown | low | verified finding #1657 (#14 Installation discovery and executable-trust UI (PROD | Open | — |
| Z1658 | Three different executable size limits for the same file (256 MB, 512 MB, hard-coded 536870912) | low | verified finding #1658 (#14 Installation discovery and executable-trust UI (PROD | Open | — |

### L9-macro-host — Personal macro host: catalog pipe, plan preparation, hotkeys, failing host tests (14 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| H1 | Personal host: AutoClicker Dispose and StopClicker no longer release held keys (PanicE2ETests x2 fail) | high | baseline MacroSuite.Infrastructure.Tests / SDK tests | Open | — |
| H2 | Personal host: transport hotkey defaults and the settings shortcut bar disagree with their tests (SettingsViewModelTests x4 fail) | medium | baseline MacroSuite.Infrastructure.Tests / SDK tests | Open | — |
| H3 | Personal host: GlobalHotkeyServiceTests.Unregister_AllowsReRegistrationOfCombo gets LocalOnly instead of Success | low | baseline MacroSuite.Infrastructure.Tests / SDK tests | Open | — |
| H4 | Personal host: AIVisionLiveActionTests x2 are rejected by the newer 'expected window required' safety rule | low | baseline MacroSuite.Infrastructure.Tests / SDK tests | Open | — |
| H5 | MacroSuite.SDK.Tests discovers no tests, so the SDK has no running coverage | low | baseline MacroSuite.Infrastructure.Tests / SDK tests | Open | — |
| Z10 | A disabled saved macro is rejected as "exceeds the bounded execution plan limits" | low | verified finding #10 (map:macrohost) | Open | — |
| Z11 | Catch-all swallows in the plan preparer and document pin leave no diagnostic trace | low | verified finding #11 (map:macrohost) | Open | — |
| Z1700 | Any targeted-input fault, including a benign window move or resize, is reported as CleanupFailed and quarantines the whole Personal host | low | verified finding #1700 (Area #15: input ownership layer (src\Zenith.Automation.I | Open | — |
| Z1704 | PauseForEditorFocusAsync re-reads _activeController/_playbackSession across threads and writes IsPaused=true after the run may have ended | low | verified finding #1704 (Area #15: input ownership layer (src\Zenith.Automation.I | Open | — |
| Z1705 | Transport hotkey defaults are hard-coded in six places, the shortcutDefaultsVersion marker is never read, and an unused preset list offers reserved keys | low | verified finding #1705 (Area #15: input ownership layer (src\Zenith.Automation.I | Open | — |
| Z1757 | MacroSuite.Benchmarks --smoke-test always reports PASS; two benchmarks do not measure what they claim | low | verified finding #1757 (#16 Tools and benchmarks: tools\Zenith.Sessions.Capabili | Open | — |
| Z3 | Pipe server accept loop terminates permanently on a non-stop accept exception and nobody observes it | low | verified finding #3 (map:macrohost) | Open | — |
| Z8 | Three display-name validators; the view-model capture check omits the link/credential-marker rule so the failure surfaces late with a generic message | low | verified finding #8 (map:macrohost) | Open | — |
| Z9 | Request deadline is reported to the user as "host is not connected" | low | verified finding #9 (map:macrohost) | Open | — |

### L10-tooling-evidence — Self-check, scripts, owned-bridge script, harness and test infrastructure (29 items)

| ID | Item | Severity | Source | Status | Evidence |
|---|---|---|---|---|---|
| Z1050 | Packaged self-check evidence predates today's fixes and is not the [Dist] report | low | verified finding #1050 (Packaged self-check pipeline used as [Dist] evidence: sr | Open | — |
| Z1051 | Packaged composition stage never runs the game-links surfaces or PrivateServerStore | low | verified finding #1051 (Packaged self-check pipeline used as [Dist] evidence: sr | Open | — |
| Z1052 | Report hash identifies only Zenith.Sessions.App.dll, not the Core/Windows code it tests | low | verified finding #1052 (Packaged self-check pipeline used as [Dist] evidence: sr | Open | — |
| Z1053 | An interrupted bundle stage permanently breaks the reused self-check fixture | low | verified finding #1053 (Packaged self-check pipeline used as [Dist] evidence: sr | Open | — |
| Z1054 | The 15 s deadline does not stop the STA composition stage; a hang produces no stage-attributed report | low | verified finding #1054 (Packaged self-check pipeline used as [Dist] evidence: sr | Open | — |
| Z1055 | Console summary divides by stages reached, not stages planned | low | verified finding #1055 (Packaged self-check pipeline used as [Dist] evidence: sr | Open | — |
| Z1451 | Clock-skew tolerance differs between the Luau script and the C# receipt reader, so valid receipts read as InvalidResponse | low | verified finding #1451 (Area #10 (element 9): owned-experience bridge server scr | Open | — |
| Z1453 | PowerShell scripts fail on Windows PowerShell 5.1, the only PowerShell on this PC | low | verified finding #1453 (Area #10 (element 9): owned-experience bridge server scr | Open | — |
| Z1454 | Luau script never retries a failed MessagingService subscription | low | verified finding #1454 (Area #10 (element 9): owned-experience bridge server scr | Open | — |
| Z1455 | Bridge worker errors are swallowed without any server-side trace | low | verified finding #1455 (Area #10 (element 9): owned-experience bridge server scr | Open | — |
| Z1456 | Luau server checks are outside the suite, and the C# payload is never fed through the script | low | verified finding #1456 (Area #10 (element 9): owned-experience bridge server scr | Open | — |
| Z1457 | Review checkpoint records cite a fixed old test artifact and overwrite a label-less file | low | verified finding #1457 (Area #10 (element 9): owned-experience bridge server scr | Open | — |
| Z1459 | package-shipping.ps1 labels the package with a stale version that never reaches the binary | low | verified finding #1459 (Area #10 (element 9): owned-experience bridge server scr | Open | — |
| Z1501 | Bridge assumes PC and Roblox clocks agree within about 2 s | low | verified finding #1501 (#11: Owned-experience bridge (integrations\roblox\Zenith | Open | — |
| Z1502 | A status check blocks any move for 45 s and the reader reports Pending | low | verified finding #1502 (#11: Owned-experience bridge (integrations\roblox\Zenith | Open | — |
| Z1503 | SubscribeAsync has no pcall or retry; one failure leaves that server deaf | low | verified finding #1503 (#11: Owned-experience bridge (integrations\roblox\Zenith | Open | — |
| Z1505 | No install lifecycle; package-shipping.ps1 builds a stale host ZIP, not a Sessions package | low | verified finding #1505 (#11: Owned-experience bridge (integrations\roblox\Zenith | Open | — |
| Z1508 | Server script hides its errors and dropped requests | low | verified finding #1508 (#11: Owned-experience bridge (integrations\roblox\Zenith | Open | — |
| Z1509 | Luau checks run only by hand and are never tested against the C# side | low | verified finding #1509 (#11: Owned-experience bridge (integrations\roblox\Zenith | Open | — |
| Z1510 | Review checkpoint and manager scripts fail on this PC's PowerShell 5.1 | low | verified finding #1510 (#11: Owned-experience bridge (integrations\roblox\Zenith | Open | — |
| Z1511 | Review records give a stale test time, overwrite each other, and the last two cycles have no After record | low | verified finding #1511 (#11: Owned-experience bridge (integrations\roblox\Zenith | Open | — |
| Z1753 | Harness hard-coded capability assessment is stale and contradicts Core ProviderCapabilities and its own probe evidence | low | verified finding #1753 (#16 Tools and benchmarks: tools\Zenith.Sessions.Capabili | Open | — |
| Z1754 | Harness crashes on InvalidDataException instead of returning the documented exit 2 | low | verified finding #1754 (#16 Tools and benchmarks: tools\Zenith.Sessions.Capabili | Open | — |
| Z1758 | Worker-process tests drop the child's exit code and stderr, which hid the cause of today's six failures | low | verified finding #1758 (#16 Tools and benchmarks: tools\Zenith.Sessions.Capabili | Open | — |
| Z20 | 'Client FPS and graphics: pending' is a Settings search result and a settings paragraph that lead to an explanation, not a control | low | verified finding #20 (map:core) | Open | — |
| Z30 | Five separate 'same process instance' checks with differing access rights and liveness tests | low | verified finding #30 (map:windows) | Open | — |
| Z37 | Worker launch failures discard the exit code and stderr that would have identified the cause | low | verified finding #37 (map:tests) | Open | — |
| Z43 | A unit test writes four review PNGs into the repository's artifacts folder on every run | low | verified finding #43 (map:tests) | Open | — |
| Z44 | Environment-dependent tests are not tagged, so a runner cannot select or exclude them and the ledger cannot say which subset ran | low | verified finding #44 (map:tests) | Open | — |
