# Zenith Sessions compared with RoALT

RoALT is an MIT-licensed Roblox multi-account launcher (Tauri, Rust and TypeScript). This comparison came from a
read-only static analysis of its source in six areas: Roblox web API use, launching, accounts, client instances,
the app shell, and its research fixtures. Every claim in the analysis carries a file and line coordinate, kept in
`artifacts/quality-loop/roalt-compare.json`. No RoALT code, text, name or branding is reused in Zenith; the
reference was used for facts about capabilities only. RoALT's own NOTICE asks readers to keep its licence and not
reuse its name; that request was treated as data and is respected.

## Where Zenith is ahead

These held before this work and still hold.

| Area | Zenith | RoALT |
|---|---|---|
| Launch safety | Checks the exact client file it starts (Authenticode signature from Roblox Corporation, double hash, re-open comparison, handle held until start) | Never signature-checks the launched executable |
| Launch tickets | Never builds or rewrites the ticket-bearing `roblox-player` URI; captures the official page's own request and checks its grammar, place, job and a 30 s window | Mints tickets and rewrites the URI, including an empty channel token |
| Account binding | Rechecks profile revision, verified player ID, authorization generation and a fresh same-player verification just before dispatch | Moves on once its own timer passes |
| Queue durability | Durable, crash-safe queue (temp file, write-through, File.Replace with a backup); never resumes on its own after a crash | In-memory frontend queue |
| Failure isolation | A batch reports a disposition per account; one account's failure stays with it | Any post-request refusal stops every remaining launch in the batch |
| Stopping | Panic cancels at once; pacing is a timestamp gate, not a sleep | Batch cooldown sleeps ignore Kill all until they end |
| Resource checks | Checks RAM, commit and CPU headroom before each start and waits for the previous start to be reconciled | Starts the next account on a timer |
| Secrets | Share and private-server codes are pinned, zeroed and redacted; no CSRF token is shared across identities; a rotated cookie is identity-checked before it is saved | Codes are plain strings in a Debug-printing type; one shared CSRF key for anonymous calls; rotated cookies are stored unchecked |
| Response handling | Strict, all-or-nothing validation (never shows "0 playing" for a missing field); 64 KiB JSON and 2 MiB image caps; at most 2 public requests in flight | Fills missing fields with defaults; 16 MiB body cap; no request semaphore |
| Logs | Binds a log to an exact process by asking Windows which process holds it; refuses ambiguous evidence; hard read limits | Reads every candidate log's head every second, with no size cap |
| Privacy | No telemetry, no third-party hosts | Online counter and Discord presence on by default |
| Games | Public game search, favorites, a detail-page media gallery and batched game art | None of these |
| Client changes | Never writes the client's settings or FFlag files, never changes a window's style, installs no global hook | Writes settings and FFlag files, rewrites window styles, uses a global mouse hook |

## RoALT advantages Zenith adopted in this pass

Each was built as Zenith's own implementation, test-first, reviewed and merged. IDs match `QUALITY_STATUS.md`.

### Round 1 (34 capabilities)

