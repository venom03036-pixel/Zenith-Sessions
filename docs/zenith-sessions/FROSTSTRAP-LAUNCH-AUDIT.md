# Local launch-source audit, cycle 0.40

Reviewed November 6, 2026. This is static, read-only source investigation, not a native launch, account sign-in, arrival, compatibility or optimization result. Neither reference application was executed or changed. No reference code was copied into Zenith. The repositories' working-file hashes below do not establish a clean tree or equivalence to installed binaries.

## What the existing Zenith launcher does

`WindowsNativeAccountLaunchProvider` already composes a held, publisher-verified executable with an owned official-page handoff preparation, final protected-grant binding and one-use dispatch. `WindowsConfiguredNativeLaunchProvider` discovers/selects an installation and intentionally accepts the Roblox player rather than arbitrary bootstrapper executable names. Both advertise **Unverified** account launching; ordinary `LaunchAsync` rejects and `ConfirmArrivalAsync` returns false. The selected-account explicit trial is routed by `ShellNativeTrialFeatures`; it is not an ordinary enabled group launcher.

The inspected `artifacts/sessions-native-client-observation.json` records `ExecutableStarted=false`. The older distribution handoff check is synthetic/capture-policy evidence. Neither establishes a failed real Roblox startup. Consequently there is no evidence that a missing custom bootstrapper is the cause of the user's launch problem. A real trial still needs stage-specific observations: selected installation/trust, owned page preparation, accepted opaque handoff, current final grant, dispatch outcome, and observed process/start identity. Even a process receipt does not prove the intended player or destination arrived.

## Froststrap's useful separation

The local Froststrap source separates protocol argument parsing in `Froststrap/LaunchSettings.cs` (reviewed lines164–195), installation/update lifecycle and executable selection in `Froststrap/Bootstrapper/Bootstrapper.cs` (613–700), and account switching in a different integration. Argument ownership, channel/install lifecycle, cancellation and distinguishing the bootstrapper PID from its eventual player PID are useful independent design ideas.

`Froststrap/Integrations/AccountManager/AccountCookieWriter.cs` and `CookiesManager.cs` instead implement account switching by replacing Roblox's shared `RobloxCookies.dat`. This is not a separate Roblox installation per saved account. Zenith's current requirements exclude modifying those shared login files, so that mechanism is not integrated. No compatible independent production multi-instance implementation was established by this audit.

The local license is MPL-2.0. Source reuse would require reviewing covered-file obligations and dependency notices before redistribution. No such reuse happened in this cycle; these are factual design observations, not advice that all code can be copied freely.

## RoALT's distinct mechanisms

RoALT's `src-tauri/src/launcher/mod.rs` (139–212), `launcher/client.rs` (149–168), and `launcher/uri.rs` separate selected-account handoff preparation, URI composition and native executable start with a process/start receipt. That does not by itself prove process/account association or arrival. Its `instances/multi.rs` separately holds the singleton object, can take over handles, and locks shared login files. These remain outside the agreed implementation boundary. Temporary client settings are another independent subsystem, and their presence is not measured resource improvement.

The right next native step is evidence from the existing explicit trial, followed by the smallest correction to the failing stage. The launch providers should not be replaced, enabled as qualified, or given a public-server fallback merely because a reference app contains these other mechanisms. Protected private destinations, selected-account/PID association, current native handoff compatibility, verified arrival and reconnect still require implementation and live evidence.

## Pinned local evidence

- Froststrap HEAD reference: `02028c4b0b909a218d92a5f87170063597f738cb`.
- Froststrap `LICENSE`: SHA256 `1F256ECAD192880510E84AD60474EAB7589218784B9A50BC7CEEE34C2B91F1D5`.
- Froststrap `Bootstrapper/Bootstrapper.cs`: SHA256 `61EF3067C8A97EE3B2ED898BE716C65933DB7E21BBD05A84B6557C4F9A64C0AA`.
- Froststrap `AccountCookieWriter.cs`: SHA256 `455D918ED3390838ADEAF85B26A5A55BC8876F3A181B026DECB11471FA4D8C46`.
- RoALT HEAD reference: `2e304a0c2c5ab90e40b63a982f81c03e992270d4`; reviewed Cargo version1.0.1.
- RoALT `launcher/mod.rs`: SHA256 `D34E4A2D3798CFC70A26C12C04262FA1FE2AD5DFF5DE20B64C6BF10E1EDAA3AF`.
- RoALT `launcher/client.rs`: SHA256 `53FE92CBD4E668C95E8E54146F59309E5A041593D7075D6093C5BA06B99CADEE`.
- RoALT `launcher/uri.rs`: SHA256 `4F040A6660060646ADBB6B18FEF2F509492EF27D7C8117F574183FCAA891117E`.
- RoALT `instances/multi.rs`: SHA256 `6C0BBDD03F0AFF028791B77D1305415C909D1B26505D60262FB2D874C722F662`.

Paths are relative to the owner's local repositories under `C:/Users/noelm/OneDrive/Documents/GitHub`. The independent reviewer supplied source ranges and hashes; the root also checked the local license, source locations and current Zenith provider gates. No secrets, authentication values, full native command lines or reference executables were collected.
