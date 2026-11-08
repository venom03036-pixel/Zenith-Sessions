# Round-4 attended trials and decisions

No live acceptance is claimed. Execute trials only after the final offline gate and explicit attendance. Existing production gates remain in force.

## R-api-7 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

In an attended isolated standard-player trial, save Direct ticket mode. Confirm GET /v1/client-assertion/ has no request body and POST /v1/authentication-ticket/ sends the returned clientAssertion (or the supported empty JSON object {} case), with honest Zenith headers and at most one CSRF retry. Confirm the one-use ticket launches the selected identity at the reviewed public place/exact server, and rotated sessions are identity-checked before persistence. Repeat cancellation/Lock/Panic and refused/challenged responses; no browser-header spoofing, public fallback or unreviewed retry is permitted. This mode needs its own executable-bound qualification; Official page stays the default.

## R-launch-0 — Built, needs live trial

Qualification binds the actual captured launch mode, product, executable bytes/path and held file identity. Old product-only records cannot enable launches; replacing or changing the executable requires a new attended trial.

The live trial with the owner's account and a real client must produce the first recordable evidence: a client started by the saved mode (Direct ticket, or the official page) through the selected installation whose own log reports the expected player in the requested place while the exact process is observed, so that "Record this trial as verified" becomes available. The owner then records it, and the ordinary path (Queue selected, Queue checked accounts, bundles) runs against real clients for the first time; the trial must confirm that each queued account's client actually joins as that account and that one-at-a-time starts with the current pacing are acceptable at 2, then 16 clients. Multi-client compatibility still needs a supported approach (no singleton workaround was built). The recorded match is the evidence the owner accepts, not proof of any later client's account or arrival. Replace/update the selected executable, including a same-path replacement; ordinary launching must refuse until a new trial is recorded. Change saved mode during review, release checking and preparation, including a change away and back; stale review must never qualify the later mode. Scale only if the previous attended stage succeeds and the OS/client supports simultaneous instances without workarounds.

## R-launch-2 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

The live trial must confirm, with the owner's accounts and real clients, that a real client's Connected log line is read and corroborated to the exact started process (PID and creation time) inside the stabilization window, so that the wait actually shortens, and should measure the pacing saved across the planned sixteen-account batch; every test here used synthetic receipts and log snapshots.

## R-launch-16 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

The live trial must confirm, with a real account and a real selected RobloxPlayerBeta.exe: (1) a client started with a saved extra set still starts and joins the requested place (Roblox's own request stays first and unmodified; whether the client accepts any particular extra argument is Roblox's behaviour, not Zenith's); (2) the trial review line "Extra client arguments (this account)/(default): …" matches what the process receives (for example by inspecting the started process's command line); (3) an account's own set replaces the default for that account and a blank one falls back to the default; (4) no argument text appears in Diagnostics, the event log or a metadata export after a trial.

## R-launch-17 — Excluded

Bootstrapper inspection remains; launching through a bootstrapper is deferred until exact child-player association exists.

Owner planning response: Standard Roblox player first; bootstrappers follow. No bootstrapper trial or ordinary qualification is enabled in this build.

## R-launch-18 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

Live trial must confirm, on a PC with a real Roblox install: (1) that Refresh marks the player Roblox itself registered under HKCU\Software\Classes\roblox-player (the version-xxx\RobloxPlayerBeta.exe) as Suggested and not RobloxPlayerLauncher.exe beside it; (2) that on a PC with the Microsoft Store Roblox the WindowsApps player is listed but never suggested (the store registration may also arrive as an AppX alias the parser rejects as an invalid hint, which also yields no suggestion); (3) that a leftover older Versions folder leaves the registered player suggested; and (4) that a trial started from the suggested row still runs the Authenticode publisher check and refuses an unsigned file, exactly as for a clicked row. All of this was proven only with fake inventory sources and in-process shells.

## R-api-6 — Built, needs live trial

Explicit account-owned observations/actions use durable authorization fencing, identity confirmation, bounded requests and private-data cleanup.

