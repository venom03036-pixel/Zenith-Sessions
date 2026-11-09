# Zenith Sessions desktop preview

The rebuild branch now has a Tauri desktop workspace with the exported Zenith colors, typography, navigation and account table. It runs separately from the existing WPF app. The preview's library lives under the Windows local application data directory at `com.zenith.sessions.preview\library`.

## Using the preview

1. Close any older Zenith preview window before opening the new executable.
2. In **Accounts**, select **Add account** and complete sign-in in Roblox's own window. Once the window reaches Roblox's home page and identity verification succeeds, Zenith saves the sign-in with Windows encryption, selects the profile, and closes the sign-in window. If saving fails, Zenith displays the error and offers **Retry save**. To renew a saved profile, use **Add account** again and sign in as the same Roblox account.
3. To bring over an existing saved sign-in, exit the existing Zenith app. From the repository root, stage that account with the command below. The [build and staging instructions](RUST-LAUNCH-PROBE.md) show how to build the existing vault helper first.

   ```powershell
   & ./apps/zenith-sessions/tools/Stage-LaunchProbe.ps1 -Username 'YOUR_ROBLOX_USERNAME'
   ```

   In the preview, choose **Import protected file** and select the returned `probe.dpapi` file. It is protected by Windows for the current Windows user. Keep the staging directory local.
4. Open **Sessions** and choose the account. Search by game name and select a result, or enter a place ID or public Roblox game link. For Anime Card Clash the place ID is `110829983956014`. To target a specific server, choose **Browse public servers** and select a row. **Use any server** clears that selection. Select **Launch** when ready.
5. Close any other Roblox players, the existing Zenith app and RoALT before the preview starts Roblox. Overview shows the actual memory headroom. **Try one account anyway** appears only when the physical-memory reserve is the limiting factor and the hard physical and commit requirements still pass.

The preview rechecks the signed standard Roblox player and verifies the saved account with Roblox before starting it. **Arrival confirmed** requires the owned process and window plus attributable client evidence for the initial account, place, and server if specified. A confirmed mismatch stops the owned process. **Running · unconfirmed** means there was a process receipt and Zenith could not confirm arrival. Stop targets the process this attempt started. If Windows declines Focus, use Roblox's taskbar window.

Server browsing requests one public page at a time. Occupancy is reported by Roblox when the page loads, and seats are assigned when Roblox joins the server. Changing the destination clears the selected server. The initial game-name results are capped at 50. Public links with query parameters and private/share links are rejected by this preview.

The account list contains authenticated or imported identities. Removing an account removes its preview sign-in and profile. A damaged credential leaves the profile visible and is refused at launch. Renewal replaces the encrypted credential for the same Roblox user ID and keeps the existing profile.

## Preview 1.11 emergency stop

**Panic stop** is available in the header on every page. **Ctrl+Shift+P** also invokes it through a native Windows shortcut worker, including when another app has focus. If Windows refuses registration, the workspace shows a shortcut notice and the button remains available. [Windows global shortcut registration](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)

Panic immediately revokes old launch permits and holds new starts. It cancels preparation, discards late launch authentication results, prevents further launch authentication requests and retries, and asks the current owner to stop its client processes. A delayed Launch click still carries the previous state and is refused after Resume. An HTTP request already sent can finish within its existing timeout. If Windows has already entered process creation, Zenith stops the resulting owned process when it has the handle. Recovered and externally started clients stay outside that process-control boundary.

**Resume launches** appears after the owned work finishes and cleanup succeeds. A newer Panic invalidates an older Resume action. If cleanup fails, the hold remains and the header button retries it. Resume permits a fresh launch selection. It does not replay an interrupted request. The Panic hold belongs to the current app run. The existing recovery journal governs unresolved starts after restart. The normal **Stop all owned clients** action remains available in Sessions.

The Chromium fixture reproduced the missing header control in 1.10 and verified the hold and explicit resume in 1.11. Native tests register an alternate Windows shortcut, post a fixture message to its own native queue, verify the Panic fence and registration conflict, and verify release on shutdown. Separate fixtures check late preparation, stale Resume, and cancellation before a CSRF retry. No Roblox client or account was used in those checks.

For the attended keyboard check, open this preview, move focus to Notepad, press **Ctrl+Shift+P**, and return to Zenith. Confirm the hold appears and only **Resume launches** clears it. For the owned-client trial, launch one designated account, verify its arrival, and repeat the shortcut with Roblox focused. Check that the exact client closes, any unrelated window stays open, and a new launch remains held until Resume. Repeat during the preparing phase and confirm no late start appears. Physical key delivery and stopping a real Roblox client remain attended checks. Bounded graceful window close and a future automation input owner's Panic integration remain separate work.

