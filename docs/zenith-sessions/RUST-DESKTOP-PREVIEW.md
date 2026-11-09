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

Server browsing requests one public page at a time. Occupancy is reported by Roblox when the page loads, and seats are assigned when Roblox joins the server. Changing the destination clears the selected server. The initial game-name results are capped at 50. Use **Private servers** for Roblox private invitations.

The account list contains authenticated or imported identities. Removing an account removes its preview sign-in and profile. A damaged credential leaves the profile visible and is refused at launch. Renewal replaces the encrypted credential for the same Roblox user ID and keeps the existing profile.

## Preview 1.21 protected private-server invites

In **Sessions**, select one saved account, expand **Private servers**, and save a private invitation with a local label. The field accepts an official Roblox game-page link with `privateServerLinkCode` or `linkCode`, or an official `/share` link with `type=Server`. Server share links require the selected saved account. Zenith verifies that account and checks the link when saving and again before launch. If the provider supplies an experience ID without a place, Zenith checks the experience's public root place. The launch stops if the invitation no longer resolves to the saved place. [Roblox private-server settings and invites](https://en.help.roblox.com/hc/en-us/articles/205345050-Private-VIP-Servers-FAQ)

Use the saved invite, then select **Launch private server**. The same standard-player publisher, account verification, capacity, cancellation, and process-ownership checks apply. One private invite can be tried at a time. Zenith sends a private request using the place and link code and has no public fallback. That native envelope is based on the pinned reference's protocol behavior and requires a live trial against the installed player. The launch journal records the private destination kind and process evidence. The invite is stored in a separate current-user DPAPI file, and a removed or renamed record invalidates an older launch reference. Windows can hold the invite transiently in the native command line.

**Rename** changes the label in Zenith's saved list. Managing the list does not update a server on Roblox. Existing private invites saved in the WPF build can be added from their original links. The older Rust build preserves and holds a recovery journal containing an active private record because it predates schema 2. Reopen that journal with 1.21 to review it before rolling back.

For an attended trial, obtain a Roblox private invitation for a place the designated account may join. Try a direct invitation and a Server share link separately. Save, select, and launch the invite with other clients closed. Verify the expected account and private server in Roblox. Zenith reports account and place evidence separately and keeps private arrival unconfirmed. Stop the owned client, reopen Zenith, and confirm that the saved invite is available. Then test a revoked share link, cancellation during preparation, and removal of the selected invite. Check that no ordinary launch occurs and that a new launch requires an explicit selection. A live private join has not been verified.

The 1.21 executable has SHA-256 `42A31D61FD8642CE6D05E5B81BF1C4378CEA48FBA4FA468A4242EB02C4575645`. The browser regression failed on 1.20 and passed on 1.21 for save, rename, private dispatch arguments, and removal without selecting a public target. Sixty-five offline Rust tests, thirteen Chromium checks, formatting, Clippy with warnings denied, and a zero-warning Release build passed. The packaged self-check passed all six stages twice. The matching Personal editor is in the same preview folder.

## Preview 1.20 public game details and artwork

In **Sessions**, enter a public place ID or game link, or select a name search result. Choose **Load game details** to see the experience name, creator, description, population, listed server limit, visit count when available, and Roblox artwork. The panel separates the requested place from the experience's root place. It fetches the public icon and up to six public thumbnails after the text loads. Missing images and API errors leave the destination and server picker usable. [Roblox public game API](https://create.roblox.com/docs/cloud/reference/domains/games), [Roblox thumbnails API](https://create.roblox.com/docs/cloud/reference/domains/thumbnails)

The lookup uses anonymous requests and changes no account, launch, or saved destination state. If the place changes during a lookup, old results are discarded. The numbers in the panel describe the public listing. Use **Browse public servers** for reported occupancy at the requested place.

For a desktop check, open **Sessions** and use a public place ID such as `110829983956014`. Load details, browse and choose a server, then refresh the details. The chosen place and server should remain selected. Paste another place while a lookup is running to check that its response cannot replace the new selection. None of these steps requires an account or starts Roblox.

