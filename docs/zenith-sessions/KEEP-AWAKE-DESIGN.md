# Keep clients awake (R-instances-17)

Owner decision 2026-11-08: adopt RoALT's anti-AFK capability as Zenith's own built-in plan, with the project's own gate. Roblox disconnects
a client that gets no input for 20 minutes. Every N minutes (default 15, bounded) Zenith brings each client forward, sends it one F13 press
through the single input executor, and restores the window that was in front. Off by default; refused by the capability gate until the live
background input isolation trial marks background input Supported, exactly like Launch and run. Built in lane D6b, test-first, with fakes.
This reverses the recorded review position that anti-idle actions were out of scope (FEATURE-COMPARISON.md rows 51 and the paragraph
before 48; REQUIREMENTS-LEDGER.md CAP-13-05); the reversal is recorded in [HANDOFF.md](HANDOFF.md). RoALT (MIT) was a reference for the
capability only; no code, text or branding was reused, and Zenith's version differs where the constraints below say so.

## What the user sees

- Settings → **Keep clients awake**: Off (default), Every 5 minutes, Every 10 minutes, Every 15 minutes, plus a saved value that is not
  among them (`SessionsSettings.KeepAwakeMinutes`, optional within settings schema 1; a file without it loads unchanged and older builds
  ignore it; 0 or 5-19 is valid, anything else makes the settings invalid, like a bad idle-lock time). Save settings applies it.
- A status line under the choice says, in plain words, what is happening: off; refused by the gate and why; "no verified input
  transport in this build"; sample mode; "First press in N minutes"; waiting for the user to leave the keyboard and mouse alone; the
  last tick's outcome ("Sent one F13 press to 2 of 3 clients: 1 could not be brought forward. Next press in 15 minutes."); or stopped by
  Panic or Lock. A **Resume keeping clients awake** button restarts it after Panic or Lock.
- The Guide ("Keep clients awake") and Settings search ("keep-awake": anti-afk, idle kick, F13, …) describe it. The Diagnostics journal gets
  one activity line per tick ("Keep clients awake: sent F13 to 2 of 2 clients."), never which clients.

## The gate (`CapabilityGate`)

`Zenith.Sessions.Core.CapabilityGate.Check(GatedFeature)` reads the shipped capability list (`ProviderCapabilities.Current`) each time it is
asked and permits a gated feature only while "Background macros & broadcast" is `Supported`. Today that capability is `Unverified`, so
`KeepClientsAwake` (and `LaunchAndRun`, which is listed for the same reason and still has no code behind it) are refused with an
explanation that names the feature, the capability and the live input isolation trial. The shell checks the gate when the choice is saved
and again at every tick; a future supported capability still requires a qualified production transport. Nothing reads the gate's answer from a cached
copy.

## The plan (`KeepAwakeRunner`, Core)

One tick, over the clients of the latest fresh and complete scan that have a window Windows answers for (no unreadable row, no hung or
not-responding client), at most 32 per tick (a rotating cursor gives the tail a turn on the next tick of the same scan order); each client in turn, one lease at a time:

1. `IKeepAwakeInputExecutor.Register(client)` turns the exact client incarnation (PID, start time, HWND) into a registered
   `TargetedInputTarget` of the verified transport. Null means "could not be registered": no key.
2. `TargetedInputCoordinator.TryOpen(target, TargetedInputPurpose.KeepAwake)` opens the one lease the press may use. A window another
   run owns (a macro, a broadcast follower, a diagnostic) is refused by the owner and left alone; the plan never waits for or steals it.
3. `IForegroundWindowSwitch.BringForward(window)` asks Windows to bring the client forward and re-reads the foreground. If the client is
   not in front afterwards, no key is sent to it (`NotInFront`): a focus change between the check and the key must never deliver F13 to
   another program, which was the recorded risk of RoALT's version.
4. `run.SubmitAsync([F13 down, F13 up])` through the owner. The transport receives the exact target with each command and keeps its own
   contract: validate identity and geometry, check admission at the native commit point. The plan has no fallback to global input.
5. `run.StopAsync(KeepAwakeSent | NotStarted | AfterFailure)` releases the lease whatever happened; the stop reason is one of the owner's
   closed set (`KeepAwakeSent` was appended with its own text). A release the owner could not complete keeps the window reserved and the
   result says so.

At the end the window that was in front before the tick is asked back (`BringForward(previous)`); a refusal is reported, never retried. A
cancellation (Panic, Lock, exit) stops before the next client; a send cancelled in flight is handled by the owner exactly as for a macro:
reported uncertain, its hold released by the owner's own cleanup, the lease freed.

