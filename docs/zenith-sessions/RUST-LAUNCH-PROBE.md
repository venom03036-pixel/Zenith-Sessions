# Rust launch probe and Tauri setup

The Rust application lives in [`apps/zenith-sessions`](../../apps/zenith-sessions). The first build is a development probe. The desktop executable shows real Windows launch admission data. The companion console executable tests one saved account and a public place before the replacement account library or interface is wired up.

## Build and reproduce

The tested machine has Rust 1.99.1, Tauri CLI 2.12.4, Visual Studio Community with MSVC, WebView2, Node 20.20.2, and the existing .NET 10 SDK. In the integration checkout, run the following in PowerShell. Add `$env:USERPROFILE\.cargo\bin` to the process `PATH` if Cargo is unavailable in a terminal opened before installation.

```powershell
dotnet build apps/zenith-sessions/tools/LegacyProbeBundle/LegacyProbeBundle.csproj -c Release -m:1

Push-Location apps/zenith-sessions/launch-core
cargo fmt --check
cargo test --locked
cargo clippy --locked --all-targets -- -D warnings
cargo build --locked --bin launch-probe
Pop-Location

Push-Location apps/zenith-sessions
npm ci --ignore-scripts
cargo tauri build --no-bundle
Pop-Location
```

The local HTTP fixtures need access to `127.0.0.1`. In the managed sandbox those connections returned a network error. The same tests passed with ordinary Windows loopback access. `target/release/zenith-sessions-desktop.exe` under `src-tauri` is the self-contained desktop executable for this probe and uses the installed WebView2 runtime.

The executable reads Windows physical and commit headroom through the Rust backend. The only frontend permission is the local diagnostics window's memory command. The client probe uses Reqwest with Windows TLS for bounded HTTPS requests to the fixed Roblox hosts, `windows` bindings for DPAPI and publisher verification, and `zeroize` for owned secret buffers. Tauri supplies the native WebView host. The existing .NET vault is used solely to create the encrypted handoff.

## One-account live procedure

1. Close legacy Zenith, RoALT, and all Roblox players. Confirm they are gone with `Get-Process -Name Zenith.Sessions.App,RoALT,RobloxPlayerBeta -ErrorAction SilentlyContinue`. Launch the reference and probe in separate runs. Use the same authorized account, destination, player version, and graphics settings when comparing.
2. Stage only the designated account with `$bundle = & ./apps/zenith-sessions/tools/Stage-LaunchProbe.ps1 -Username sethplayzfan`. The script takes the legacy app's exclusive instance lock while copying the account database and encrypted vault to an ignored directory. The helper creates a current-user DPAPI bundle. It requires one exact profile and rejects expired, mismatched, or password-locked sign-ins. Keep the existing app closed during the trial. Keep the snapshot local and out of shared archives.
3. Use `--registered-player` to follow Roblox's official current-user installation record. To pin the same version as a comparison run, pass its explicit executable path. `--player-check --registered-player` checks the selected file and publisher without launching it.
4. Run the command below. Include `--try-one` only for a deliberate single-client trial when the reading offers it. The executable takes a fresh reading before authentication and again before native startup. It records only stage names, timings, the exact spawned process receipt, and parsed identity and destination facts. It stops the exact process it created at the end of the trial.

```powershell
$probe = './apps/zenith-sessions/launch-core/target/debug/launch-probe.exe'
$logs = Join-Path $env:LOCALAPPDATA 'Roblox/logs'
& $probe $bundle sethplayzfan 110829983956014 --registered-player $logs
```

The result is a successful launch trial only if the probe prints `Confirmed arrival` for the requested account and place. Startup without that evidence is an incomplete trial. A Roblox account challenge or sign-in expiry requires an attended renewal. The probe handles cookie rotation within an attempt. Durable rotation and protected password migration are part of the replacement vault work.

