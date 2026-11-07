# Zenith Sessions: first-milestone repository audit

This audit describes the original feasibility baseline. The later manager request added separate companion projects without replacing the Personal host. See [MANAGER-IMPLEMENTATION.md](MANAGER-IMPLEMENTATION.md) for the current implemented scope and unpassed release gates.

## Current implementation

Personal source: `working-copy`, Windows WPF/MVVM, application version 2.5.12, targeting .NET 8. The isolated harness targets installed .NET 10 and references Core directly. The application runtime is not upgraded during this milestone.

Reusable components include `MacroController`, `PlaybackSessionContext`, `ITargetWindowService`, protected macro documents/recovery, settings persistence, theme resources, the faceted theme-colored logo and the existing editor/mini toolbar.

`Win32InputPlayer` owns the native input path. `PlaybackSessionContext` validates the pinned process/window and foreground before emitting input, releases held input on pause and requires manual resume. Multiple tasks do not create independent background streams.

Despite its class name, `WindowsGraphicsCaptureService` wraps DXGI Desktop Duplication; it captures desktop regions. A target's rectangular screen bounds are not an unobscured image of that window when another window overlaps it. Existing image recognition does not prove the proposed occluded-window requirement.

No account-selecting launch/reconnect provider, account/session repository, Roblox log tailer or companion IPC endpoint is present. The existing scan resource sampler measures Zenith, not system launch headroom. Existing provider-credential vault behavior is unsuitable for the proposed per-account vault without replacement.

## Capability matrix

| Capability | Current status | Evidence boundary |
|---|---|---|
| Existing targeted foreground playback | Implemented | Controlled Core tests; not a new live Roblox test |
| Keyboard/buttons/pointer in independent unfocused clients | Unavailable in current executor | Pinned execution intentionally requires foreground |
| Fresh images of occluded client contents | Unavailable in current capture implementation | Desktop-region capture is not unobscured per-window capture |
| Identify installed client versions and open windows | Metadata inspection implemented | No authentication or independent-account inference |
| Multiple independently authenticated simultaneous sessions | Unverified | Several processes cannot prove this |
| Official OAuth identity/API authorization | Documented; integration not implemented | Does not establish native account selection |
| Destination links | Documented, legacy construction deprecated | Opening a link does not prove selected account or arrival |
| Unattended correct-account native reconnect | Unverified; no implementation | No eligible provider to exercise |
| Owned-experience status/teleport bridge | Documented; not implemented at this stage | Connected-player teleport cannot revive a disconnected client |

## Incremental migration and rollback

This milestone adds an isolated tool, policy tests, a runner and documentation. Its controlled test also reproduced a pre-existing cancellation-callback race in `PlaybackSessionContext`: a paused waiter could resume before its linked stop callback ran. A narrow source fix checks the owning controller's stop/panic state directly before restoring or emitting input. It preserves foreground targeting and public signatures. No installed application is updated and no user data is migrated. Rolling back the new files plus that direct-cancellation guard restores the previous source behavior; the guard regression test explains why retaining the fix is preferable. Future .NET 10 migration and shared presentation/IPC extraction remain behind the two approved feasibility gates.

## Rule 18 evaluation

CPU/memory: bounded client-version/process/window enumeration; no recurring sampler or per-row workers. Threads: no persistent workers; Core transition tasks are awaited/disposed. Cancellation: CLI deadline and Ctrl+C; controlled probe deadline. Resource ownership: dispose process handles, registry keys, Core sessions/controllers and temporary output streams. Errors: unknown metadata stays unknown; failed inspection cannot approve release. Testability: gate policy and Core boundaries run without desktop input. Secrets: do not read process arguments, game logs, cookies, vaults or credentials. Compatibility: production formats and runtime behavior unchanged. Dependencies: no new production packages; tests reuse the repository's existing xUnit/Test SDK versions.

## Release decision

Stop at the generated feasibility report until background macros and correct-account unattended reconnect have both been demonstrated. This is the stop condition in the user's approved plan, not a claim that a full companion was completed or that every conceivable external provider is impossible.

## November 4 follow-up implementation

The isolated NativeProbe now evaluates PostMessage and Windows.Graphics.Capture on its own temporary windows. It is a separate executable, not a second production input/capture owner. The original foreground executor and guards are untouched. Successful cooperating native receiver tests are not proof that a game consumes posted input. WGC freshness on an owned covered window is useful platform evidence, not verified Roblox image matching.

The harness's version-2 report distinguishes feasibility from product release readiness and binds future proofs to adapter versions/configuration, trial counts, account/process identities, observation time and persisted artifacts. There is no arbitrary external live-success import. Current native reports retain their controlled origin. The documented legacy mouse-camera requirement is replaced by the approved arrow-key requirement; independent macro state and explicit live broadcasting now have separate gate entries.

Rule 18: the native process has a 25-second internal and 35-second parent deadline, three small nonactivating windows, one capture owner, two GPU buffers, dimensions capped at 1024x1024, coalesced callbacks and bounded frame reads. Callbacks only signal. No global hooks, other-process input, desktop screenshots, account data or saved pixel buffers are used. Native/GPU resources and hooks have deterministic teardown. Its controller logic is diagnostic-only and is never connected to product execution. New dependencies are SDK/WPF framework references already installed; no third-party package or product runtime upgrade is introduced.

Migration/rollback: additions are isolated tools/tests plus report schema 2; old report files remain historical artifacts. Existing macro/settings schemas and installed binaries are unchanged. Rollback removes the native tool and restores harness/report files; retain the prior tested Core Stop/Panic fix. No user data conversion or application replacement is performed.