The runner takes `SessionObservation`s and returns one `KeepAwakeClientResult` per client (`Pressed`, `NotRegistered`, `LeaseRefused`,
`NotInFront`, `InputRejected`, `Cancelled`, with a plain explanation) and whether the earlier foreground was restored. It is pure
orchestration over interfaces, tested without a screen, a window or a client.

## Schedule (`KeepAwakePolicy`, Core; `ShellKeepAwakeFeatures`, App)

- One `DispatcherTimer`, armed for the saved gap when the saved choice is on and the gate permits. A scheduled press is a timed action, so
  a timer is the right tool; nothing polls between ticks (Rule 14).
- When the timer wakes, `KeepAwakePolicy.Decide(minutes, permitted, userIdleFor)` answers Off, Refused, Defer or Press. **Defer**: the user
  used the keyboard or mouse anywhere in the last 10 seconds (`IUserInputIdleSource`, GetLastInputInfo, a read-only system fact), so the
  foreground is never taken from them; the timer re-arms for 15 seconds and looks again. Idle time Windows could not report does not
  stop a press: a missed press is a disconnected client. Only after a press has ended is the timer armed again for the full gap, so two
  ticks never overlap.
- Panic and Lock run `StopKeepAwake` as a `StopOperations` step (Rule 11): the tick in flight is cancelled, the timer stopped, and the
  feature stays stopped for this run until **Resume keeping clients awake** or until the choice is saved on after being off. Unlock alone
  does not restart it. Exit cancels the tick and joins it (`_keepAwakeTick`, Rule 12). Sample mode arms nothing.
- A theme or window-placement autosave does not re-arm the timer; only a changed choice does.

## What production has today

The shell is composed without an `IKeepAwakeInputExecutor`: Zenith has no verified transport for Roblox client windows (that is the held
item Z1702, and the subject of the live trial). The gate refuses first; a build whose gate passed but that still had no transport would say
"no verified input transport in this build" and arm nothing. The foreground switch (`WindowsForegroundWindowSwitch`) and idle source
(`WindowsUserInputIdleSource`) are real Win32 reads with no side effect until a press runs. The transport the trial qualifies decides
how the key reaches the client: posted messages to the exact HWND, or a foreground executor that must itself refuse when the target is
not in front at its commit point.

## Rule 18 review

- CPU: one tick per 5-19 minutes, at most 32 clients, two signals each; between ticks nothing runs.
- Memory: one bounded result list per tick, replaced by the next; no history.
- Allocations: not a hot path.
- Threads and timers: one `DispatcherTimer`; no thread; the owner's own tracked tasks.
- Cancellation: the tick's token is linked to the shell lifetime and cancelled by Panic, Lock and exit; the owner's cleanup has its own
  bounded deadline as for every run.
- Errors: a failed tick is shown with `Fail(…, ManagerSubsystem.KeepAwake)` and kept by type only (Rule 10); per-client refusals are in the
  status text, not errors; the idle read failing is recorded by type and treated as unknown.
- Testability: `KeepAwakeTests` (gate, policy, runner over a controlled transport, foreground switch and executor), `KeepAwakeSettingsTests`
  (schema-1 optional field, bounds, XAML, Guide, search), `KeepAwakeIntegrationTests` (shell: save arms the timer, a tick presses each
  client of the latest scan through the owner and re-arms, deferral, the shipped gate refuses, no-transport build, sample mode, Panic
  cancels a send in flight and the owner releases the hold, Resume, Lock, Unlock, save-on restarts, exit joins the tick). The packaged
  layout check pins the off default and the Resume binding.
- Security: no secret, credential, log content or client file is touched; no window style, hook or FFlag.
- Backwards compatibility: `KeepAwakeMinutes` is optional in schema 1; `TargetedInputPurpose.KeepAwake` and
  `TargetedInputStopReason.KeepAwakeSent` are appended without renumbering; the coordinator test that pins the closed reason set was
  extended for the new member.

## Still to confirm live (blocked by: live input isolation trial)

With the trial's verified transport and two owned accounts in running clients: a press reaches only the client it was sent to and no
other window; the client's idle timer resets (it is not disconnected after 20 minutes while the feature runs); the earlier foreground
window is back in front after each tick; a tick while the user types defers and takes no window; Panic during a tick stops it and leaves
every client in a consistent key state. Only then is "Background macros & broadcast" marked Supported, which lets the gate permit the
feature.

## Completion repair 0.60-keepawake

Scheduling starts only after manager initialization. Panic stops an armed or in-flight feature; an inert gate or missing transport keeps its explanation and offers no Resume. Schedule generations suppress late cancelled results after Resume or saving Off/On. Saving a changed interval cancels old work. Registration errors expose only their type. The live exercise now requires actual successful first, later and restore foreground requests and records refusal counts; zero input cannot pass. Rotation alone does not prove clients stay connected when the total cycle exceeds the idle interval.
