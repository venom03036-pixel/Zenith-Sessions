# Zenith Sessions capability harness

This is milestone 1 of the approved personal Windows companion plan. The plan requires proven background macros and unattended correct-account reconnect before full implementation. This executable records the current blockers; it is not a launcher or the companion UI.

## Run

Requires .NET 10 SDK. From the Personal source directory:

```powershell
.\scripts\Check-ZenithSessionsCapabilities.ps1
.\scripts\Check-ZenithSessionsCapabilities.ps1 -RunNativeProbe
dotnet test .\tests\Zenith.Sessions.CapabilityHarness.Tests\Zenith.Sessions.CapabilityHarness.Tests.csproj --configuration Release
```

Outputs are `artifacts/sessions-feasibility/feasibility.json` and `feasibility.md`. Repeated runs replace these reports instead of creating runtime extractions. Exit code **3** means the inspection completed but the release gate is blocked. Code 2 means the harness/configuration failed; 130 means cancellation. This version has no option to import evidence or override a failed gate.

## What actually executes

- Bounded metadata discovery of known Win32 client files, protocol registration keys, named Roblox processes and their window geometry. Access-denied results are recorded explicitly. File versions and process counts do not prove client authenticity, account identity or multi-client capacity.
- The real `MacroSuite.Core` playback/targeting code with invented windows and an in-memory sink: focused baseline, focus-loss release, blocked unfocused input, manual resume, stop cancellation, geometry generations and reused-process rejection.
- A fail-closed assessment of every required live exercise. Controlled tests, installed files and documentation cannot approve release.

The CLI itself does not send native input, capture pixels, activate windows, launch clients, change registrations, read log contents, inspect client memory, collect authorization or modify the installed app. The optional separate NativeProbe creates its own temporary message receivers and captures only its own window, without foreground activation. See `../Zenith.Sessions.NativeProbe/README.md`. The optional runner path requires PowerShell 7. No new production NuGet dependency is added.

## Updated evidence contract

Report schema 2 records the approved sixteen-client target and four test place IDs, shared macros plus live key/click broadcasting, per-client image decisions and arrow-key camera scope. `FeasibilityDecision` permits proceeding only after real-client proofs; `ReleaseReady` remains false until product implementation and sixteen-client acceptance. Schema-1 reports are historical snapshots, not inputs to this evaluator.

The gate rejects status labels without recent collector evidence, repeated trials, independently verified outcomes, distinct account/process instances and hashed artifacts. Input, capture and launch adapters form one versioned configuration fingerprint; they need not be the same component. Artifacts are written atomically, read back and rechecked for modification. These checks validate evidence integrity and required context, not the truth of an arbitrary caller's claims. There is deliberately no CLI route to import claimed live successes or override a gate. A future live adapter must collect actual observations.

`--native-report <file>` accepts only a bounded, recent `NativeControlledExercise` report, with strict schema and origin checks. It is displayed separately and cannot satisfy any Roblox gate. Repeated runs replace fixed report files; failed native runs cannot silently reuse the previous report. Exit 0 from the native process means its own checks passed, not that a live gate passed.

## Next engineering step

See `docs/zenith-sessions/LIVE-EXERCISES.md`. A supported background input/capture provider and an independently verified account-specific reconnect provider must exist before those exercises can run. Do not bypass the current foreground checks or substitute server teleports for disconnected-client reconnect. The approved plan stops at this report while either requirement remains unavailable.