For rollback, close the diagnostics app and use the existing Zenith executable and original data directory. The probe writes its staging data separately. It never imports the historical launch queue or modifies the original stored credential.

## Evidence

- Existing Zenith base `60a721a3e2e7b64bb0bd2d8acdefab15b863af85`, existing executable SHA-256 `41D1A91EBD1417E6526AFCBF1621320A4FB81AA8BC9481736E4EF84122AF621C`.
- Installed RoALT 1.1.0 executable SHA-256 `A7E58EAE8B4D73F8F4BCA0F65483740E54555CAC4542482E1BD3F94EA797E98D`. The [pinned public source](https://github.com/inspectsoftware/RoALT/tree/d16b3e277ce24859a5159354a6f289891b09fe2f) was consulted for behavior. The probe ports Zenith's own launch request and publisher checks. The local RoALT 0.4.0 folder describes an older release.
- The first trial used the older stock player `version-bc1bf755618240e8`, SHA-256 `57E5C4203D420815D7704FA53FA1A63E09D1190E4A55439356B41B1101AF2120`. Roblox verified the saved account and issued a ticket. The old process started, detected a required update and exited. The replacement game log carried the exact tracker from the first request and reported Seth's user and requested place. The replacement window showed Seth in the game. The probe did not establish process ownership across that handoff, so the first attempt counts as an incomplete managed cycle.
- Roblox then registered `version-f0ea5da9412f4e35`, SHA-256 `F98B351FEBDE8A4F89F8B25B8B07AF62E6DD772CC3EEE374A4F77F0DCF477713`. With that publisher-verified player, **10 of 10 authorized direct launch-and-stop cycles passed**. Each observed the exact requested Roblox user and place with the probe’s fresh tracker, its own process and visible window, and a connected game session. Overall there were 11 live attempts including the cold update handoff. The ten successful arrival times were 5.24–6.54 seconds, with a 5.38-second median. All ten owned processes stopped. The probe binary used for those cycles had SHA-256 `04BDD33DEFC62456A3A03BEA0E002CEE8226131126D541C84F6AF3369198AA3E`.
- Eight Rust tests passed. The initial-arrival test failed before the parser change because a later teleport erased the initial destination. The final parser preserves the initial join boundary. The HTTP fixtures checked identity mismatch, an account challenge, and cookie rotation through one CSRF retry. The registry fixture checks that a quoted executable path can be read as a path and that a command line is rejected.
- The C# Release helper built with zero warnings. A synthetic fixture was written by the legacy vault API, staged by the helper, and read by the Rust DPAPI adapter. Reopening the same bundle as another account failed. All 83 focused legacy vault and ticket tests passed with host loopback access. The initial sandboxed testhost connection attempt aborted. The paid cloud AI test was outside the filter.
- The release Tauri executable opened outside the development server. Its refresh button requested a new real sample through IPC. One reading showed 4.95 GiB available physical memory versus 8.89 GiB required and 15.89 GiB available commit versus 9.29 GiB required.
- Figma returned the Make source manifest. Its screenshot tool does not render Make files. The owner-supplied export was decoded and its commit and pack hashes verified. The extracted React and CSS reference is staged at `artifacts/zenith-ui-reference/1d56d914aeb5717c6918c61e5cb0f4d5fc775b0a` for the UI integration.
- The initial preflight held while an existing Roblox process was open. It sent no launch request in those checks. The recorded live attempts are `artifacts/rust-launch-probe/single-account-01.txt` through `single-account-11.txt`. Multi-client results and RoALT timings remain pending.

## Next gates

Use a second designated account for the five two-account batches before completing the broader UI and migration. Roblox update handoff ownership still needs a production solution. The redesigned UI, durable account storage, password migration, singleton coordination and takeover, window tools, private joining, performance controls, and the sixteen-account validation remain open. See the [feature inventory](ROALT-1.1-FEATURE-INVENTORY.md) for the wider comparison.