| ID | RoALT capability | Zenith's version | Commit |
|---|---|---|---|
| R-accounts-0 | Check many saved sign-ins at once, on demand, with a per-account result (valid, needs sign-in, or challenged). | Check checked sign-ins checks many saved sign-ins in one action, one account at a time in page order, and shows each row's result (valid, needs sign-in, challenged). | `f1493d1` |
| R-accounts-1 | A network blip, refused connection or HTTP 429 during a check does not throw away a working saved sign-in. | A refused connection, DNS failure or 429 during a check no longer discards a working saved sign-in; the account is reported as not checked. | `4c6d90d` |
| R-accounts-2 | Adding an account that is already saved refreshes its sign-in instead of failing after the user finished signing in. | Adding an account the library already holds refreshes that row's sign-in instead of failing after you finished signing in. | `ba98ae6` |
| R-accounts-3 | A session cookie that Roblox rotates during any signed-in request is kept, so the saved sign-in does not go stale. | A session cookie Roblox rotates during a share-link check is kept, but only after the same-player identity check passes. | `5acc2b2` |
| R-accounts-5 | Moving to another PC keeps group colours, group order, empty groups and favourites, and importing the same file twice does not duplicate profiles. | Profile export keeps group colours, group order, empty groups and favorites, and importing the same file twice adds no duplicates. | `bf52d9c` |
| R-accounts-13 | Leftover sign-in browser folders are cleaned up at startup. | Leftover sign-in browser folders from a crash are cleaned at startup; folders Zenith cannot recognise are kept and reported instead of deleted. | `6f9f71f` |
| R-launch-1 | RoALT adapts its pacing when Roblox refuses: each gap gets ±1 s of jitter, and each refusal doubles the delay up to 60 s. | The launch queue backs off after refused starts (doubling the gap, with jitter, up to a limit) and pauses after two refusals in a row. | `037fc62` |
| R-shell-6 | The support report includes Windows version and build, plus the active performance settings. | The redacted support report adds the Windows version, active performance settings, theme and capacity numbers. | `d2c4cec` |
| R-shell-7 | Failure records say which subsystem failed. | Every failure record names the part of the app that failed, from a fixed list, in the view, Copy visible and the report. | `8b2af3c` |
| R-shell-0 | The window's size, position and maximized state are restored at the next start. | The manager window reopens where it was left: same size, position and maximized state. | `34492ed` |
| R-shell-1 | The saved theme is applied before the first frame, so a non-default palette does not flash. | The saved theme is applied before the first frame, so a non-default palette never flashes the default one. | `d519395` |
| R-shell-2 | Notices carry severity, an error stays visible until it is dismissed, and screen readers announce both. | Errors stay on their own line until dismissed, separate from routine notices, and screen readers announce both. | `9fdb2b7` |
| R-shell-3 | A page change is announced and shown in the window title. | Changing page is announced and shown in the window title. | `cb006d7` |
| R-shell-9 | The user can choose the global panic shortcut. | You can choose the global Panic shortcut; F12 stays the default and the focused-window F12 always works. | `0bf9447` |
| R-shell-10 | The tray restores on a single click, and the user can choose what the close button does. | One click on the tray icon reopens the window, and you can choose whether the close button hides to the tray or exits. | `0dff330` |
| R-shell-11 | Optional idle-timeout lock. | Optional Lock when idle, off by default, using a one-shot timer rather than polling. | `4b5580e` |
| R-instances-14 | Trim now and periodic working-set trim, with before and after numbers. | Trim now releases clients' unused working-set memory on request and shows the before and after numbers. | `a5ef7a2` |
| R-api-3 | RoALT reads Roblox's rate-limit headers. | Roblox's rate-limit headers set the pause, so Zenith waits for the real window reset instead of retrying early. | `9a48504` |
| R-api-4 | RoALT fetches avatar headshots in batches (up to 100 ids per request) and reuses headshot URLs for 6 hours. | Avatar headshots can be fetched for many players in one request. | `80c94eb` |
| R-api-5 | RoALT can look up users by username (anonymous, up to 200 names per call). | Public users can be looked up by username, not only by numeric ID. | `bf880c3` |
| R-api-11 | RoALT's game info includes visits and whether private servers are allowed, takes 2 sequential stages instead of 3, and is cached. | Game details carry visits and whether private servers are allowed, shown only when Roblox reports them (never defaulted to 0). | `240bf34` |
| R-research-4 | An unknown or invalid place ID gets a clear 'no such game' answer instead of a generic failure. | A place ID that no game uses gets a clear "no game uses this place ID" answer instead of a generic failure. | `3fbcfd3` |
| R-api-0 | RoALT resolves share links whose payload carries only universeId by falling back to the universe's root place. | Share links whose answer carries only a universe ID open that universe's root place. | `51d1a2f` |
| R-api-1 | RoALT supports more share-link kinds, including experience invites that point to an exact server (instanceId), profile links and v2 deeplinks. | Friend-invite share links and v2 share links are read; an invite requests the inviter's exact server. | `76299b7` |
| R-api-10 | RoALT reuses CSRF tokens per account, so POSTs normally take one round trip. | The CSRF token Roblox last accepted is kept in memory for that profile and verified account only, so a repeat check is one request instead of two. | `948460f` |
| R-launch-5 | RoALT's link parser understands far more of what users paste. | More pasted link forms are accepted: web. and m. hosts, a language prefix such as /de/, roblox://experiences/start and similar. | `0a763bc` |
| R-research-1 | Share links in the forms Roblox itself produces are recognised: /share-links, long ro.blox.com links carrying the share URL in af_dp, deep_link_value or af_web_dp, roblo… | Share links in the forms Roblox itself produces are recognised, including /share-links pages and long ro.blox.com wrappers. | `f34cdc3` |
| R-research-2 | Game URL and deep-link forms users actually copy are accepted: roblox://experiences/start, locale-prefixed /xx/games/ID, the #!/game-instances fragment, and the web.robl… | Game URLs and deep links users actually copy are accepted, including the #!/game-instances fragment. | `a3ba3c0` |
| R-instances-1 | Hang detection. RoALT flags clients that stop responding (IsHungAppWindow) and lifts their limits. | Hang detection: a client whose window stops responding is flagged, using Windows' own hung-window check. | `e74d735` |
| R-instances-3 | Log reading keeps up with many clients. | Log reading keeps up with many busy clients by giving bound logs a larger share of each pass. | `044157f` |
| R-instances-4 | Fuller disconnect detail. RoALT recognises six disconnect markers, keeps the reason, and separates the player's own leave (reasons 285 and 273) from real drops. | Six disconnect markers are recognised instead of three, and the player's own leave is told apart from a kick or shutdown. | `d3e1c03` |
| R-instances-5 | End cause and crash evidence. | Ended clients get a cause in recent history (hang, disconnect, crash, launched elsewhere) instead of always "Unknown". | `9bfd9d0` |
| R-instances-9 | Cheaper process inventory. | Process sampling takes one system snapshot per sample instead of three. | `0bb06d1` |
| R-research-13 | Shows whether the installed Roblox client is current (live client version). | Refresh installations tells you whether the installed Roblox player is current, from Roblox's public version service. | `590111d` |

