# Delayed settings save and Panic review repair

Reviewed baseline: `21354e980637cfbf97517565446b96f6ce7d0968`; repair applied to merged integration `11883efc2919bd4fbcc9b99d0c4746ab37dc62c9` in the D9 lane. This is a local offline review and regression result, not native acceptance or release readiness.

The important finding was a pending **Save settings** request that began before Panic but completed after it. The save's completion callbacks treated a newly saved off-to-on preference as a fresh activation request, replacing the newer stop. The same path affected Roblox release checks, keep-awake, arrival muting, Discord alerts and Discord status. A stopped local disk write did not need to be discarded; its authority to restart automation needed a separate lifetime.

`ShellViewModel` now captures a settings-activation generation before the first asynchronous preference validation or write. Panic, Lock and shutdown advance it before stopping owners. An older save may complete and persist its selected values, but its activation callbacks cannot restart an owner after that newer stop. Each callback checks the generation separately. Keep-awake uses the same captured generation inside the serialized save. Keep-awake and Discord retain stop state even when their saved preference is still off, covering an enable request waiting to persist. Later explicit saves and the existing Resume actions keep their existing behavior. No layout, credential format, network identity or native dispatch policy changes.

## Executed regressions

All five tests use the real Shell and hold its actual preferences-writer semaphore at asynchronous write admission. They start Save settings, prove it has not completed, invoke Panic, release the writer and await the saved result. Production logic was unchanged during the failing run. No compiler error or timeout is used as failure evidence.

| Test (all names begin `AnEarlierPendingSettingsSaveCannot`) | Before repair | After repair |
| --- | --- | --- |
| `RestartAutomaticChecksAfterPanic` | Fake public release source called once | No release or discovery request; no timer; enabled value still saved |
| `StartKeepAwakeAfterPanic` | Two synthetic targeted F13 commands dispatched | No input or foreground request; no timer; explicit Resume remains available |
| `RestartArrivalMuteAfterPanic` | Arrival pause cleared | Arrival pause retained; a later synthetic client receives no mute |
| `RestartDiscordAlertsAfterPanic` | Outbox active | Outbox remains stopped; fake transport untouched |
| `RestartDiscordPresenceAfterPanic` | Presence owner active | Owner remains stopped; fixture pipe receives no connection |

Release run, `-m:2 -nr:false --no-restore`:

- `artifacts/sessions-tests-0.60-d9saveepoch/save-stop-all-before.trx`: **5 failed, 0 passed, 0 skipped**, successful compilation. SHA-256 `6A29569D3DB529008BBBA8A546A3F9A4F36D9E9C583230D5D6CEBD55F0F13BB5`.
- `artifacts/sessions-tests-0.60-d9saveepoch/save-stop-after.trx`: **319 passed, 0 failed, 0 skipped** across Roblox update, keep-awake integration, client preview/sound presentation, Discord alerts/presence, fail-safe stop and settings-recovery suites. SHA-256 `DE685CC1FB1597461BBE2FDCB1984CE078752AEC0E3C092AE0CABB75A8E9DEAE`.
- Before/after requirement checkpoints `0.60-d9saveepoch` verify all three original prompt fingerprints and scope `PROD-QA-07`, `CAP-08-06`, `PROD-CORE-03`. The after checkpoint references both TRX files.

The first three-only red run (`save-stop-before.trx`, 3 failures) preceded adding the two Discord counterexamples. The five-case red run above is the complete reproduction.

Rule 18: one scalar generation; no new timer, worker, request, allocation-heavy loop, dependency or persisted field. The tests use synthetic release/input/sound transports and an isolated fake Discord pipe. No real Roblox client, real account, external Discord message, UAC, installer or paid cloud test was used. Original capability gates and attended native trials remain required.
