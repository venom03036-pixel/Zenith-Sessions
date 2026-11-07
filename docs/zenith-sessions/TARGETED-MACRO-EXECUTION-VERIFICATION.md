# Targeted macro interpreter integration verification

Cycle 0.43, November 6, 2026. Personal development work only. **LiveTestReady=false; ReleaseReady=false.** Installed editions, real Roblox accounts/clients, credentials, owner identities and published experiences were not changed. The native exercise created only its own temporary message-receiver windows; it did not use computer-control tools or send physical/global input.

## Implemented scope

The pure target-input owner and geometry types now live in one first-party .NET8 `Zenith.Automation.Input` assembly. Sessions.Core forwards the existing full type names; old constructor/method signatures and enum values remain intact. The additive Clearing state is value7. The published manager includes this shared dependency, and its existing packaged input check verifies the forwarders, clear/continue, retained lease and paused clear semantics. The actual owner exposes its read-only hold limit for whole-plan admission.

`Zenith.MacroHost.Execution` connects the existing **MacroSuite.Core MacroExecutor** to that same owner. A bounded privately frozen plan creates a separate model, interpreter, context, controller, active-time clock, progress, target, holds and cancellation state for each run. There is no second interpreter or per-input IPC. Whole-document preflight checks supported actions, declared input vocabulary, actual hold budget, coordinates, finite repeats/runtime and target policy before opening a lease. Unknown/custom/disabled unsupported steps, Unicode, screen-position actions, relative-camera input, image/color/plugin/AI/control-flow paths and incompatible saved targeting are rejected. This initial plan explicitly rejects indefinite repetition rather than pretending the interpreter's disabled runtime ceiling bounds it.

An optional `IClientRelativePlaybackTarget` supplies explicit recipient metadata for existing Click/Move steps when no resolved session recipient exists. Existing players do not implement it, so their coordinate-service behavior is preserved. It never grants foreground, account or input authority. The synchronous legacy release call requests a tracked async clear; the next event and finalization await its completion. Clear discards ongoing holds while retaining the lease; Pause retains unfinished holds for deliberate restoration.

The interpreter bridge is currently referenced by the **separate NativeProbe/test path**, not Sessions.App. App's catalog/assignments remain metadata-only. No ordinary Run command, execution pipe, foreground fallback, Roblox transport or account qualification was enabled. The only admitted native purpose is registered own-process diagnostic windows. The [reviewed design](TARGETED-MACRO-EXECUTION-DESIGN.md) records these limits and the incremental migration.

## Lifecycle corrections

Independent review found three composition defects before the first test run: a pause-publication gap could become an input failure; a Pause arriving during hold restoration could be lost; and a failed clear could skip later transition/session disposal. The composition now closes its own emission gate before child transitions, fences restoration with generations, rechecks gate identity at submission, tracks both interrupted transitions and independently joins every cleanup leg. Failed native release retains a quarantined lease until explicit successful cleanup. Stop/Panic and finalization take precedence over later transitions.

Preflight also checks peak ongoing holds against the actual coordinator limit, not only a declared transport value. It rejects known unmatched releases before any earlier action can emit. No completed click or key event is replayed to compensate for Pause.

## Executed identities and checks

- Published App0.43 SHA256: `6319745D0706EFE363D6926726EB809AB673035B6DAD9B76C3C767B021284B70`.
- Machine record: `artifacts/sessions-manager-verification-0.43.json`, SHA256 `D0241CE4860B8A9D21C9F9B37A5FEB5D33AC7DF61249C257A4A3E50F26CA1F91`.
- **3,969/3,969** source checks and **3,969/3,969** with the exact published first-party manager assemblies. **91/91** affected checks passed twice against those files.
- **50 new Sessions cases:**20 clear/extraction cases,26 real-interpreter cases and4 direct existing-step recipient regressions. Coverage includes source mutation, unsupported whole-document refusal, independent progress/delays, old readiness gates, blocked restoration, failed clear, actual clock disposal, quarantine/retry, owner limits and disposal joining uncooperative submission.
- **709/709 existing Core checks** passed on .NET8 against the same MacroSuite.Core/SDK binaries used by the Sessions tests. The corrected capture subset passed **4/4 twice** more.
- **34/34 capability/evidence cases** passed. Collector0.3.0 is accepted explicitly; unknown0.4.0 remains rejected. The actual native report was parsed by the rebuilt harness, whose7 controlled boundary checks passed while both mandatory live gates remained blocked.
- **42/42 packaged checks twice**, within the unchanged deadlines; final repeat wall time8,322ms. All29 staged files match28 manifest entries plus the manifest. Required runtime/dependency files include the newly extracted input assembly. No extra/reparse/private-filename matches were found; this is a filename/hash check, not an exhaustive content-security scan.
- Final product, test, capability, probe and smoke builds were warning-free. No product source changed after the final passing product pipeline. There were no new UI renders or interactive/DPI/performance acceptance checks in this engine increment; prior UI/media evidence remains separately attributed.

