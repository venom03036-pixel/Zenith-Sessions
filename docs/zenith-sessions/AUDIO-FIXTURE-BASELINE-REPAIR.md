# Native audio fixture baseline repair (0.74)

Source parent: 7d292a4b8e8555157b119bac5bd9e2071b710b0a. The integrated second gate failed in ClientAudioMuteNativeTests.LaterHelpersOfTheSameProgramAreUnmutedOnExitEvenWhenWindowsStartsOneMuted at its first Applied assertion. The failure said the sound was already muted and Zenith did not own it. This is an invalid fixture precondition, not evidence that production must take ownership of an existing user mute.

Windows can retain an executable's mute between helpers. This test did not normalize its first helper, whereas the neighboring native audio test already requested an unmuted baseline. The first helper must begin unmuted so the production control actually sends its first mute; only helpers created after that mute are candidates for inherited-mute ownership.

## Changes

Only ClientAudioMuteNativeTests.cs changed executable code. No production source, skip condition, timeout, retry policy, mutation permission or expected production result changed.

- The exact first owned helper PID is deliberately muted and read back before its baseline step. This deterministically represents the troublesome initial state without muting any unrelated process or depending on test order.
- That first helper alone is then explicitly unmuted and read back before the production control is created. Concurrent and later helpers still call StartAsync with default arguments, retaining Windows' actual inherited mute behavior.
- Existing Applied, RestorePending, muted-state, final-unmuted and pending-restore assertions remain. Control disposal is followed by a snapshot of surviving helpers before fixture cleanup; cleanup cannot make the restoration assertions pass.
- Cleanup attempts unmute and disposal independently for every owned helper, including the first one when an early assertion fails. Cleanup unmute calls now check native acceptance and read back sound state; failures are reported rather than silently ignored. An already disposed/exited helper is left alone.

## Genuine failing-before and passing evidence

Artifacts are under wt/D3/artifacts/sessions-tests-0.74-audiobaseline/:

1. audio-baseline-red.trx: **1 failed, 0 passed, 0 skipped**. The first helper was seeded muted, with unmutedBaseline still false. The unchanged Applied assertion failed with the same pre-existing-mute message as the integrated gate. The cleanup addition was already present, so the red run restored/stopped its fixture helper. Production source was unchanged from the parent.
2. audio-baseline-green.trx: **20 passed, 0 failed, 0 skipped** after enabling the first helper baseline. These are the two native owned-helper tests and eighteen fake-native audio owner tests; the partial presentation test class has another class name and is not claimed here.
3. audio-baseline-final.trx: **20 passed, 0 failed, 0 skipped** after strengthening fixture cleanup acceptance/readback. This is the final executable test source. The native tests executed, rather than skipping for no audio output device.

Commands used Release, --no-restore, -m:2 and -nr:false with exact audio test filters. Paid cloud tests were excluded. The final TRX counters were independently parsed; git diff --check passed. Before/After 0.74 source-inventory fingerprints match. Full integrated source gates must be rerun on the final merged candidate; the earlier failed pair is not accepted.

## Boundaries

Native Core Audio reads/writes addressed only exact PIDs of newly created silent PowerShell fixture helpers. Each helper's own sound was restored before it exited except the intentional first-helper exit while muted inside the inherited-state scenario; surviving helpers then exercise production Exit restoration and fixture cleanup. No Roblox client, account, sign-in, unrelated process audio or paid service was used. This is Windows helper evidence, not live Roblox acceptance.