The 1.20 executable has SHA-256 `F290D45AEB81700C092146A13BC7D51BF7D214338F7C2C2B97048EADD2350B0F`. The browser check failed on 1.19 and passed on 1.20, including the exact subplace and server passed to the synthetic launch command. Fifty-nine offline Rust tests, twelve Chromium checks, the anonymous Roblox game-details and artwork smoke test, formatting, Clippy with warnings denied, the frontend build, and the zero-warning Release build passed. Both packaged self-checks passed all six stages. The matching Personal editor is in the same preview folder.

## Preview 1.19 account details and sign-in

The preview keeps the same account library. In **Accounts**, select a saved account's name or headshot to view its Roblox profile, public favorite games, friends, and Robux balance. Switching away and back shares an unfinished private lookup for that profile and sign-in revision. The headshot display retries a failed image with a new URL so a cached error does not leave the avatar on initials. Initials remain visible until an image loads. **Refresh** in the inspector retries details and headshots. [Roblox headshot API](https://create.roblox.com/docs/cloud/reference/features/avatars)

After the official Roblox page reaches Home, Zenith verifies the account, writes the protected sign-in, and dismisses the isolated window. If the dismissal request fails after the save, the confirmation names the account and tells the user to close that window. A failed save keeps the existing **Retry save** action.

The browser regression reproduced a stuck headshot and duplicate private request before the fix. The updated fixture covers six synthetic profiles, switching back during a private lookup, and the saved-sign-in notice with a separate window warning. An anonymous request using Roblox's public user ID 1 returned a PNG with the cache-refresh parameter. Actual private data and automatic window closure remain to be checked with a designated account.

The 1.19 executable has SHA-256 `8355F0CB2E965710B1FDF74C97577F3A3D7604E7458741003386F5D184A3130A`. Thirty desktop Rust tests, eleven Chromium checks, formatting, Clippy with warnings denied, the frontend build, and the zero-warning Release build passed. The staged executable passed all six self-check stages twice. The matching Personal editor is in the same preview folder.

## Preview 1.18 paired foreground playback

The preview directory contains `Zenith.Sessions.Preview.exe` and the matching `Personal\MacroSuite.App.exe`. Keep Personal's files together. The Personal preview requires the .NET 8 Desktop Runtime and an active Personal license. Close older instances before opening the matching pair. The Sessions preview continues to use its existing preview library. The paired Personal executable uses the normal Personal macro library.

1. In the matching Personal editor, save an enabled **Active external window** macro with ordinary letter, digit, arrow, or space actions and finite delays. Leave the macro saved and keep Personal open.
2. In Sessions, launch one designated account using the standard Roblox player and wait for **Arrival confirmed**. Open **Automation**, refresh the saved catalog, select the macro, and assign it to that account. Select **Review playback targets**.
3. In Personal, select **Pair Sessions** and copy the one-time code. Close the pairing dialog. Paste the code into **Automation** in Sessions and select **Start macro** within two minutes. The control focuses the exact reviewed window before asking Personal to start.
4. Check the intended input in Roblox. Switch focus away while the macro is running and confirm it pauses. Use **Resume macro** to focus the same window and resume. Select **Stop macro** and look for the completed cleanup result. Review and pair again for another run. For Panic, start a fresh trial and press the Sessions shortcut while it runs. Verify release in Personal and the Sessions hold. A closed connection asks Personal to stop and join, while a disconnected Sessions view cannot report a terminal acknowledgement.

Personal binds the control peer to the exact packaged Sessions SHA-256 and checks the direct Roblox child, Windows process creation time, and native window. The Rust path requires a confirmed session and consumes a review token tied to that exact HWND, attempt, macro revision, protected assignment snapshot, and Panic generation. The launch provider applies the standard player signature gate. [Windows process snapshot identities](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/ns-tlhelp32-processentry32w), [Control protocol](FOREGROUND-CONTROL-PIPE.md)

The browser fixture fails on the previous frontend and passes on 1.18 with Start, Pause, Resume, Stop, and late review after Panic. The native ownership fixture used a disposable process and window, checked wrong peer hash, replaced process time or HWND, and then verified rejection after exit. Controlled cross-runtime fixtures passed. A live Roblox input trial and direct interaction with the paired Personal window remain pending.

## Preview 1.15 playback target review

Open **Automation**, refresh the Personal catalog, and select the assigned macro. Use **Review playback targets** on the account, imported group, or imported bundle you want to check. For an inherited reference, save the exact current macro revision to that row first. If the macro changes or is disabled in the editor, Zenith explains why the review cannot proceed.

