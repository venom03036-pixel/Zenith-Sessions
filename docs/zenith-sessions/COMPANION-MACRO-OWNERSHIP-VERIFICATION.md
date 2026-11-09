# Companion macro ownership and Stop verification

## Foreground backend for the Rust rebuild

`CompanionForegroundPlayback` runs one exact saved revision through the existing MacroExecutor and host-owned input player. It takes a host-local window receipt with the expected handle, process ID, and process start time, and checks that the current foreground matches. It borrows the same input arbiter as the editor, checks activation and playback licensing, and rechecks the saved revision after waiting for neutral input. It preserves the saved **Active external window** policy. Its first execution scope supports finite macros using delays and ordinary letter, digit, arrow, and space key actions. Other target modes and action types fail preparation before any input.

Focus loss pauses the run and releases its held input. Resume checks neutral input and the same foreground window. Stop revokes the lease, cancels preparation or playback, and waits for the actual worker and owned releases. An incomplete cleanup leaves the arbiter quarantined. Ordinary input uses the existing session-bound Win32 executor. This follows the Windows input-stream and foreground APIs. [Microsoft SendInput documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput), [Microsoft foreground window documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow)

The Personal integration fixture reads a protected saved revision through the actual editor source and runs it through the real Win32 player with a recording native sink. The packets contain the saved key and its release, while the different editor draft remains unexecuted. The Sessions fixtures cover rejection of an unsupported later step, reused handles, a saved revision changing during preparation, focus loss with a held key, explicit resume, Panic during a pending read, and incomplete cleanup. **131 Sessions checks, 96 Personal checks, and 762 Core checks passed.** The host Release build completed with zero warnings and errors. The tested execution assembly SHA-256 is `156E00E65620C916AB290AB10BEF78FF04083BC2FC479BDAF003E00FF2710655`.

The desktop package remains [Preview 1.15](RUST-DESKTOP-PREVIEW.md#preview-115-playback-target-review). The host-local operation has no command endpoint. The Rust preview still needs an explicitly authorized Personal connection that binds the exact owned client to the host receipt, maps stop and disconnect to that operation, and exposes its status. Only then can an attended trial verify focus, delivery, pause and release with a designated Roblox account. The controlled fixtures send no desktop input.

## Earlier diagnostic backend

Backend cycle 0.51. The staged application remains the previously verified 0.50 preview. This cycle does not promote a new package.

## Backend handoff

`CompanionMacroOperations.RunAsync` is the host-local entry point. Construct it with the Personal host's existing `HostInputOperationArbiter`, a `SavedMacroPlanPreparer` for the protected saved-document source and its required fault callback, and a `TargetedMacroRunner` backed by a registered diagnostic transport. Pass the saved reference, the explicit target list and the host stop generation observed when accepting the request. The call freezes up to 32 distinct recipients, borrows one host lease before starting any saved read, and returns typed preparation and per-recipient results after all accepted work and cleanup have joined. Two registered recipients execute the same saved revision with separate interpreters and input ownership.

No host builds the preparer yet. The host that does must pass each error type the fault callback receives to its fixed-payload diagnostics, the way `ManagerDiagnostics.Record(kind, errorType)` keeps only the type. Until then, unexpected source, owner-guard, compiler and editor-read errors behind an Unavailable, Changed or UnsupportedDocument result are not recorded anywhere.

Host Stop revokes ordinary and restore admission at the transport commit point and cancels preparation and interpreter waits. Runner-wide Panic also revokes the group lease, including recipients that have yet to start. Existing editor playback, recording, Auto Clicker, pickers, and capture tests cannot obtain that host lease while the companion owns it. An already committed event can finish after Stop. Cleanup uses the captured owner's independent authority to release its own submitted holds and keeps the host reserved until cleanup completes.

`RecoverAsync` takes the failed operation ID and retries only that operation's input releases. A separate lifecycle cleanup failure keeps the host quarantined. A reused window handle or a new activity cannot inherit the old operation's recovery authority.

## Executed checks

- **201 focused Sessions tests passed twice**, including 25 new controlled cases. These cover host and runner Stop during saved reads and recipient admission, final send timing, cleanup after Stop, restored prefixes, shared host exclusion, and exact recovery.
- **759 Core and 199 Personal tests passed on .NET 8**. The Personal fixture used the editor's real protected saved-document path and its actual arbiter. The saved F24 reached the memory-only receiver while the unsaved F23 draft stayed in the editor. The four shared backend assemblies matched the files exercised by the Sessions tests.
- The broader shared-tree run, started before the UI handoff, had four account-template resource failures against the in-progress UI and one Windows file-observation timeout. The latter passed on its isolated retry. The account templates and their tests belong to the separate UI work. The 0.50 candidate App hash was checked again and matched the prior verification.

The [machine evidence](../../artifacts/sessions-backend-verification-0.51.json) has SHA-256 `5EFAB42A94EB75CA6C618DE8930DE2E14865AC58D0D44809C438C5573CDA2A0E`. The backend currently has diagnostic recipients and memory-only integration evidence. UI wiring, control IPC, target-specific visual conditions, verified Roblox account and arrival evidence, real background delivery, broadcasting, reconnect and competitive load tests remain pending.

## Backend 0.52

`CompanionMacroOperations.Start` returns a host-local handle. See [Tracking an operation](COMPANION-MACRO-OWNERSHIP-DESIGN.md#tracking-an-operation) for the contract. `RunAsync` remains available to existing callers.

- **153 focused tests passed twice.** Six new cases exercised Stop during a blocked saved read, progress before first input with a later recipient both admitted and rejected, independent Pause/Resume/Stop, stale handles after same-window replacement, and Stop during in-flight hold restoration.
- **199 Personal tests passed** from an isolated .NET 8 build with the same backend DLL hashes. **759 Core tests passed** with the same Core and SDK DLL hashes.
- Both builds completed with zero warnings or errors. The restricted test runner could not connect to its child process. Direct runs with local communication enabled completed successfully.

The [machine evidence](../../artifacts/sessions-backend-verification-0.52.json) has SHA-256 `DB7ABECE0ADECB9C7BE46B2894BF6D118E649C95CDA6337EBD1837927093AB94`. The staged **0.50** preview matched its earlier hash. The manager's UI, connection to the Personal host, and installed releases were outside this change. These tests used controlled recipients and did not exercise real Roblox sessions.