With two disposable owner-controlled accounts, explicitly refresh their presence while each is offline, online and in a game. Confirm each request uses only that account and reported player/place/job values match the website. Change selection, disconnect, Lock and Panic during a delayed refresh; private observations must clear and late results must not return.

## R-api-13 — Built, needs live trial

Explicit account-owned observations/actions use durable authorization fencing, identity confirmation, bounded requests and private-data cleanup.

Use an owner-controlled test account to refresh private details. Compare balance, Premium, verified-email boolean, two-step method flags and social counts with Roblox. Confirm unsupported fields remain Unknown, no email address is exposed, and selection/disconnect/Lock/Panic clear the results. Exercise cookie rotation and a refused/challenged request without automatic retries.

## R-api-16 — Built, needs live trial

Explicit account-owned observations/actions use durable authorization fencing, identity confirmation, bounded requests and private-data cleanup.

Use two disposable owner-controlled accounts. Review the single friendship pair, confirm it, and verify the final relationship on Roblox. Repeat with existing friends, a pending request, cancellation and an account restriction/challenge; no unreviewed pair or automatic retry may run. Cancel may not undo an already processed remote request.

## R-launch-12 — Built, needs live trial

Signed-in Recommended/BestLatency pages, owned private-server listing and reviewed name-only rename are built. Purchasing, renewal and access mutation are excluded.

Select a disposable owner-controlled account and a real place. Compare Recommended and reported-latency results and cursor pages with Roblox, including missing ping and repeated/expired cursors. List only owned private servers. Review and rename one owned server, then fetch again to confirm; no purchase, renewal or permission change should occur. Exercise 401/403/429, rotation, selection changes, Lock/Panic and cancellation with no anonymous fallback or automatic retry.

## R-api-14 — Built, needs live trial

Signed-in Recommended/BestLatency pages, owned private-server listing and reviewed name-only rename are built. Purchasing, renewal and access mutation are excluded.

Select a disposable owner-controlled account and a real place. Compare Recommended and reported-latency results and cursor pages with Roblox, including missing ping and repeated/expired cursors. List only owned private servers. Review and rename one owned server, then fetch again to confirm; no purchase, renewal or permission change should occur. Exercise 401/403/429, rotation, selection changes, Lock/Panic and cancellation with no anonymous fallback or automatic retry.

## R-accounts-10 — Built, needs live trial

Optional password protection wraps the vault data key, locks it in memory and resumes interrupted migration without an alternate persisted recovery key.

Use a disposable application data folder: enable a master password, restart, verify saved sign-ins cannot be used until unlock, and try a wrong password. Exercise idle/Windows/manual Lock and Panic during unlock and launch preparation; no late acceptance may start a new dispatch. Interrupt and resume migration with the same password, then test separately confirmed forgotten-password recovery while retaining account metadata. Verify legacy sign-in-required records receive the explicit reauthenticate/remove-first message.

## R-accounts-11 — Built, needs live trial

Password-sealed portable backup preserves protected sign-ins and allowed metadata while importing fresh identities through verified consent.

In isolated source/destination data folders on separate Windows accounts or PCs, explicitly export with a new backup password, then import and verify account identity. Test wrong password, modified/truncated archive, an existing local account, cancellation/Lock/Panic and a concurrent account change. Imported profiles must use new local identities; no verification grant, automatic launch, webhook secret, extra launch arguments or client-control authority may be replayed.

## R-accounts-12 — Built, needs live trial

Optional Windows EFS protects supported data-folder files; unsupported Windows editions, partial encryption and redirected paths are reported.

In a disposable NTFS data folder on an EFS-capable Windows edition, explicitly enable EFS and confirm files cannot be read from a different Windows account without the certificate. Exercise an unsupported filesystem/edition and partial failure. Verify new SQLite/vault writes inherit encryption and that certificate export/recovery works before using real data. Never run this on the only copy of account data.

## R-accounts-7 — Built, needs live trial

Roblox-hosted Google/Apple sign-in navigation and opt-in retained sign-in browser data are implemented without saving provider passwords or provider sessions as Roblox credentials.