Review uses the chosen row's scope. It checks the current protected assignment snapshot and the saved macro ID and revision against the editor. Imported bundles use their stored profile IDs in order. Imported groups use the archive's ordinal case-insensitive group names. An imported member matches a current account only through its verified Roblox user ID. Missing or unlinked members appear in the result. Review supports up to 32 recipients. [Windows ordinal string comparison](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-comparestringordinal)

The review distinguishes no active session, unconfirmed initial arrival, and an unavailable or ambiguous owned window. An available window requires a live process held by the current attempt, exact account and process identity, confirmed initial arrival, and a single visible, non-minimized, unowned top-level window for that process. Each result is a snapshot. Panic invalidates pending reviews. The Personal pipe still provides saved metadata only, and playback requires a production foreground transport through the existing engine. [Windows top-level window enumeration](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows)

For the attended review, select a designated saved account and macro in the compatible Personal editor and preview. Check the assignment before launch, then review again after the designated account has a confirmed arrival. Minimize its window and repeat, then restore it and repeat once more. An imported group or bundle should show its exact saved recipients, including unavailable members. Change the macro in the editor and verify that reviewing the old selection requires a refresh and saving the new revision. These steps exercise review with actual accounts and clients and require an attended trial.

The 1.15 executable has SHA-256 `D3424BEDA92C14E99DCABC367BD60136A1A8BCD9161BE973C6E29DB21ACCAAC9`. Fifty-five offline Rust tests, the .NET catalog and C# migration fixtures, ten Chromium checks, formatting, Clippy, the frontend build, and a zero-warning Release build passed. The browser target-review check failed on 1.14 and passed on 1.15, including a response arriving after Panic. Both packaged self-checks passed all six stages.

## Preview 1.14 account details and sign-in