The separate Execution DLL SHA256 is `C99AA1CBF7B267998465AEAD0D180E96DC99C8293B4D89122E7837FC5C3C4C21`; MacroSuite.Core is `A2ACE52028DF9AB1B73ECF18FC076133BB735416F56E7590028CF4116C8C5AF0`; published shared input is `464EF53F012C5F2738D8BCBFD31A03B85FB54C343E3D2C41E6C912431F532022`. The collector verifies identical Execution/Core/SDK binaries in Sessions tests, NativeProbe and .NET8 smoke, and identical published Core/input/contracts in the probe. These separate execution artifacts are not described as manager runtime dependencies.

## Actual runtime and compatibility

A smoke executable ran the real interpreter on **.NET8.0.31**, using the exact tested bridge/Core/SDK and published input DLL. A finite two-repeat plan completed6 steps and4 in-memory submissions with clean terminal input. NativeActions=0, AccountVerified=false and RobloxQualified=false.

Before extraction, a small caller was compiled against the actual0.42 Sessions.Core and passed its memory-only baseline. Its DLL was **not rebuilt**: SHA256 remains `5DFCBD22861686B222462A2C07ADB0F2FBE275F0A4FEDB32F14CDBC24F5FB94A`. The same binary then loaded0.43 via the type forwarders, exercised geometry, opened the owner, submitted a pair and stopped cleanly. Its resolved input/geometry assembly is now Zenith.Automation.Input. The baseline and both result records remain under `artifacts/sessions-input-legacy-caller`.

## Native owned-window exercise

NativeProbe collector0.3.0/schema1 passed **22/22 twice** under its unchanged25-second internal deadline. The original14 checks run first;8 additional checks traverse the actual interpreter bridge. Reports are `artifacts/sessions-feasibility/native-probe-0.43-first.json` and `native-probe-0.43-repeat.json`.

Both interpreters used one frozen five-step plan against two exact registered own-process HWND incarnations. One stayed paused while the other completed5 steps. Receiver acknowledgments confirmed pause release and unfinished-hold restoration: A acknowledged8 ordered key/button messages, B4, with no receiver foreground samples at admission. Clear retained its target lease, refused a competing run and allowed a later pair. Stop prevented later resume; two-run Panic joined both releases. The unrelated cover received no input. The existing one-owner WGC check still observed fresh changed pixels under that cover. All temporary windows/workers exited.

Posted/acknowledged cooperating-window messages do **not** establish Roblox/raw input consumption, per-client image decisions, exact-account association, native arrival, reconnect, physical broadcast, saved-document execution IPC or a live release gate. Evidence remains `NativeControlledExercise`, never live evidence.

## Retained failures and smallest corrections

The initial test compile missed a Xunit import; its42 attribute diagnostics and the subsequent correction are retained. One test assertion-style warning was corrected to Assert.Single; the full final pipeline was rerun without warnings or changed assertions.

The broad Core run exposed an unchanged test's scheduling assumption: sequential requests could straddle the worker's Task.Yield/drain, producing2 source captures instead of1. The test now holds an explicit primer capture through the existing source dependency, queues both consumers, then releases it. It requires exactly one primer plus **one** bounding capture of Rectangle(10,10,30,10), preserving original per-frame origins/pixels. The wait is cancellation-aware and all frames/gates are joined/released in finally. No production capture code, scheduling delay, timeout or expected consumer result was relaxed. The initial708/709 report is retained; the corrected709/709 and4/4 repeats are separately recorded.

An auxiliary script initially passed a build-only option to dotnet test. It was corrected to build then run the test assembly; this was an evidence-runner argument error, not a product failure. Retained initial logs/TRX distinguish these events from the final evidence.

The seven-ID After checkpoint itself completed successfully. Its caller then inspected PowerShell's stale native-exit variable and reported a false helper failure before archival. The generated Phase/IDs/source fingerprints and exact ledger hash were checked directly, and that unchanged result was archived as `after-0.43.json`. This did not change product source or test evidence.

## Remaining acceptance

Next required integration is host-owned exact saved-document/revision authority, shared host input arbitration/control IPC and target-bound visual capture. Do not expose mutable editor Play over IPC or treat a catalog metadata revision as an encrypted-document receipt. Preserve the separately identified pinned-foreground final-emission recheck requirement before any new production host execution route.

Qualified real Roblox background keys/clicks/images, verified account/process/native arrival and reconnect, protected private/share/reserved joining, owned-game bridge, live broadcasting, full UI/focus/DPI/latency, sustainable2/4/8/16-client tests, comparative benchmarks and release packaging remain unfinished. Ordinary/group launches remain disabled; confirmed per-operation trials remain Unverified. Historical tiny-file diagnostic stress latency is still open. No installation, completed parity, performance superiority or two-account test invitation is claimed. Continue the full active product goal rather than treating this diagnostic increment as completion.