In the final app, use a disposable owner-controlled Google/Apple-backed Roblox account and complete the provider flow yourself. Confirm only the verified Roblox identity is saved, provider navigation discards the entire transient browser profile, and cancellation/Lock/Panic removes transient sign-in material. Separately, use ordinary Roblox sign-in to test the explicitly opted-in browser-device-data retention: sign in twice, verify Roblox authentication is cleared between runs, then use Forget sign-in browser data and confirm removal. Provider navigation never supplies the retained-profile reuse case. Captcha reduction is not guaranteed.

## R-accounts-8 — Built, needs live trial

Roblox-hosted Google/Apple sign-in navigation and opt-in retained sign-in browser data are implemented without saving provider passwords or provider sessions as Roblox credentials.

In the final app, use a disposable owner-controlled Google/Apple-backed Roblox account and complete the provider flow yourself. Confirm only the verified Roblox identity is saved, provider navigation discards the entire transient browser profile, and cancellation/Lock/Panic removes transient sign-in material. Separately, use ordinary Roblox sign-in to test the explicitly opted-in browser-device-data retention: sign in twice, verify Roblox authentication is cleared between runs, then use Forget sign-in browser data and confirm removal. Provider navigation never supplies the retained-profile reuse case. Captcha reduction is not guaranteed.

## R-instances-7 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

The live trial must confirm, with a real RobloxPlayerBeta.exe client: (1) the cap applies through the production adapter against the client's real executable (the executable-name guard only accepts RobloxPlayerBeta.exe; tests exercise the identical path with a cmd.exe helper); (2) a real Roblox client is normally in no Windows job, so Cap CPU does not hit AlreadyInJob in practice (and that it is correctly refused when a bootstrapper or another manager already placed the client in a job); (3) the measured CPU share actually binds and lowers the client's frame rate as intended, and whether that helps capacity — no performance/capacity benefit is claimed, it needs an equal-workload measurement; (4) that a cap a killed Zenith left is observed on the client at the next start and that closing and reopening the client clears it. No real account, client or key was used here.

## R-instances-12 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

The live trial must confirm, with an owned account and a real client: (1) that a real Roblox client shows its leave prompt on the first WM_CLOSE and leaves on the second one posted 400 ms later (the gap was chosen from RoALT's observed behaviour, not measured here); (2) that a healthy client exits within the 6 s wait, so Force close is not offered for clients that are merely slow to leave (if it is, raise ClientCloseOperations.DefaultExitGrace); (3) that a hung client (Not responding) stays open through the wait and the force close ends it, with the forced end then shown in Recent clients; (4) the rendered row button and both confirmation dialogs on screen (only the XAML structure and the view model were tested).

## R-instances-18 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

The live trial (an attended session on this PC, accepting the UAC prompt) must confirm: (1) Apply upload limit at, say, 1000 kbit/s ends with "Windows confirmed the upload limit..." and an administrator `Get-NetQosPolicy -PolicyStore ActiveStore` lists ZenithSessionsUploadLimit-RobloxPlayerBeta.exe, -RobloxPlayer.exe and -Roblox.exe with ThrottleRateAction 1000000 and the matching AppPathNameMatchCondition; (2) Remove ends with "Windows confirmed that Zenith's upload limit policies are gone" and the listing no longer shows them; (3) declining the prompt yields the Declined status line and no policy; (4) a second Zenith start in the same Windows session shows the recorded limit and a start after a Windows restart shows none; (5) optionally, with a running client, that its outbound traffic is actually held near the rate (needs the QoS Packet Scheduler bound to the adapter, the default). None of this could be run unattended: no test may answer a UAC prompt or change this PC's QoS policies. In a disposable Windows guest, cancel or time out an approved Apply, then try Remove while the old helper is still pending: new mutation must remain blocked until actual exit or definite no-start. Repeat after closing and reopening Zenith, including a wall-clock correction. Unknown restart evidence must retain the hold. Verify recovery after an observed lower Windows uptime; inspect legacy/unreadable record handling without blindly deleting a pending-owner marker.

## R-instances-15 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

Live trial with a running Roblox client and the option on: the Sessions row must show a number that moves with the client's load and agrees in order of magnitude with Task Manager's per-process GPU column; the real "\GPU Process Memory(*)\Dedicated Usage" instances must parse under the pid_<pid>_luid_<high>_<low>_phys_<n> convention on the owner's driver (an unrecognized name makes every client's memory Unavailable by design, which the trial would reveal); the extra query-limited handle per client per second should not be visible in the manager's own CPU; a client that closes mid-sample must show Unavailable for that second and credit nothing to the next process that takes its PID.

