# Isolated Windows capability probe

Requires the .NET 10 SDK and an interactive Windows desktop. Run from the Personal source directory:

```powershell
.\scripts\Check-ZenithSessionsCapabilities.ps1 -RunNativeProbe
```

The runner creates three small temporary windows without requesting foreground activation. Its experimental message executor can address only explicitly registered HWNDs belonging to its own process. The single WGC owner also rejects other processes. The original fourteen checks send F24 key messages and diagnostic left-button messages, check nonrecipients/cancellation/physical-key-state differences, and verify changing center pixels under an own cover. After those checks deliberately destroy the second receiver, the interpreter exercise creates one replacement receiver; there are still only three live diagnostic windows. All windows and capture resources are closed afterward. The parent enforces a 35-second process deadline.

Collector `zenith-native-probe/0.3.0` retains report schema 1 and adds eight distinct checks using the real `MacroSuite.Core` interpreter, the .NET 8 execution bridge and the shared target-input owner. Two registered recipients share one privately frozen key/left-button/delay plan with separate interpreters and controls. The exercise checks pause-one/continue-other, unfinished-hold restoration, ordered cooperating-receiver acknowledgments, clear-holds with retained target ownership, Stop/Panic, refused unsupported input/recipients and an unchanged nonrecipient cover. The source model is deliberately changed after plan compilation. Foreground observations are sampled before native admission for each acknowledged command; they are not raw-input or Roblox acceptance. Existing and macro exercises own the same input implementation sequentially, never competing pipelines. No extra capture owner is created.

The native probe uses Windows.Graphics.Capture with two GPU buffers, bounded 1024x1024 dimensions, coalesced frame notifications and bounded reads. Pixel data remains in memory; only small numeric observations are persisted. No native input goes to Roblox or other apps. No global input hook, SendInput, focus swapping, login, client launch or credentials are used. No screen/display capture is used.

**This tests cooperating diagnostic receivers, not Roblox.** Posting a key message does not change physical/raw keyboard state. The shared interpreter exercise does not connect saved-host control IPC, profile/account identity, target-bound visual conditions or broadcast. `AccountVerified` and `RobloxQualified` stay false. Successful transport and fresh own-window pixels cannot satisfy background Roblox input, image matching, correct-account reconnect or sixteen-client acceptance.

The report has `NativeControlledExercise` provenance. The main harness can attach it with `--native-report`, but never promotes its checks to `LiveRobloxExercise`. Exit 0 from this executable means its own controlled checks passed; exit 3 means at least one was not established; exit 2 means setup/reporting failed. The full runner still returns 3 while live gates remain blocked.

No new third-party package is required; the Windows SDK projection and WPF framework references come from the installed SDK. This experimental process is not included in a product ZIP or connected to the production input/capture owners.
