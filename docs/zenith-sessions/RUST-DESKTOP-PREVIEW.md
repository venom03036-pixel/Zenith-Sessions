# Zenith Sessions desktop preview

The rebuild branch now has a Tauri desktop workspace with the exported Zenith colors, typography, navigation and account table. It runs separately from the existing WPF app. The preview's library lives under the Windows local application data directory at `com.zenith.sessions.preview\library`.

## Using the preview

1. Close any older Zenith preview window before opening the new executable.
2. In **Accounts**, select **Add account**. Complete sign-in in Roblox's own window. Return to Zenith and select **Save sign-in**. Zenith verifies the authenticated account ID with Roblox and saves its credential using Windows encryption. To renew a saved profile, use **Add account** again and sign in as the same Roblox account.
3. To bring over an existing saved sign-in, exit the existing Zenith app. From the repository root, stage that account with the command below. The [build and staging instructions](RUST-LAUNCH-PROBE.md) show how to build the existing vault helper first.

   ```powershell
   & ./apps/zenith-sessions/tools/Stage-LaunchProbe.ps1 -Username 'YOUR_ROBLOX_USERNAME'
   ```

   In the preview, choose **Import saved sign-in** and select the returned `probe.dpapi` file. It is protected by Windows for the current Windows user. Keep the staging directory local.
4. Open **Sessions** and choose the account. Search by game name and select a result, or enter a place ID or public Roblox game link. For Anime Card Clash the place ID is `110829983956014`. To target a specific server, choose **Browse public servers** and select a row. **Use any server** clears that selection. Select **Launch** when ready.
5. Close any other Roblox players, the existing Zenith app and RoALT before the preview starts Roblox. Overview shows the actual memory headroom. **Try one account anyway** appears only when the physical-memory reserve is the limiting factor and the hard physical and commit requirements still pass.

The preview rechecks the signed standard Roblox player and verifies the saved account with Roblox before starting it. **Arrival confirmed** requires the owned process and window plus attributable client evidence for the initial account, place, and server if specified. A confirmed mismatch stops the owned process. **Running · unconfirmed** means there was a process receipt and Zenith could not confirm arrival. Stop targets the process this attempt started. If Windows declines Focus, use Roblox's taskbar window.

Server browsing requests one public page at a time. Occupancy is reported by Roblox when the page loads, and seats are assigned when Roblox joins the server. Changing the destination clears the selected server. The initial game-name results are capped at 50. Public links with query parameters and private/share links are rejected by this preview.

The account list contains authenticated or imported identities. Removing an account removes its preview sign-in and profile. A damaged credential leaves the profile visible and is refused at launch. Renewal replaces the encrypted credential for the same Roblox user ID and keeps the existing profile. The isolated import path does not replace complete migration of groups, notes, settings, macro assignments, or password-protected records.

## Build and validation

Run `npm ci --ignore-scripts`, then `cargo tauri build --no-bundle -- --locked` from `apps/zenith-sessions`. The release output is `src-tauri/target/release/zenith-sessions-desktop.exe`. The installed WebView2 Runtime is required.

The Rust checks cover cancellation during preparation, late results from an older attempt, DPAPI import and reopen, protected renewal, first-party sign-in navigation and preservation of metadata when a credential cannot be read. The native launch core uses the same registered-player, ticket and attribution mechanism as the [ten successful single-account trials](RUST-LAUNCH-PROBE.md). Live sign-in and the desktop launch, focus, stop and close controls still need attended trials.

The release executable opened the Accounts view and its native protected-file picker on the host. The preview's initial library was empty. Release SHA-256 `0494F34C3FF6193A20CE2720EC1A675C8A33A2852971DB82EA2AA4E824C586C9` identifies this first desktop workspace build.

The updated release built with the direct sign-in action. In a host smoke check, **Add account** opened the separate **Sign in to Roblox · Zenith Sessions** window, and its document reported `https://www.roblox.com/login`. Fifteen Rust tests and the frontend build passed. Release SHA-256 `622BF38D24B7EA29B79A1C856A2EE51BE3B091D2A7F5618709D2A2F790F7E037` identifies this build. The smoke check reached the login page without entering credentials.

Preview 1.03 adds anonymous public game search, public link and place ID input, and paginated public server browsing. The anonymous adapter smoke check found Anime Card Clash and returned public server rows. The same-place, wrong-server regression failed before the arrival check changed and passed afterward. The attended exact-server trial is to select a designated saved account, search for Anime Card Clash, choose a listed server with space, start Roblox, and compare the requested server with the initial server confirmed from attributable client evidence. Repeat using **Use any server** to check that the request does not carry the prior server.

The 1.03 executable has SHA-256 `6948202D1CF5F01CBC8FE31ED7F782F8C8DFF5E21D75EFE594FC7B1986040656`. Nineteen offline Rust tests, a separate anonymous endpoint smoke test, formatting, Clippy with warnings denied, the frontend type check and build, and the Tauri Release build passed. The 1.02 sign-in window was left open for the owner, so the 1.03 desktop interaction check is pending.

The two-account command is built for a separate attended trial. The desktop launcher currently admits one client. The second designated account needs a saved sign-in through the official Roblox window. Multi-client launch and takeover, full data migration, private-server joining, previews and tiling, automation and performance controls remain open in the [feature inventory](ROALT-1.1-FEATURE-INVENTORY.md).
