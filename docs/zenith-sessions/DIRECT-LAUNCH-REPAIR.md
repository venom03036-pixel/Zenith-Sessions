# Saved-account direct launch repair (0.81)

The owner chose **“Launch directly, like RoALT”**, superseding the earlier requirement to record a successful trial after each executable change for normal Direct ticket launches. The owner also chose **“Finish the code; I’ll test it myself.”** No real account, Roblox client, browser sign-in or paid AI test was used in this repair.

## Findings and implementation

The local RoALT repository at `C:\Users\noelm\OneDrive\Documents\GitHub\RoALT`, revision `2e304a0c2c5ab90e40b63a982f81c03e992270d4`, was inspected read-only. Relevant sources are `src-tauri/src/commands.rs`, `webview.rs`, `vault/dpapi.rs`, `roblox_api/mod.rs`, and `launcher/{mod,uri,client}.rs`. No reference code was copied.

RoALT captures a session from its owned Roblox sign-in window, verifies the user, and encrypts the session cookie. Launch requests a client assertion and authentication ticket, carries forward replacement cookies, and passes a ticket-bearing `roblox-player:` request directly to the installed player with that executable's parent as the working directory. It does not require a separate installation per account. Zenith already used encrypted saved sessions and a held, publisher-checked native executable; rewriting the vault or adding OAuth was unnecessary.

The defects were in the path using that saved session:

- Ordinary launch capability was withheld until a historical manual trial was recorded. Direct ticket now supports ordinary launches without that historical gate. The real native provider still checks the current account, final grant, publisher, held executable, capacity, stop state and cancellation. An Official page choice retains its recorded-trial policy.
- Ticket preparation returned a rotated cookie to its caller but continued sending the old cookie on the ticket POST and CSRF retry. Each step now sends the newest accepted cookie. A deletion or invalid replacement stops negotiation instead of falling back to a stale cookie. The replacement still undergoes same-player verification before encrypted persistence and dispatch.
- Ticket requests omitted the Roblox protocol Origin and Referer headers. These now accompany Zenith's own User-Agent; no Chrome/browser User-Agent is impersonated. Endpoints, no-redirect handling, no automatic cookie jar, bounded response sizes, one CSRF retry and deadlines remain enforced.
- Direct ticket is the default. Older settings without `NativeLaunchPolicyRevision` migrate the former Official page default on read, with a notice, without rewriting the original file. Settings written by this revision retain deliberate later Official page choices. No account/vault schema changes are involved.
- Account and launch-review controls bind to launch availability, separately from recorded trial evidence. Optional diagnostic trials accept available preparing providers while retaining per-request consent, account binding and explicit destination requirements. Queue pauses still require Resume queue, with a visible explanation; selecting a different installation, Panic or Lock cannot silently resume pending work.

Only launch wiring and explanatory text changed in the UI. A process receipt remains `Client started · unverified`; a successful ticket request or process start never becomes a claim of correct player or arrival.

## Reproduction and offline evidence

`artifacts/direct-launch-fix/red/direct-launch-red.trx` records **12 failing-before cases** for the stale-cookie chain, missing protocol headers, invalid replacement handling, direct-mode default, availability without a historical trial, and fresh identity/mode enforcement. The first affected pass exposed two old header-contract assertions, updated to assert the exact required headers while retaining honest User-Agent, endpoint, redirect and cookie-isolation checks.

Expanded runs caught old-default assumptions in diagnostic fixtures and an actual optional-trial conflict in the queue. Diagnostic fixtures now explicitly select Official page; direct launches have their own Shell-to-queue test. Tests for the approved default/header/trial behavior were updated to assert the new contract, not removed or skipped.

`expanded-4/direct-launch-expanded-4.trx` also reproduced a test observation race in `LaunchPacingTests.AJoinReportAfterTheLaunchSpacingEndsTheWaitAtOnceAndWakesTheQueueItself`: the fake provider had counted its second start before the coordinator persisted that receipt. The fixture now joins active-operation cleanup before the same exact persisted-state assertion. No timeout was enlarged, and no production pacing behavior changed.

`artifacts/direct-launch-fix/expanded-final/direct-launch-expanded-final.trx` records **1,737 passed, zero failed/skipped**. Coverage includes real temporary DPAPI/repository storage with synthetic cookies, actual HTTP ticket/capture/preparation flow, same-player verification, reloading the encrypted replacement, final-grant dispatch with a fake native starter, and Shell-to-provider ordinary launching without recording a trial. No network-auth or live native success is inferred.

Final whole-suite, build and package results are recorded separately under `artifacts/direct-launch-fix/final-checks/` and the delivered package receipt. Those records must identify the exact code commit. This repair does not erase the earlier failed full-gate evidence at `6f5b813`, change the 93 historical item dispositions, establish two complete accepted gates, or promote main. Live R-api-7/R-launch-0 acceptance remains outstanding.

## Owner test

1. Exit the old preview through its menu, then open the new executable with all accompanying files.
2. Settings → Roblox installation: select the standard installed `RobloxPlayerBeta.exe`; Direct ticket should be the saved mode. No “Record this trial” setup is required. No new sign-in is needed unless the existing session is actually invalid.
3. Cancel any old blocked, never-started request for the account; do not confirm an uncertain client closed unless it really is closed.
4. Select `@sethplayzfan`, choose Anime Card Clash, place `110829983956014`, and queue the checked account. If paused, choose **Resume queue**. No game web page should open for Direct ticket.
5. Confirm that Roblox opens as the intended account and joins the intended game. Inspect Launch evidence for the receipt and client-reported player/destination. Restart Zenith and check that the account remains saved.
6. Report the exact failed stage if it does not start. Do not share a cookie, password or launch ticket. Multi-client, bootstrapper and private-server compatibility remain separate attended trials; this repair does not implement singleton-enforcement workarounds or claim those trials passed.

Rollback: use the prior isolated package or explicitly select Official page in this build. A settings read does not destroy the prior file, and account encryption is unchanged. A new build's saved Direct ticket mode can still be read by the prior build, which will apply its older trial gate.
