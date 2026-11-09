# Zenith Sessions desktop preview

The rebuild branch now has a Tauri desktop workspace with the exported Zenith colors, typography, navigation and account table. It runs separately from the existing WPF app. The preview's library lives under the Windows local application data directory at `com.zenith.sessions.preview\library`.

## Using the preview

1. Sign in through Roblox inside the existing Zenith app. Exit the existing app after the sign-in completes. The staging script needs its exclusive instance lock.
2. From the repository root, run the following command for the account you want to import. The [build and staging instructions](RUST-LAUNCH-PROBE.md) show how to build the existing vault helper first.

   ```powershell
   & ./apps/zenith-sessions/tools/Stage-LaunchProbe.ps1 -Username 'YOUR_ROBLOX_USERNAME'
   ```

3. Open the preview and choose **Import saved sign-in**. Select the `probe.dpapi` file returned by the script. It is protected by Windows for the current Windows user. Keep the staging directory local.
4. Under **Accounts**, choose **Select for launch**. On **Sessions**, check the selected account, enter the place ID and select **Launch**. For the authorized Anime Card Clash trial, use `110829983956014`.
5. Close any other Roblox players, the existing Zenith app and RoALT before the preview starts Roblox. Overview shows the actual memory headroom. **Try one account anyway** appears only when the physical-memory reserve is the limiting factor and the hard physical and commit requirements still pass.

The preview rechecks the signed standard Roblox player and verifies the saved account with Roblox before starting it. **Arrival confirmed** requires the owned process and window plus attributable client evidence for the initial account and place. **Running · unconfirmed** means there was a process receipt and Zenith could not confirm arrival. Stop targets the process this attempt started. If Windows declines Focus, use Roblox's taskbar window.

The account list contains real imported identities. Removing an account from the preview removes its preview sign-in and profile. A damaged credential leaves the profile visible and is refused at launch. If Roblox requires sign-in again, renew in the existing app and import a fresh bundle into the preview. The preview is still an isolated import path. It does not replace the complete migration of groups, notes, settings, macro assignments, or password-protected records.

## Build and validation

Run `npm ci --ignore-scripts`, then `cargo tauri build --no-bundle -- --locked` from `apps/zenith-sessions`. The release output is `src-tauri/target/release/zenith-sessions-desktop.exe`. The installed WebView2 Runtime is required.

The Rust checks cover cancellation during preparation, late results from an older attempt, DPAPI import and reopen, duplicate account rejection and preservation of metadata when a credential cannot be read. The native launch core uses the same registered-player, ticket and attribution mechanism as the [ten successful single-account trials](RUST-LAUNCH-PROBE.md). The new desktop controls still need their own attended launch, focus, stop and close trials.

The release executable opened the Accounts view and its native protected-file picker on the host. The preview's initial library was empty. Release SHA-256 `0494F34C3FF6193A20CE2720EC1A675C8A33A2852971DB82EA2AA4E824C586C9` identifies this first desktop workspace build.

The two-account command is built for a separate attended trial. The desktop launcher currently admits one client. The second authorized account, multi-client launch and takeover, full data migration, game and server browsing, previews and tiling, automation and performance controls remain open in the [feature inventory](ROALT-1.1-FEATURE-INVENTORY.md).
