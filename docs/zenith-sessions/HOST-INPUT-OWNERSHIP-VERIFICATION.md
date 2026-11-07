# Personal host operation ownership — 0.46 verification

Reviewed November 6, 2026, local time. This is verified development progress, not a finished companion or an installed update. **ReleaseReady=false; LiveTestReady=false.** The [requirements ledger](REQUIREMENTS-LEDGER.md) and [reviewed design](HOST-INPUT-OWNERSHIP-DESIGN.md) retain the full product scope.

## Result and identity

The staged companion passed **4,006 source tests and 4,006 tests against its exact staged first-party assemblies**, **350 affected manager cases twice**, and **42 packaged checks twice** under the existing budgets. The actual Personal editor/store/input regression passed **568/568**, with **216 affected cases twice more**. Core passed **758/758** on the .NET 8 test target. Final builds reported zero warnings and errors. These include **120 new controlled cases: 26 Core and 94 Personal**.

The stage contains 28 manifested files plus the manifest, 29 in total. Lengths and hashes match. A filename/reparse/hash audit found no unexpected private-data filenames; that bounded audit is not an exhaustive secret or security scan. Nothing was installed, no real accounts or settings were used, and no real Roblox session changed.

- Staged `Zenith.Sessions.App.dll`: `0032A32FC456B907D84EB51999395CCD52DEDD1171BC79965C2B2CC81B70CBDF`.
- Tested Personal `MacroSuite.App.dll`: `156FD45D871CDF8CD7CF4507A040448BC6246D46ACC4954C6E4BDD019E573BA1`.
- Tested `MacroSuite.Infrastructure.dll`: `D2C06116B045B6DB3EC527C9B25D0450AE1C52B0566A5A0A916D9515F7F28A58`.
- Shared `MacroSuite.Core.dll`: `F21C44E2D03FA175EE72F7AF639E8ADDCCE1907EB39AD2ACEF4497569FAF2F6C`.
- Machine evidence: [sessions-manager-verification-0.46.json](../../artifacts/sessions-manager-verification-0.46.json), SHA-256 `DD9FDBFFBCFC5CC4E2FFB1414D00A8DA74AC075594F6262A8B372CE33483D3E1`.

The final source snapshot fingerprints 36 product/test files. Core/SDK hashes matched the manager and Personal test outputs. Three other independently rebuilt shared assemblies differed; the exact manager files were copied into the isolated Personal test directory, then all 568 and both 216-case repetitions passed again. The final evidence checks all six shared identities. No installed binaries were replaced.

## Implemented behavior

One App-composed operation arbiter now covers the existing Personal editor, recording, Auto Clicker, picker and capture routes. Startup reserves an exact lease before asynchronous activation or reentrant callbacks. A paused producer retains it. Normal Stop, Panic, closing and recovery use captured operation/generation identity; a late callback cannot release or stop its replacement. Compatibility constructors and optional player/picker interfaces preserve existing SDK/player contracts and macro formats.

Hook signals close emission admission without UI, disk or native-input waits. Controller Stop remains distinct from Panic. Retirement joins actual producer, transition and cancellation work. A presentation deadline does not release a still-running recorder or consumer. Cleanup failure keeps the retired owner quarantined; only deliberate same-owner recovery can clear it. A successful final release cannot erase an earlier transition failure.

The native backend retains managed mode across attachment gaps. It checks the captured lease at final submission and accepts cleanup only for the exact owner. Idle editor/Auto shutdown cannot globally release another producer's holds. Auto finalization attempts independent captured cleanup even after a failed pause/resume transition.

The location picker uses per-arm state and actual hook-thread exit, not a timed join treated as success. Explicit interactive parents may be borrowed; other producers cannot lend their authority. Old cancellation/finalizers cannot tear down a replacement arm. Overlay startup errors join the child and dispose visual/privacy state.

F10 now uses an operation-owned helper and the existing shared capture service. A captured frame transfers to its caller only after privacy, mode and lease cleanup succeeds. Stopped/failed paths dispose the held frame. Privacy setup/restoration marshal asynchronously to the actual Application dispatcher and are awaited. The real privacy lifecycle remembers before mutation, rolls back setup errors, attempts every restoration, retains only failed entries, and blocks new scopes while unresolved. Remembered state is bounded to 16 engine windows, unrelated to the account/client count.

## Failure history and corrections

Three initial compile attempts exposed two missing namespace imports and six test lambdas selecting xUnit's obsolete async-exception overload. They were corrected without suppressing diagnostics.

The first running test lane was deliberately aborted after locating a closed fixture gate. Its old stalled-recording test waited for public Stop before allowing the actual recovery consumer to finish; Stop now correctly joins that consumer. The identified isolated test host alone was ended. Its 205 completed passes are **not** a passed suite. The corrected fixture observes the unchanged five-second fault deadline, asserts pending Stop and retained exact ownership, always opens its gate before joining, checks the original terminal timeout/quarantine, and then verifies explicit recovery and saved actions.

The first complete Personal run passed 557 of 561 tests and failed four. The overlay fixture assumed synchronous startup despite newly published asynchronous ownership; it now disposes at the actual callback. The empty-mini fixture now runs on its WPF Application dispatcher, retaining every assertion for new destination, defaults, recorded tail and restoration. These two are fixture timing/thread-affinity repairs, not claimed observed desktop defects. Product corrections distinguish an ordinary completed modal error from explicit cleanup uncertainty and marshal capture privacy creation as well as restoration. Six new privacy cases cover rollback, nesting, failed-state retention, captured-token retry, bounded state and off-dispatcher rejection. No global test resets or longer product deadlines were added.

Initial logs, aborted TRX, four-failure TRX, source snapshots and final raw logs remain fingerprinted in the evidence. The final empty-mini, recording timeout/recovery and all four previously failing cases pass. Independent source review found no remaining blocker in the scoped privacy/frame/shutdown paths; that review is not native acceptance.

## Limits and next work

Cancellation callbacks must be signal-only. Arbitrary synchronous self-disposal/self-joining from those callbacks is unsupported; direct controller StateChanged self-disposal fails explicitly rather than deadlocking. Forced process exit is not proof of clean native shutdown or release.

A returned failed privacy token can retry its captured unresolved restoration. If setup rollback fails before returning a token, or a closed snip cannot restore, the current interface has no recovery button; retained quarantine/blocked privacy requires application restart. It does not silently clear the error.

`ImageSourceDialog.xaml.cs` still contains a legacy independent synchronous GDI capture. The modal is now operation-owned, but complete shared asynchronous capture migration remains open. No production companion macro-execution RPC was added. Target-specific image/condition execution, qualified account/PID association, verified arrival/reconnect, physical broadcasting, full UI/focus/DPI/performance acceptance, sixteen real clients and comparative benchmarks remain unfinished. Ordinary/group native-account launching stays disabled; explicit per-operation trials remain Unverified.

This cycle sent no native input, showed no interactive test UI and added no new rendered-UI evidence. Existing hidden WPF regression fixtures are not real-desktop latency/focus proof. The historical 22 owned-window native checks from 0.43 were not rerun and do not qualify Roblox background input. Public-disabled compilation and dependency/file exclusion passed; no installed Public or Linux product was changed or accepted. No live-test invitation is issued yet.
