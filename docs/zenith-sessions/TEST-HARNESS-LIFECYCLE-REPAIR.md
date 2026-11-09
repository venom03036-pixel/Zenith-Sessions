# Offline fixture lifecycle follow-up (0.79-harness)

This follow-up addresses concrete setup and cleanup defects found while validating the live-trial repairs. It does not change the user's UI design, Roblox authorization, native launch grammar or production input policy. Required assertions, test coverage and the paid-cloud exclusion remain in force.

## Retained unsuccessful runs

- `artifacts/completion-integration/final-pass-9`: incomplete gate on `bf193748`. The Sessions test host crashed after 3,413 recorded passes. The TRX run outcome is Failed, and the parent was cancelled after a CLIXML stderr parsing error stalled its progress. It is not a successful first pass.
- `artifacts/completion-integration/final-pass-10`: failed gate on `295c049`. The same WPF host exception occurred after 3,202 recorded passes. The gate recorded the failed suite and refused package acceptance. The other five suites, Release builds, tool publish and Luau checks passed, but that does not qualify the candidate.
- `artifacts/completion-integration/live5-wpf-diagnostic/sessions-blame.trx`: diagnostic full Sessions run with test-sequence logging; 7,170 passed, two failed, one existing opt-in crash-test skip. No WPF host abort occurred in this run. The two failures were the targeted-input stalled-shutdown fixture and a five-second initialization containment timeout in `SavedSessionPublicationRaceTests`. The latter's assertion and time limit remain unchanged; its exact delay source was not established.

The WPF aborts reported `Dispatcher processing has been suspended, but messages are still being processed`. Source inspection found raw STA render fixtures without the WPF synchronization context, early completion before dispatcher shutdown, and one helper with no dispatcher shutdown. These are concrete fixture defects and a plausible mechanism for the abort, not proof of the exact crashing fixture. WPF's [DispatcherSynchronizationContext implementation](https://raw.githubusercontent.com/dotnet/wpf/main/src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Threading/DispatcherSynchronizationContext.cs) prevents the CLR's contended wait from pumping sent messages while dispatcher processing is disabled. An STA thread alone does not establish that context.

## Scope and acceptance

The repair must establish the context before creating WPF objects and retain it through cleanup, await dispatcher retirement before a fixture reports completion, and propagate phase/dispatcher failures. Existing layout, accessibility, theme and geometry assertions must be preserved. Resource collection membership and unrelated test parallelism remain unchanged.

The shutdown-timeout fixture must establish that cleanup has actually stalled before expiring its stage token. Its original 200 ms owner-disposal bound and exact timeout/cause/method assertions remain unchanged. Cancellation before any input hold exists is a separate control and cannot count as a stalled owner.

## Executed evidence

- `artifacts/completion-integration/live5-harness-repair/red-host/fixture-red.trx`: **5 failed, 8 passed, no skips** on unchanged production `295c049`. The create/cleanup phases had null synchronization contexts; the UI helper completed while `ShutdownStarted` was deliberately held. Both timeout assertions instead received `InvalidDataException` when setup was held until the old 200 ms stage deadline. That red fixture is retained as `PackagedTargetedInputCheckTests.red.cs` beside the TRX. The earlier test-host connection abort ran zero tests and is not red proof.
- `artifacts/completion-integration/live5-harness-repair/green-host/fixture-green.trx`: **400 passed, no failures/skips**. This includes the unchanged new layout/lifecycle regression tests, every affected fixture family and the original saved-session publication races with their five-second initialization limit. An initial missing `System.IO` test import was corrected before this executed green run; a compile error is not behavioral evidence.
- All 206 original assertion-bearing lines across the five migrated fixture files compare identically after trimming indentation. Existing collection membership, outer time limits, test names and unrelated parallelism remain intact. The shutdown-timeout assertions still require exact timeout type, original cancellation cause, owner method and error code; only their setup now waits for three submitted holds and entry into stalled cleanup before cancelling. A separate early-setup cancellation control requires zero submissions and no cleanup entry.

The shared runner now installs `DispatcherSynchronizationContext` before creation and retains it through cleanup and shutdown, returning phase/shutdown failures through the tracked task. Raw STA fixture wrappers and the early-finishing UI helper use that runner. Production input, shutdown bounds, UI markup and live launch admission are unchanged.

Full acceptance still requires an immutable independent review and two new complete runs on the final clean commit. The 400-case pass resolves the focused regression checks; it does not alone establish that the intermittent full-suite WPF abort is gone. No real account/client, paid cloud test or production installer is part of this offline work.