**Accounts** opens with the first saved profile visible in the inspector. Select another account's name or headshot to see its Roblox display name, profile details, public favorite games, friends, and Robux balance. Select a favorite to use it as the launch destination. Refresh in the inspector checks the details and headshots again. The account list and inspector use Roblox's avatar headshot for the saved user ID. Initials remain visible while the image loads or if Roblox cannot provide it. [Roblox headshots](https://create.roblox.com/docs/cloud/reference/features/avatars), [Roblox user data](https://create.roblox.com/docs/cloud/reference/features/users)

When an isolated sign-in reaches Roblox Home, Zenith verifies the identity, saves the protected sign-in, then requests closure of the sign-in window and identifies the account that was saved. Temporary network or validation failures get up to two automatic retries. Challenges, rate limits, and local save failures display the reason for the stopped attempt. Use **Retry save** after resolving the issue.

The Chromium fixture reproduced an empty initial inspector, an image failure stuck on initials, and missing save confirmation in 1.13. The updated fixture checks all six synthetic accounts individually and tests that late results for a previous account cannot populate the next account's details. An anonymous Roblox request verified the public profile, headshot, and favorites endpoints. Controlled Windows and Rust tests exercised protected persistence and the retry policy.

The 1.14 executable has SHA-256 `8C2555994BBAFC0D6B397CB97F9C9C05690FE726B3A765C39179D919A1A4A4F2`. Fifty-four offline Rust tests, nine Chromium checks, formatting, Clippy, the frontend build, and a zero-warning Release build passed. Both packaged self-checks passed all six stages.

For the attended check, open 1.14 under the same Windows user as the previous Rust preview. In **Accounts**, select each saved profile and compare its headshot, favorites, friends, and balance with Roblox. Renew one designated account through **Add account**. After reaching Home, check that the sign-in window closes and the saved-account notice names that account. Reopen the preview and verify the account and its details are still available. Real private reads and automatic closure against a live Roblox sign-in remain to be checked.

## Preview 1.13 macro assignments

Open **Automation**, refresh the Personal catalog, and choose **Select macro**. Under **Macro assignments**, select **Assign selected macro** or **Replace assignment** on a saved account, imported group, or imported bundle. **Remove assignment** clears the preview's effective reference and works while the editor is closed. The preview pages through targets in groups of 25.

A save rechecks the selected macro ID, saved revision, and exact editor catalog page. A changed or closed editor requires a refresh before saving a new selection. The local edit also checks the assignment and target snapshot before committing. Assignments carry a saved macro reference and a display label. Saving one grants no playback authority.

Preview edits are stored separately in a current-user encrypted `library\macro-assignments.dpapi` file. Imported references remain in the protected migration archive. An imported account reference attaches to a current account only when its verified Roblox identity matches. Other references with missing or unverified targets appear as archived. Group and bundle references use their imported IDs. Changes in this preview do not update the original Personal or Sessions data. The 1.12 executable can still read the original archive and account library. The 1.13 assignment overlay is used only by previews that support it.

The browser fixture reproduced the missing assignment controls in 1.12 and exercised selecting, saving, and removing a reference in 1.13. Native storage fixtures covered stale edits, restart, invalid files, verified legacy-account mapping, and preserving the original archive. The .NET catalog fixture accepted the exact saved macro and rejected a mismatched revision. The C# migration fixture verified both a linked account reference and an archived password-protected reference.

For the attended check, open a compatible Personal editor and choose a designated saved macro. Assign it to an account in Automation, reopen Zenith and check that the row still shows the same reference. Refresh the catalog, select a macro, then save a changed revision in the editor before clicking **Replace assignment**. Verify the preview asks for a catalog refresh and leaves the old assignment in place. Close the editor and remove the assignment. For a migrated workspace, repeat on an imported group or bundle and compare the original legacy reference. Installed-editor interaction, assignment precedence for playback, and execution through the existing macro engine remain pending.

The 1.13 executable has SHA-256 `DFA6C1C080E69F5C4843987437BBCF445D8FB75261B698A9DA4893954E5A774D`. Fifty-three offline Rust tests, the .NET catalog and C# migration fixtures, six Chromium checks, formatting, Clippy, the frontend build, and the zero-warning Release build passed. Both packaged self-checks passed all six stages.

## Preview 1.12 saved macro catalog

Open the **Zenith Macros Personal** editor, then choose **Automation → Refresh catalog** in the preview. The catalog shows saved macro names, target modes, hotkeys, top-level action counts and enabled status. Use **Next page** and **Previous page** to browse in groups of 25. If the editor restarts or its catalog changes during paging, the preview clears the old rows and asks for a refresh. A disconnected or unavailable host has its own status. Opening the page waits for a manual refresh.

The Rust preview uses the Personal editor's existing v1 local named pipe with anonymous impersonation and compares its Windows owner SID to the current process user SID before writing the request. It accepts one framed metadata request at a time with a three-second total budget. Request IDs, the host session, exact catalog revision, page size and offsets are checked before showing results. The named pipe boundary separates Windows users. A same-user process can still claim the metadata endpoint, so displayed records carry no execution authority. [Windows named-pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights), [Tokio named-pipe security options](https://docs.rs/tokio/latest/tokio/net/windows/named_pipe/struct.ClientOptions.html#method.security_qos_flags)

The catalog carries display metadata. Macro documents stay in the Personal editor. Assignment editing and playback remain pending. A migrated workspace still shows its preserved assignment-reference count. The existing editor and the preview library keep their current files and formats.

The Chromium fixture first failed against 1.11 because Automation was missing. It passes on 1.12, including explicit refresh, disconnected status, and clearing a changed page. The Rust client also read the existing .NET 8 Personal catalog fixture and received the expected `Changed` response for a stale host session. Native pipe fixtures cover oversized frames and a stalled response. Codec checks cover duplicate or missing fields and the exact 64-bit catalog revision. Cross-user Windows testing was outside these fixtures.

For an attended check, start the compatible Personal editor with a designated saved macro, then refresh Automation and compare its row with the editor. If the editor has over 25 saved macros, change one saved macro in the editor before selecting **Next page** in Zenith. Verify the changed-catalog message, then refresh and check the updated row. Close the editor and refresh again to check the disconnected state. Using the installed editor and its actual saved catalog remains an attended check.

The 1.12 executable has SHA-256 `04367C0149F944962C7162F1B7D9DB876F97C10D42C134FE1B772E9D91CEA7C9`. Fifty-one offline Rust tests, the separately run .NET 8 fixture, five Chromium checks, formatting, desktop Clippy, the frontend build, and the zero-warning Release build passed. Both packaged self-checks passed all six stages.

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