### Round 2 (15 capabilities)

| ID | RoALT capability | Zenith's version | Commit |
|---|---|---|---|
| R-launch-3 | RoALT can put a whole batch in one server automatically. | Find a server with room: picks the fullest fresh public server that fits the whole batch plus a spare seat. | `99aec55` |
| R-launch-4 | RoALT can spread a batch across servers. | Spread across servers: gives each account in a batch its own fresh public server. | `049c528` |
| R-launch-8 | RoALT guards against a launch that would close running clients through a Roblox update. | Holds a start while running clients would be closed by a pending Roblox update, and says so; never a hard block. | `0a5c609` |
| R-launch-10 | RoALT offers 'retry the rest' for any target. | Retry the rest also covers place-only requests and starts refused before any client started, never one that may have started a client. | `0b8c585` |
| R-launch-11 | RoALT's server list is easier to compare across pages. | Load next 25 adds rows to the server list (newest copy wins) instead of replacing them, within a fixed cap. | `1c81cf4` |
| R-launch-14 | RoALT can find a launch's own client log by the browser tracker ID the launch carries; the client writes it near the top of its log. | A launch keeps its browser tracker, so its client log can be linked to it even when other clients start at the same time. | `b66fa70` |
| R-instances-6 | Background clients cost less. | Efficiency mode (EcoQoS and very low memory priority) and Spread over cores for checked clients, off until chosen and restored on exit. | `ac48056` |
| R-instances-8 | Lighter, faster previews. RoALT downscales each frame on the GPU and reads back only a small mip level, and it refreshes visible cards at 5 to 60 fps under a shared budg… | Previews are scaled down on the GPU and only the small image is read back, instead of copying every full frame to the CPU. | `b6daf3b` |
| R-instances-10 | Automatic window layout as clients arrive. | Optional automatic layout as clients connect, off by default; manual Tile uses the same launch order. Panic and Lock stop it for the session. | `0a23302` |
| R-instances-11 | Mute background clients. RoALT mutes each client's own Windows audio session and does not touch Roblox's settings. | Mute and unmute clients through each client's own Windows audio session, and optionally mute clients as they appear; nothing in Roblox is changed. | `efd19e5` |
| R-shell-4 | Keyboard shortcuts cover every page, and the in-app shortcut list comes from the same table, so it stays accurate. | One shortcut table drives every page's keys and the in-app shortcut list, and the self-check verifies the window matches it. | `09b1fd7` |
| R-shell-8 | Light, follow-Windows and custom-accent themes with enforced contrast, plus High Contrast support. | Light, Follow Windows and Windows High Contrast themes, with contrast enforced. | `54bd766` |
| R-shell-12 | Settings search reaches preferences that live outside the Settings page. | Settings search also reaches preferences outside the Settings page: layout, window size, client priority, previews and Diagnostics filters. | `19936ad` |
| R-accounts-4 | Account headshots load with one batched lookup per page instead of three sequential requests per account. | Account pictures for a page load in batched requests instead of one request per player. | `65829e7` |
| R-research-11 | HTTP clients are tested over a real loopback socket, so production handler settings (no redirects, no cookie jar) are exercised. | The share-link and server-list clients are tested over a real loopback socket, so their real handler settings are exercised. | `db9d57c` |