## R-instances-2 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

The live trial must confirm, with a real client that stays in one server for more than five minutes: (1) the row keeps "Place · last confirmed N min ago" and the state "Connected · last confirmed N min ago" across a long session, and the marker advances once a minute; (2) Join same game, Join same server and Copy reported server link stay disabled after five minutes and are refused at click time; (3) a real teleport, disconnect or client exit after five minutes turns the row to that state (not the old place) with the destination kept as last-known; (4) that a real client never logs a second "Joining" without a recognised teleport or disconnect marker in a long session (a join after five minutes is now accepted as a new join, as before; one inside five minutes is still treated as a conflict); (5) that a lease lapse under a busy log worker (BudgetExceeded) does not make the display flicker to Unknown too often, since a lapsed binding still drops the row to Unknown exactly as before.

## R-instances-17 — Needs decision

The bounded keep-awake policy, runner and single-input-owner integration are built and tested with fakes, but the production target-directed transport is absent (Z1702); a live trial alone cannot enable the shipped implementation.

Keep keep-awake, background macros and broadcast disabled in this first live-test build while a production target-isolation transport is evaluated separately under Z1702?

## R-api-12 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

The live trial must confirm, with a real Discord webhook the owner creates: (1) a real Discord channel accepts the JSON body (content plus allowed_mentions.parse []) and shows the message; (2) a real 429 from Discord carries a Retry-After the client reads (tests used a loopback server's header); (3) with a real client, the hang alert fires once the hung-window check flags it, the disconnect alert fires on a real logged disconnect with the right reason wording, and the crash alert fires after the Windows fault-record check (the fault check runs 5 s after the client ends and needs the real WindowsClientFaultProbe); (4) the account label resolves from a real saved sign-in's verified identity (tests covered the loaded-page path and a null result only); (5) a batch-finished alert after a real Queue checked accounts run. No message was sent to Discord unattended.

## R-api-17 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

The live trial must confirm, with the real Discord app running on this PC: (1) that the Discord application the owner registers in the Developer Portal (name it as it should appear, e.g. "Zenith Sessions") accepts Zenith's handshake with its Application ID pasted into Settings → Discord application ID, and that Discord shows the status as "<application name>: N clients running" (and the game line when chosen); (2) that the status clears on Lock, Panic and Exit (the pipe is closed; Discord is expected to clear a status when its RPC connection closes) and returns after Unlock and Resume Discord status; (3) that starting Discord after Zenith is picked up within a minute and quitting Discord leaves the status line saying it is not running, with no error; (4) that an unknown Application ID yields Discord's close code 4000 as assumed, so the "Discord does not know the saved application ID" message fires. No Discord application exists yet for Zenith: registering one needs a Discord account, which this unattended run could not do, so the ID is a user setting; the owner may later bake the registered ID in as the default so users need not enter one.

## R-shell-21 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

Live trial on the owner's PC (no account or client needed): press Schedule shutdown with a short delay and confirm Windows shows its own shutdown notice carrying the Zenith comment and that the countdown on Settings and in the footer agrees with it; press Cancel shutdown and confirm Windows reports the shutdown aborted (exit code 0 path); schedule one with shutdown /s /t from a command prompt first and confirm Zenith reports "already has a shutdown scheduled" (exit code 1190) and that Cancel shutdown clears it; confirm Panic cancels a Zenith schedule and tray Exit cancels it; let one run to zero on a disposable session to confirm the "shutting down now" state and that the Windows session end does not run shutdown /a. Every test here replaces the tool with a fake; only cmd.exe and PING.EXE were run for real.

## R-api-18 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

Live trial must confirm, on a real PC with the guard saved on: (1) switching the active connection's Windows profile from Private to Public (or joining a Public Wi-Fi with internet access) raises the Network List Manager notification and, within a moment, locks the dashboard with the "Public network: ..." notice, shows "Public network: launches held" in the footer, and makes a queued launch row read the PublicNetwork hold explanation; (2) switching back to Private lifts the hold, shows the "no longer Public" notice and lets a waiting row start on its own once the queue is resumed; (3) a Public network with no internet route (Hyper-V/WSL/VPN adapter) does not hold; (4) no COM error on PCs where the Network List Service is disabled (the monitor should answer Unknown and the status should say Windows could not report the profile). Unattended, the Windows monitor was only exercised against this PC's own network list (read, idempotent Start, Stop, Dispose) with no transition.

## R-shell-14 — Needs decision

Signed-update code, daily/manual scheduling, stop fencing and Settings controls are built; production trust remains intentionally unconfigured.

Which public HTTPS release host, signing-key custodian and Authenticode publisher certificate/fingerprint distribution should the production build trust?

## R-shell-15 — Built, needs live trial

Signed release verification, staged-content integrity and explicit per-user distribution tooling are built. No production trust keys/feed or ReleaseReady package is supplied.

Prerequisite: resolve R-shell-14 production host/key/publisher trust, provide an explicitly accepted signed build and choose a clean disposable Windows guest. In a clean disposable Windows guest, use an explicitly approved release host, embedded public key and Authenticode publisher. Verify valid update staging and an explicitly approved apply on next start; reject altered signatures, ZIP/content/inventory changes, expired/downgrade manifests and unsigned publishers. Test missing .NET/WebView2, declined prerequisite install, first install, update rollback, Start-menu/Apps registration, and uninstall with both data-preserve and separately confirmed data-delete choices. No unattended install or real data deletion is authorized by this offline record.

## R-research-12 — Built, needs live trial

Local and GitHub Actions gate builds/tests all six projects, rejects stale/no-test results, records source identity, excludes the paid test and checks the package.

After publishing the reviewed commit to the intended repository, run the Windows Actions workflow without API/account secrets. Confirm all six suites, pinned Luau, warning-as-error builds and packaged stages run, the paid AI test is absent, skips are named, and a deliberately failed check prevents a green workflow. No hosted Actions execution is claimed by local runs.

## R-launch-9 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

Live trial on the owner's PC must confirm: (1) https://setup.rbxcdn.com/<clientVersionUpload>-RobloxPlayerInstaller.exe answers HTTP 200 with the current installer (the address shape follows Roblox's own bootstrapper and the RoALT research fixture; no live fetch was made because network access was permitted only for R-research-10 and R-api-19; if Roblox's host differs, only RobloxClientUpdate.SetupHost and RobloxClientRelease.TryInstallerUri change); (2) Windows verifies that real file's signature from Roblox Corporation with cached trust evidence through the existing WindowsAuthenticodePolicy; (3) running RobloxPlayerInstaller.exe with no arguments updates the installed player and what it opens afterwards, and that the 10-minute wait, the post-run refresh and the "N Roblox clients are running" warning behave with a real client that the installer closes; (4) the automatic check's 3 s and 30 min cadence on a real dispatcher (tests drive the tick body directly and assert the armed intervals).

## R-shell-20 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

Live trial must confirm, with a real running Roblox client: (1) that the client's own open log is refused by the cleanup's exclusive-share open (FileShare.Delete only) and so is counted "in use" and never deleted — the tests prove this with a handle the test host holds open with read sharing, as the C runtime does, not with Roblox's real handle; (2) that today's Roblox Player log file names still contain "_Player_" (the filter the probe already relies on); (3) that the Delete these logs confirmation and the result line read well in the real window. No real account, client, API key or network was used.

## R-shell-17 — Built, needs live trial

Merged implementation and synthetic/native-helper tests; real-client or service acceptance is not claimed.

The live trial must confirm, with a real running Roblox client on an owned account, that the client's current log opens with read, write and delete sharing while the client writes it (tests prove the shape with files this test host holds), and that today's client still writes the recognised line shapes (join, identity, Replicator created, the disconnect markers, teleport, challengedByGcs; an Unverified format per IncrementalClientLogReader.FormatSupport), so a real saved report carries the joins and disconnects a support reader expects.

## R-api-19 — Needs decision

DevForum RSS/Atom reader and News page are built and tested. Four official Newsroom feed candidates returned 404; no Newsroom feed or scraping fallback is configured.

Ship DevForum announcements plus a confirmed external Newsroom button until a valid official Newsroom RSS/Atom URL is supplied?

## O-C4-3 — Needs decision

Lock already cleared the previous error. The old report changed an assertion to restate existing behavior; it is not a genuine regression repair.

Retain the existing lock-clears-error behavior and exclude this documentation-only correction from regression-proof requirements?

## O-C4-4 — Needs decision

Added coverage passes on the original code and detects deliberate mutants. That is useful coverage proof, but not a failing-before production repair.

May this coverage-only item be excluded from the runtime failing-before requirement, with the new assertions and recorded mutation proof retained?

## O-C2-4 — Needs decision

Comment/test-name correction retained; runtime already behaved correctly, so no genuine failing-before test exists.

May this documentation-only correction be excluded from the failing-before requirement while retaining the corrected text and existing behavior?

## O-C7-2 — Needs decision

Comment/test-name correction retained; runtime already behaved correctly, so no genuine failing-before test exists.

May this documentation-only correction be excluded from the failing-before requirement while retaining the corrected text and existing behavior?

## O-C8-3 — Needs decision

Comment/test-name correction retained; runtime already behaved correctly, so no genuine failing-before test exists.

May this documentation-only correction be excluded from the failing-before requirement while retaining the corrected text and existing behavior?

## R-instances-0 — Built, needs live trial

Browser-tracker and exact process-incarnation correlation, ambiguity handling and diagnostic evidence are built; live association remains unproven.

Attend one public standard-player trial, then a two-account trial if the first succeeds. For each start, compare expected player/place with the exact process PID/creation time and its own browser-tracker-linked log. Exercise simultaneous logs, PID reuse, delayed/ambiguous evidence and restart; ambiguous data must not qualify a launch or assign another account. Do not use singleton workarounds.

## R-instances-13 — Needs decision

No production automatic-recovery coordinator exists. Official-page mode is attended, and releasing old launch reservations or inferring server unavailability requires a separate authority policy.

Authorize opt-in Direct-ticket-only recovery after a separate attended recovery qualification, releasing reservations only after exact-process exit and retrying the same server without speculative fallback, while deliberate closes and macros stay stopped?

## R-instances-16 — Excluded

No foreign window-style rewrite or global hook to force unsupported minimum window sizes.

Supplied handoff explicitly excludes windows smaller than the Roblox minimum because it requires a foreign style rewrite/global hook.

## R-launch-7 — Built, needs live trial

A saved protected private-link reference has a distinct, explicit one-account official-page trial; no speculative direct-ticket private URI or public fallback exists.

Select a checked saved private server, account and trusted standard player in Official page mode. Review and confirm one private-server trial, press Play on the owned official page, then verify the actual client identity and private server. Repeat with revoked invite, unsupported emitted protocol, cancellation, saved-record removal and Lock/Panic during capture; expect no public fallback, duplicate dispatch or replay after restart.

## R-shell-13 — Excluded

Data folder relocation is not implemented.

Supplied handoff explicitly records that the owner declined moving the data folder.

## Z1702 — Needs decision

Input ownership, cancellation and keep-awake policy are tested, but the production target-directed input transport is absent; these controls remain disabled.

Keep background macros, broadcast and keep-awake disabled in this first live-test build while the production target-isolation transport is evaluated separately?
