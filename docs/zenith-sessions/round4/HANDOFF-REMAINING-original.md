# Zenith Sessions: remaining work (handoff)

Snapshot 2026-11-08. Main is commit 3cfa4c6 in backups\zenith-quality.git (external git dir). Working tree: working-copy.
Read docs\zenith-sessions\QUALITY_STATUS.md and ROALT-COMPARISON.md first.

## Ground rules
- Never run AIVision_CloudProvider_ClicksGreenTargetButton_WhenKeyConfigured (paid cloud AI, owner's key). Exclude it with --filter "FullyQualifiedName!~AIVision_CloudProvider_ClicksGreenTargetButton_WhenKeyConfigured".
- Never use real Roblox sign-ins, live clients, API keys or accounts. Test authenticated paths with fakes.
- No spoofed browser or Roblox-website identity. No writes to client settings or FFlag files. No spending Robux. No RoALT code, text or branding.
- Build with dotnet build/test -m:2 -nr:false. Keep Panic/Lock stop steps and exit behaviour. New features default off. Secrets never in logs, exports, diagnostics or ToString.
- Owner decisions of 2026-11-08: see ownerDecision fields in work-items-r4.json.

## 1. Lane branches to merge or finish
Worktrees: wt\D1 .. wt\D9 (branches lane4/D1, D2, D3, D4, D5, D6a, D6b, D7, D8, D9; in the backups repo). Check: git -C <wt> log --oneline main..HEAD and git status.
Last run: D8, D6a and D6b passed review. D1 was fix-needed and its repair never ran. The rest were cut off by the usage limit.
Merge lanes into an integration worktree, resolve conflicts (likely ShellViewModel.cs, MainWindow.xaml, ShellDirectoryFeatures.cs, docs), then run every suite, the Release build (0 warnings), and the self-check (Zenith.Sessions.App.exe --self-check --data-root <new empty folder>, expect 43/43 or more). Fast-forward main from the working copy.

## 2. Items never attempted (usage limit)
Full specs: working-copy\artifacts\quality-loop\work-items-r4.json (fields proposedChange, ownerDecision, analysisConstraint, alsoCovers).

| ID | Lane | Item |
|---|---|---|
| R-api-13 | D2 | RoALT shows an account dashboard: Robux, Premium, email verified, 2-step methods and social counts. | 
| R-api-16 | D2 | RoALT can make the user's stored accounts friends with each other in one action. | 
| R-api-6 | D2 | RoALT shows each account's Roblox presence (online, in game or in Studio, plus place and gameId), asking with that account's own session. | 
| R-research-6 | D2 | Headshots that are still Pending are asked for once more within the same pass, so new avatars appear without a manual refresh. | 
| O-C7-3 | D3 | The 1 s limit starts before InitializeAsync | 
| R-api-14 | D3 | RoALT lists, edits, renews and buys private servers per account. | 
| R-api-19 | D3 | RoALT has a news tab with app releases, DevForum announcements and the Roblox newsroom. | 
| R-launch-12 | D3 | Through a chosen account, RoALT offers signed-in sorts (Recommended, and fastest by latency) and lists that account's own private servers with cursor  | 
| R-research-10 | D3 | A broad set of recorded, sanitized captures drives the parser tests, including error and limit responses | 
| R-accounts-10 | D4 | Optional master password that locks saved sign-ins at rest and after an idle period. | 
| R-accounts-11 | D4 | Password-sealed, portable backup that includes saved sign-ins. | 
| R-accounts-12 | D4 | A copied account database cannot be read on another PC or Windows account. | 
| M-L1a-6 | D5 | One 401 from the share-link resolve endpoint fences the saved session as SignInRequired without confirming | 
| R-accounts-7 | D5 | Accounts that use Sign in with Google or Apple on Roblox can be added. | 
| R-accounts-8 | D5 | Fewer captchas at sign-in, by reusing one kept sign-in browser profile. | 
| R-research-14 | D7 | Translation completeness gate. | 
| R-shell-16 | D7 | Full localization, including right-to-left languages. | 
| O-C11-1 | D9 | Once an owner stalls, UseAsync abandons it, but its workers keep running | 
| O-C11-2 | D9 | The filter runs after WaitAsync has already timed out | 
| O-C11-3 | D9 | The allowlist says ManagerDiagnostics "waits at most 3 s for its writer and then cancels it" | 
| O-C11-4 | D9 | This file is not in the lane diff | 
| R-research-12 | D9 | Every change is built and tested automatically. | 
| R-shell-14 | D9 | Signed self-update. | 
| R-shell-15 | D9 | Per-user installer and uninstaller that check for WebView2 and ask before deleting data. | 

Deferred from the last run (outcome blocked, never completed): the same list as above. Re-run them.

## 3. Built with fakes, NOT verified (outcome implemented)
Each needs its lane suite run on the merged build. Some need a live trial (section 5).

| ID | Lane | Commit | blockedBy (what the live trial must confirm) |
|---|---|---|---|
| R-api-12 | D7 | 300dbc1 | The live trial must confirm, with a real Discord webhook the owner creates: (1) a real Discord channel accepts the JSON body (content plus allowed_mentions.parse []) and shows the message; (2) a real 429 from Discord carries a Retry-After the client reads (tests used a loopback server's header); (3) |
| R-api-17 | D7 | eb8f3b7 | The live trial must confirm, with the real Discord app running on this PC: (1) that the Discord application the owner registers in the Developer Portal (name it as it should appear, e.g. "Zenith Sessions") accepts Zenith's handshake with its Application ID pasted into Settings → Discord application  |
| R-api-18 | D7 | 854cacb | Live trial must confirm, on a real PC with the guard saved on: (1) switching the active connection's Windows profile from Private to Public (or joining a Public Wi-Fi with internet access) raises the Network List Manager notification and, within a moment, locks the dashboard with the "Public network |
| R-api-7 | D1 | fc2211a | The live trial with the owner's own account and a selected native player must confirm, with Settings → Roblox installation → Native launch mode set to Direct ticket: (1) that auth.roblox.com accepts Zenith's honest headers (User-Agent "ZenithSessions/0.50", no Referer or Origin, RBXAuthenticationNeg |
| R-instances-12 | D6a | 33d5566 | The live trial must confirm, with an owned account and a real client: (1) that a real Roblox client shows its leave prompt on the first WM_CLOSE and leaves on the second one posted 400 ms later (the gap was chosen from RoALT's observed behaviour, not measured here); (2) that a healthy client exits w |
| R-instances-15 | D6b | b8d2d3d | Live trial with a running Roblox client and the option on: the Sessions row must show a number that moves with the client's load and agrees in order of magnitude with Task Manager's per-process GPU column; the real "\GPU Process Memory(*)\Dedicated Usage" instances must parse under the pid_<pid>_lui |
| R-instances-17 | D6b | da5d0dc | live input isolation trial: with the trial's verified transport for Roblox client windows (the held item Z1702, composed as IKeepAwakeInputExecutor) and two owned accounts in running clients, confirm that each tick's F13 reaches only the client it was sent to and no other window; that a client the f |
| R-instances-18 | D6a | 68f4335 | The live trial (an attended session on this PC, accepting the UAC prompt) must confirm: (1) Apply upload limit at, say, 1000 kbit/s ends with "Windows confirmed the upload limit..." and an administrator `Get-NetQosPolicy -PolicyStore ActiveStore` lists ZenithSessionsUploadLimit-RobloxPlayerBeta.exe, |
| R-instances-2 | D6b | 71169c8 | The live trial must confirm, with a real client that stays in one server for more than five minutes: (1) the row keeps "Place · last confirmed N min ago" and the state "Connected · last confirmed N min ago" across a long session, and the marker advances once a minute; (2) Join same game, Join same s |
| R-instances-7 | D6a | 779e529 | The live trial must confirm, with a real RobloxPlayerBeta.exe client: (1) the cap applies through the production adapter against the client's real executable (the executable-name guard only accepts RobloxPlayerBeta.exe; tests exercise the identical path with a cmd.exe helper); (2) a real Roblox clie |
| R-launch-0 | D1 | 4d0d91a | The live trial with the owner's account and a real client must produce the first recordable evidence: a client started by the saved mode (Direct ticket, or the official page) through the selected installation whose own log reports the expected player in the requested place while the exact process is |
| R-launch-16 | D1 | f40106d | The live trial must confirm, with a real account and a real selected RobloxPlayerBeta.exe: (1) a client started with a saved extra set still starts and joins the requested place (Roblox's own request stays first and unmodified; whether the client accepts any particular extra argument is Roblox's beh |
| R-launch-17 | D1 | dbb30a9 | The live trial with the owner's account and an installed bootstrapper must confirm: (1) each bootstrapper (Bloxstrap, Fishstrap, Froststrap, Voidstrap) accepts the roblox-player launch request as its only, first argument and starts RobloxPlayerBeta.exe from it (RoALT does the same, but no real boots |
| R-launch-18 | D1 | e556ad4 | Live trial must confirm, on a PC with a real Roblox install: (1) that Refresh marks the player Roblox itself registered under HKCU\Software\Classes\roblox-player (the version-xxx\RobloxPlayerBeta.exe) as Suggested and not RobloxPlayerLauncher.exe beside it; (2) that on a PC with the Microsoft Store  |
| R-launch-2 | D1 | d959df3 | The live trial must confirm, with the owner's accounts and real clients, that a real client's Connected log line is read and corroborated to the exact started process (PID and creation time) inside the stabilization window, so that the wait actually shortens, and should measure the pacing saved acro |
| R-launch-9 | D8 | 2bfd795 | Live trial on the owner's PC must confirm: (1) https://setup.rbxcdn.com/<clientVersionUpload>-RobloxPlayerInstaller.exe answers HTTP 200 with the current installer (the address shape follows Roblox's own bootstrapper and the RoALT research fixture; no live fetch was made because network access was p |
| R-shell-17 | D8 | fb57bfa | The live trial must confirm, with a real running Roblox client on an owned account, that the client's current log opens with read, write and delete sharing while the client writes it (tests prove the shape with files this test host holds), and that today's client still writes the recognised line sha |
| R-shell-20 | D8 | bbb8ac6 | Live trial must confirm, with a real running Roblox client: (1) that the client's own open log is refused by the cleanup's exclusive-share open (FileShare.Delete only) and so is counted "in use" and never deleted — the tests prove this with a handle the test host holds open with read sharing, as the |
| R-shell-21 | D7 | 52acb59 | Live trial on the owner's PC (no account or client needed): press Schedule shutdown with a short delay and confirm Windows shows its own shutdown notice carrying the Zenith comment and that the countdown on Settings and in the footer agrees with it; press Cancel shutdown and confirm Windows reports  |

## 4. Owner decisions to verify in code
- Z1559: bound the handoff probe's exit wait at 10 s; update HANDOFF-PROBE-VERIFICATION.md and OWNED-NATIVE-PREPARATION-VERIFICATION.md. Not completed (lane D5).
- M-L1a-6: after a 401 from the share-link endpoint, confirm identity before fencing; update GAME-LINKS-DESIGN.md 4.2 and the Security row. Not completed (lane D5).
- Direct launch tickets (R-api-7): document the HANDOFF reversal. Lane D1 unmerged.

## 5. Live trials (need the owner, not unattended)
- Direct ticket mode: endpoint accepts Zenith's headers, ticket format, client joins (R-api-7, R-launch-0).
- Presence and account details with a real account (R-api-6, R-api-13).
- Anti-AFK: needs the background input-isolation trial first (R-instances-17).
- Private server join, automatic recovery, launch-to-log proof with many clients (R-launch-7, R-instances-13, R-instances-0).
- Release: signing keys, release host, installer test on a clean PC (R-shell-14, R-shell-15).

## 6. Review minors still open
Round-3 minors are in QUALITY_STATUS.md ("Round 3 review findings"). Round-4 O- items are in work-items-r4.json; some were done in lanes D6a, D6b, D7, D8, D9.

## 7. Excluded, do not build
- Moving the data folder (R-shell-13): declined by owner.
- Buying or renewing private servers: spends Robux via an unofficial endpoint.
- Windows smaller than Roblox's minimum (R-instances-16): needs a foreign window-style rewrite and global hook.
- Client settings or FFlag writes, singleton workarounds, shared sign-in locks, browser-secret scraping, detection evasion.

## 8. Resume steps
1. Read work-items-r4.json and lanes-r4.json.
2. For each item: write a failing test, make it pass, run the lane suite, commit with the Co-Authored-By trailer.
3. Merge lanes, run all suites, self-check, fast-forward main.