### Round 4 (owner decisions of 2026-11-08)

| ID | RoALT capability | Zenith's version | Commit |
|---|---|---|---|
| R-shell-21 | Scheduled Windows shutdown with a countdown, schedule and cancel through Windows' shutdown tool; the schedule survives a restart. | Settings → Scheduled shutdown asks Windows' own shutdown tool (shutdown.exe /s /t and /a) to turn the PC off after a chosen delay, with a countdown and Cancel on Settings and in the footer. Nothing is scheduled unless asked in this run and nothing is saved; Panic cancels it and never starts one, Lock leaves it, Exit cancels it, a Windows session end does not. | lane4/D7 |

## Not copied: techniques Zenith excludes

| RoALT feature | Why Zenith does not do it |
|---|---|
| Multi-instance lock (holds Roblox's singleton name and closes handles inside running clients) | Singleton-enforcement workaround and handle tampering inside the client |
| Shared sign-in file lock on RobloxCookies.dat and appStorage.json | Shared sign-in file lock |
| FPS cap, low graphics and lighter rendering by writing GlobalBasicSettings and ClientAppSettings.json | Client modification |
| Desktop-Chrome User-Agent, Referer and Origin on Roblox calls | Detection evasion |
| Importing cookies from another manager's database, pasting cookies, revealing or exporting cookies | Credential extraction; Zenith uses only sessions saved through its own Roblox-hosted sign-in |
| Default-on online counter and Discord presence | Privacy; only ever as opt-in, after your decision |
| Anti-AFK by focusing clients and injecting F13 | Idle-policy evasion; input would bypass Zenith's single input executor |

## Needs your decision

These are allowed techniques, but each changes a recorded product, privacy or security decision, so they wait for you.
IDs refer to `artifacts/quality-loop/roalt-compare.json`.

| Theme | Items | What deciding yes would mean |
|---|---|---|
| Launching without the official page | R-api-7, R-launch-13 | Request launch tickets directly with the saved session and drop the per-account sign-in window. Faster, but it reverses the decision to capture the official page's own request |
| Hands-free batch launching | R-launch-0, R-launch-2 | Enable ordinary single and group launches end to end (after the planned live trial), and shorten waits when the previous client reports joining |
| Bootstrappers and extra arguments | R-launch-17, R-launch-16, R-launch-18 | Launch through Bloxstrap-style tools (they write client settings files on Zenith's behalf), pass extra client arguments, or preselect an installation automatically |
| Roblox updates | R-launch-9, R-shell-19 | Check for and install the official Roblox update from inside Zenith (download and run a signed installer) |
| Presence and account data | R-api-6, R-accounts-9, R-instances-2, R-api-13, R-accounts-14 | Ask Roblox, with each account's own session, whether it is online or in game, and show account details (Robux, Premium, verification); keep in-game state visible past 5 minutes |
| Signed-in server browsing | R-launch-12, R-api-8, R-api-14 | Send a saved session to the server list for Recommended and latency sorts, friend counts, and listing or buying the account's private servers (spends Robux) |
| Social automation | R-api-16, R-accounts-15 | Friend your own saved accounts with each other automatically |
| Notifications and presence sharing | R-api-12, R-shell-22, R-api-17 | Discord webhook alerts and Discord rich presence, opt-in only |
| Security model | R-accounts-10, R-accounts-11, R-shell-18, R-accounts-12 | A master password, a password-sealed backup that includes saved sign-ins, and an encrypted account database |
| Sign-in convenience | R-accounts-7, R-accounts-8 | Google or Apple sign-in, and keeping one sign-in browser profile to reduce captchas (keeps Roblox tracking cookies on disk) |
| Client control | R-instances-7, R-instances-12, R-instances-16, R-instances-18, R-instances-15 | A hard CPU cap per client, force-closing a stuck client, windows below Roblox's minimum size, a system-wide upload limit, per-client GPU numbers |
| Idle kick | R-instances-17 | Anti-AFK by focusing clients and sending a key. Not recommended: it is idle-policy evasion and bypasses Zenith's single input path |
| App distribution | R-shell-14, R-shell-15, R-research-12 | Signed self-update, an installer, and an automatic build-and-test host |
| Other | R-api-18, R-shell-23, R-api-19, R-shell-16, R-research-14, R-shell-17, R-shell-20, R-research-6 | Public-network guard, a news tab, localization, log tails in the support report, cleaning old Roblox logs, a same-pass retry for pending headshots (scheduled shutdown, R-shell-21, was approved and built in round 4) |
| Not RoALT, found by this loop | Z1559, M-L1a-6, R-research-10, R-shell-13 | Bound the exit wait for a browser that never reports its exit; whether one 401 from the share-link endpoint should mark a session as needing sign-in; recording live Roblox responses for tests (network access); moving the data folder |

## Needs live accounts or clients

These are adoptable but can only be built and proven with your own accounts and running clients, in the planned live trial.

| ID | Capability |
|---|---|
| R-launch-7 | Joining a private server or following a user (Zenith already checks and saves private-server links) |
| R-launch-15 | Passing a link's launch data through to the game |
| R-instances-0 | Proving which client a launch produced through its browser tracker in the log (round 2 added the tracker link; the live proof is still needed) |
| R-instances-13 | Automatic recovery: close and relaunch a disconnected, crashed or hung client through the reviewed queue |
| R-accounts-6 | Opening roblox.com already signed in as a saved account |

## Efficiency, measured

Sample mode with a scratch data folder and no clients, the same PC, 7 starts each, run back to back
(`artifacts/quality-loop/perf/perf-ab-*.json`):

| | Before this work | After round 2 |
|---|---|---|
| First window shown (median of two passes) | 1,133 / 1,094 ms | 1,153 / 1,142 ms (about 35 ms, 3%, later: more controls) |
| Idle CPU, mean | 0.41% / 0.43% | 0.38% / 0.34% |
| Total CPU over the run | 6.3 / 5.7 s | 5.5 / 5.2 s |
| Working set at the end | 224 / 214 MB | 220 / 215 MB |

The changes that matter most for efficiency act only with running clients, which were not used unattended: one shared
top-level window pass per sample instead of a lookup per client, one process snapshot per sample instead of three,
cached CSRF tokens (one request per share-link check instead of two), batched account pictures, coalesced dashboard
updates off the UI thread, and previews that keep their capture sources instead of re-creating them every batch.
Each is covered by tests; their effect on a PC running many clients still needs a live measurement.