The 1.11 executable has SHA-256 `29689A104E536EDCEAC0F317670BA60553223B095D237451417EC0C086F996A5`. Forty-eight offline Rust tests, four Chromium fixture checks, formatting, Clippy with warnings denied, the frontend build, and the zero-warning Release build passed. Both packaged self-checks passed all six stages.

## Preview 1.10 saved sign-in rotation

When Roblox returns a replacement sign-in during authenticated account reads or launch preparation, Zenith checks the replacement against the authenticated-user endpoint before using it. It refuses a different account and asks for a fresh sign-in if Roblox returns a second distinct replacement during confirmation. A server-returned expiry can shorten the original saved lifetime. The encrypted bundle format and library location are compatible with Preview 1.09. [Roblox authenticated-user endpoint](https://create.roblox.com/docs/cloud/reference/domains/users), [cookie expiration rules](https://datatracker.ietf.org/doc/html/rfc6265)

Zenith allows one in-flight credential operation per saved account. If an operation is already using that sign-in, retry after it finishes. The local commit compares the original protected record before replacing it. An explicit renewal or removal wins over a late result. Launch preparation also checks the current attempt before saving and checks it again before native dispatch. Normal profile reads with no verified update leave the protected file alone. The implementation reuses the cookie parser already present in the Tauri build to handle expiry and revocation attributes.

The loopback regression reproduced a different-account replacement accepted by Preview 1.09. The fixed tests reject it, reject an unstable chain, confirm ordinary rotation through the launch ticket sequence, and reopen the verified replacement from Windows-encrypted storage. Desktop tests check concurrent use, explicit renewal and removal. The cross-language migration check still opens a synthetic legacy sign-in and keeps a password-protected vault's sign-ins in the source fixture.

For an attended check, use one designated saved account. Refresh its profile and compare the selected identity, then close and reopen Zenith and launch the same account into the agreed place. To check whether this exercise included a rotation, record the write time and hash of the account's encrypted `.dpapi` bundle before and after the isolated refresh. Inspect the encrypted file metadata only. If the bundle did not change, the exercise covers ordinary sign-in reuse. If it changed without a manual renewal, record the write and confirm the reopened profile and launch still match that account. The test depends on Roblox issuing a replacement and has no forced rotation step.

The 1.10 executable has SHA-256 `D2FAEDE249F15CFBF4658C3B8F93EEB60B9F763360C1434A41215100C4496AB7`. Forty-three standard offline Rust tests, the separately staged C# to Rust migration test, and three account browser fixture checks passed. Formatting, Clippy with warnings denied, the frontend build, and the zero-warning Release build passed. Both packaged self-checks passed all six stages.

## Preview 1.09 account refresh and client performance

Select a headshot or username in **Accounts** to open that account's profile, public favorite games, friends, and Robux balance. Headshots that are pending get two automatic retries. Initials remain available when Roblox does not return a usable headshot. The top-bar **Refresh** retries both the lookup and the image load. A successful renewal updates the open inspector for the same saved profile. Official Roblox sign-in closes after the verified sign-in is saved locally. The preview continues to use the same protected account library.

The account browser fixture uses six synthetic accounts. Against 1.08 it reproduced a pending sixth headshot stuck on initials and a stale balance after renewing the selected account. Both cases pass with 1.09, along with switching accounts while an earlier profile request is pending. Reproduce from `apps/zenith-sessions` with `npm run build` followed by `node tests/account-workspace.fixture.mjs`. Set `ZENITH_FIXTURE_BROWSER` to a local Chromium executable if Chrome is installed elsewhere.

Friends pagination uses Roblox's default sort. The request regression exercises the production URL, including escaping an opaque cursor. Live private data and the real Roblox sign-in window still need the attended trial. [Roblox friends API schema](https://prod.docsiteassets.roblox.com/assets/en-us/cloud/legacy/friends/v1.json), [attended account trial](#preview-105-account-details)

**Performance** samples confirmed clients started and still owned by the current Zenith process. It reports CPU as a share of machine capacity, working set, private working set where Windows exposes it, private commit, read/write transfer rates, uptime, and process priority. Rates require two fresh samples of the same process. I/O transfer rates include more than disk activity. Opening another page or hiding the workspace stops the frontend polling. [Windows process memory counters](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2), [Windows process I/O counters](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessiocounters)

**Set Below normal** is opt-in for a client currently at Normal. **Restore original priority** applies to the same live process when Zenith still owns the change. Both actions check the retained process handle and read the resulting class back from Windows. If Zenith exits abruptly and the client survives, the OS can retain the changed priority. The recovered entry remains read-only. [Windows process priority classes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass)

For the attended performance trial, open **Performance** after confirming one designated account's arrival. Compare the displayed PID and readings with Task Manager or Process Explorer. Set **Below normal**, check the same PID, then restore it and check again. Repeat with a second confirmed client after the pair trial, verifying the second client's priority stays where it was. Compare equivalent workloads and record client and manager CPU and memory separately before drawing conclusions. Roblox performance and any savings remain unmeasured.

The 1.09 executable has SHA-256 `F2900B09EBAD95E08A8318A0A4D234AFFA3F779009AC402B4617FEA980CE336C`. Thirty-nine offline Rust tests, three Chromium fixture checks, formatting, Clippy with warnings denied, the frontend build, and the zero-warning Release build passed. Both packaged self-checks passed all six stages.

## Preview 1.08 launch recovery

Zenith writes a bounded journal to `library\launch-journal.json`. It records the attempt, selected profile, requested place and server, and the startup checkpoint. Before creating a process it records dispatch intent. After startup it records the PID and creation time when Windows can provide both. The journal carries no sign-in material, launch ticket, or native command line.

On restart, **Sessions → Launch recovery** separates interrupted preparation, uncertain startup, a surviving exact process, and an ended process. Process creation time prevents a reused PID from inheriting a launch receipt. A surviving process is a Windows observation. Roblox account and arrival evidence remain separate. Uncertain and surviving records hold new starts. Close surviving clients using their own windows, then dismiss the entries. **Check and dismiss** on an uncertain start requires a clean Roblox process inventory. An unreadable or newer journal holds starts until explicitly archived. [Windows process creation times](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes), [Windows process wait results](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject)

For the attended trial, start one designated client and confirm its arrival. In Windows Task Manager end the Zenith Preview process while leaving Roblox running, then reopen this preview. Verify that the same PID appears as surviving, that no new launch starts, and that Focus and Stop are unavailable for that recovered entry. Close Roblox manually, confirm the entry changes to ended, and dismiss it. Repeat with two designated clients after the two-account trial succeeds. Manager restart with real Roblox and rendered recovery controls remain unverified. Automatic restart, rejoin, adoption of recovered clients, and retry backoff remain separate work.

The 1.08 executable has SHA-256 `DFA5D1DF2471561A6ED53F5BD371F698C9D9BFC6731D19AE35EB77B8CD8FF960`. Thirty-six offline Rust tests, formatting, Clippy with warnings denied, the frontend checks, and the Release build passed. The six-stage packaged self-check passed twice. Its journal stage wrote a synthetic dispatch into a disposable directory and reopened it for review.

## Preview 1.07 legacy workspace migration

The full-workspace import preserves legacy profile IDs and notes, aliases, group membership and definitions, saved favorite destinations, recipient bundles, macro assignment references, and a portable subset of preferences. The Accounts view shows imported details for matching verified profiles, archived profiles, and legacy favorite destinations. A favorite can preselect an exact place and optional public server in Sessions. Applying a supported theme is a separate action. Group and bundle editing, macro execution, and applying other archived preferences remain pending.

1. Exit the legacy WPF Zenith Sessions app, including its tray process. From this checkout, build the migration helper and stage a protected workspace.

   ```powershell
   dotnet build ./apps/zenith-sessions/tools/LegacyProbeBundle/LegacyProbeBundle.csproj -c Release
   $migration = & ./apps/zenith-sessions/tools/Stage-LaunchProbe.ps1 -Workspace
   $migration
   ```

2. Open Preview 1.07 under the same Windows user. In **Accounts**, choose **Import protected file**, then select the returned `legacy-workspace.zenith-migration` file. The single-account `.dpapi` option is also available from that picker.
3. Compare the counts and select a linked profile to check notes, groups and preferred place. Review **Archived profiles** for records without a verified identity. For an imported, historically verified account whose sign-in expired, complete the official sign-in again for that same Roblox account.
4. To retry an interrupted import, select the exact same protected file. After a completed import, this preview library refuses a second workspace import. Existing preview sign-ins with the same verified Roblox user ID take precedence. The active preview library holds up to 128 accounts, and the archive preserves profiles beyond that capacity. The staging limit is 10,000 profiles and a 16 MiB encrypted workspace.

The helper acquires the original app's instance lock while staging a copy. Keep the original WPF app and data directory for rollback. The stage includes local metadata and current-user protected files, so keep it on this PC. The installed Rust archive contains metadata only, protected with current-user DPAPI. Each transferred sign-in is stored in its normal per-account DPAPI file. An interrupted import keeps a fingerprint of the input for retry. The migration does not carry queued launches, process state, executable trust, or private access links. A password-protected or unreadable legacy vault contributes metadata only. Its protected credentials remain in the original vault. Returning to the WPF app uses the original library, and changes in the preview do not sync back.

The cross-language fixture covered a valid sign-in, an expired sign-in, an unlinked profile, a pre-existing Rust sign-in, and a password-protected legacy workspace. It also checked an interrupted import, reopen, and refusal to re-import a completed archive after removal. The actual owner's migration and packaged file-picker interaction remain attended checks.

The 1.07 executable has SHA-256 `2C2DCBD28478A8570007D5FC110EAA7900AB3E3B909B71EE9C51E2DF0E44A548`. Thirty-two offline Rust checks and the separate C#→Rust migration test passed. The installed-archive regression failed before the metadata-only correction and passed afterward. The helper and desktop Release builds passed with zero warnings, and both packaged self-check runs passed all five stages.

## Preview 1.06 desktop windows

Open **Windows** to see the active sessions. **Focus Roblox** brings forward a confirmed client's window when Windows permits it. **Tile confirmed windows** lays out the confirmed clients in launch order on the monitor containing Zenith. Zenith uses that monitor's work area, checks each window's minimum size, and reports placement only after reading back the requested bounds. Restore maximized or minimized windows and leave fullscreen before tiling. If the work area cannot fit the minimum sizes, the entire layout is refused. [Microsoft's work area guidance](https://learn.microsoft.com/en-us/windows/win32/gdi/positioning-objects-on-multiple-display-monitors), [minimum window size message](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-getminmaxinfo)

For the attended trial, launch two designated clients and confirm both arrivals. Put each Roblox window in restored windowed mode. Place Zenith on the desired monitor, select **Tile confirmed windows**, and compare the two bounds and per-account results. Verify that an unrelated window stays where it was. Repeat with Zenith on a monitor at a different scale if available. Maximize one client and confirm the operation refuses the layout, then restore and retry. Use each Focus button to check that the corresponding account comes forward. Live Roblox window placement, mixed-DPI placement, and rendered interaction are pending.

The 1.06 executable has SHA-256 `4542AF1B00C6D6F981CEB80B27B94671D8ECDD9B6880A1B7010B4BCEE84ACE1E`. Thirty-one offline Rust tests passed, including two native fixture processes and an unrelated fixture window. Formatting, Clippy with warnings denied, the frontend build, and the Release build passed. Both packaged self-check runs passed all five stages.

## Preview 1.05 account details

Select a username or headshot in **Accounts**. The inspector shows the Roblox display name, username, ID, join date, and profile description. It lists public favorite games and pages through the selected account's friends. Selecting a favorite opens Sessions with that account and game selected. Robux and friends use the selected profile's verified saved sign-in. If Roblox hides a listing or refuses a request, the affected section shows the response separately. A returned balance of zero is displayed as zero.

The Accounts list and inspector use Roblox avatar headshots. Pending or unavailable images fall back to initials. The top-bar Refresh reloads the public headshots, and the inspector's Refresh reloads account details. Account switching clears the inspector's previous results. Updating from the previous preview uses the same protected library.

For attended verification, open the preview and select each already saved account. Compare its headshot, public favorites, friends and Robux against the corresponding Roblox account. Switch quickly between two accounts while data is loading and check that only the selected account's details appear. Complete a new official sign-in and confirm the window closes after the profile appears. For cancellation, close a separate sign-in before reaching Roblox's home page and confirm that no profile was added.

The 1.05 executable has SHA-256 `5B3E9E5FC838268B1D493C197FD9E4F2578A5978FBFE13A5B66867AFC70FF946`. Twenty-six offline Rust tests, an anonymous smoke test against Roblox's public profile, formatting, Clippy, the frontend type check and build, and the Release build passed. The packaged self-check passed all five stages twice. The checks did not use saved Roblox accounts.

## Build and validation

Run `npm ci --ignore-scripts`, then `cargo tauri build --no-bundle -- --locked` from `apps/zenith-sessions`. The release output is `src-tauri/target/release/zenith-sessions-desktop.exe`. The installed WebView2 Runtime is required.

The Rust checks cover cancellation during preparation, late results from an older attempt, DPAPI import and reopen, protected renewal, first-party sign-in navigation and preservation of metadata when a credential cannot be read. The native launch core uses the same registered-player, ticket and attribution mechanism as the [ten successful single-account trials](RUST-LAUNCH-PROBE.md). Live sign-in and the desktop launch, focus, stop and close controls still need attended trials.

The release executable opened the Accounts view and its native protected-file picker on the host. The preview's initial library was empty. Release SHA-256 `0494F34C3FF6193A20CE2720EC1A675C8A33A2852971DB82EA2AA4E824C586C9` identifies this first desktop workspace build.

The updated release built with the direct sign-in action. In a host smoke check, **Add account** opened the separate **Sign in to Roblox · Zenith Sessions** window, and its document reported `https://www.roblox.com/login`. Fifteen Rust tests and the frontend build passed. Release SHA-256 `622BF38D24B7EA29B79A1C856A2EE51BE3B091D2A7F5618709D2A2F790F7E037` identifies this build. The smoke check reached the login page without entering credentials.

Preview 1.03 adds anonymous public game search, public link and place ID input, and paginated public server browsing. The anonymous adapter smoke check found Anime Card Clash and returned public server rows. The same-place, wrong-server regression failed before the arrival check changed and passed afterward. The attended exact-server trial is to select a designated saved account, search for Anime Card Clash, choose a listed server with space, start Roblox, and compare the requested server with the initial server confirmed from attributable client evidence. Repeat using **Use any server** to check that the request does not carry the prior server.

The 1.03 executable has SHA-256 `6948202D1CF5F01CBC8FE31ED7F782F8C8DFF5E21D75EFE594FC7B1986040656`. Nineteen offline Rust tests, a separate anonymous endpoint smoke test, formatting, Clippy with warnings denied, the frontend type check and build, and the Tauri Release build passed. The 1.02 sign-in window was left open for the owner, so the 1.03 desktop interaction check is pending.

## Preview 1.04 and the attended two-account trial

The desktop has a two-account trial mode with per-account status, separate launch attempt IDs, confirmed-window Focus, per-session Stop, and Stop all owned clients. It admits one start at a time. Starting a second requires a different saved Roblox identity, a still-confirmed client, 20 seconds since the latest start, and standard memory headroom. The physical-memory override applies to the first start only. Unconfirmed clients hold admission. A pre-existing Roblox client, installer, or conflicting manager can prevent the trial from starting.

1. Save both designated sign-ins through the official Roblox window or import the protected bundles. Close the older preview and any other Roblox clients and launchers.
2. In **Sessions**, select **Enable two-account trial** before starting the first client. Select the first account and Anime Card Clash, place `110829983956014`. Start it and verify that Zenith reports **Arrival confirmed**.
3. Once the stabilization message clears, select the second account and launch it. Record both confirmation results, distinct process IDs, time to arrival, and memory readings. Use each **Focus Roblox** control to verify that it brings forward the corresponding window.
4. Stop one client and confirm the other remains in its session. Relaunch the stopped account when the gate clears. In another batch, cancel the second account during preparation and verify that only the first client remains. Use **Stop all owned clients** at the end.
5. Record five attended batches and note every failure. Manager restart with surviving clients requires a separate reconciliation implementation and trial.

For a packaged check, run the 1.04 executable in PowerShell as follows. The process exits after checking its embedded frontend assets, WebView2, synthetic DPAPI round trip, Windows memory sampling, and the installed standard player's signature. It uses a synthetic profile in memory.

```powershell
$check = Start-Process -FilePath .\Zenith.Sessions.Preview.exe -ArgumentList '--self-check' `
    -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput .\zenith-self-check.txt
Get-Content .\zenith-self-check.txt
$check.ExitCode
```

The 1.04 executable has SHA-256 `CADADF19EBB8D1AEF175832E5264B81068DF2D57CDA1C326F676FC2A99A15594`. Twenty-two offline Rust tests passed, along with formatting, Clippy with warnings denied, the frontend type check and build, and the Release build. Both packaged runs passed all five self-check stages. The checks used synthetic fixtures and the installed player's signature. They did not start the player or open a real account.

Live coexistence and exact-server arrival still need attended validation. Existing-client takeover, full data migration, private-server joining, previews and tiling, automation, performance controls, and the 4/8/16-account scale trials remain in the [feature inventory](ROALT-1.1-FEATURE-INVENTORY.md).
