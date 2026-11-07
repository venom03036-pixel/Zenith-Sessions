# Live capability exercises — prerequisites not satisfied

None of the following has been executed by the metadata harness. Controlled Core tests and the separate native own-window probe must never be entered as live Roblox results. The native probe has demonstrated WGC center-pixel freshness behind its own cover and targeted message delivery to its own cooperating receivers; Roblox input consumption is still unverified.

## Prerequisites

Establish a supported provider within the approved constraints: no client modification, injection, enforcement workarounds or scraped cookies. Pin its exact version/configuration and client version. Establish multiple eligible, independently authenticated user-authorized sessions. Use the selected published test experience and approved permitted workloads. OAuth linking, a manually assigned label, a process count and a link opening are insufficient evidence.

The existing `SendInput` executor is foreground-only and cannot be advertised as the required provider. Keep all existing foreground guards. The harness intentionally supplies no unsafe input experiment or fake reconnect implementation when prerequisites are missing.

## Required evidence

For every case record provider/client version, process start identity, target/window geometry, monotonic timestamps, expected/observed target result, account/server verification source, cancellation state and evidence freshness. Exclude credentials/raw logs. Mark unavailable/unexecuted cases explicitly.

| ID | Required real-client observation |
|---|---|
| independent-accounts | Two permitted native sessions independently identify their authenticated accounts; no inference from launch order/PIDs |
| background-keyboard | Press/release and held keys reach the selected unfocused client without foreground changes |
| background-clicks | Buttons/holds reach only the intended unfocused client |
| arrow-key-camera | Arrow-key camera control reaches only eligible recipients; mouse-driven camera motion is outside the approved first release |
| occluded-image-checks | A changing target image remains fresh and detectable while covered by another window |
| input-isolation | Other clients and the user's foreground application receive no test input |
| target-lifecycle | Move/resize changes geometry; closed/restarted clients and reused identities cannot receive stale work |
| emergency-cancellation | Stop/Panic fence pending work and release only owned holds |
| independent-runs | One macro revision drives separate run/condition/image state per client; A's image does not advance B |
| live-broadcast | Leader receives physical input once; only explicit followers receive copies; synthetic feedback and competing macros are rejected |
| confirmed-disconnect | Real in-game disconnect is distinguished from authentication challenges and a deliberate close |
| account-reconnect | Specified still-authorized account reconnects unattended through the verified provider |
| reconnect-identity | Independently confirm the account after reconnection; requested identity alone is insufficient |
| reconnect-arrival | Confirm prior-server arrival, or observed unavailability followed by authorized same-game fallback |

Ordinary client disconnections must not be induced through client tampering or by interrupting unrelated network work. A selected test session can be disconnected using an authorized experience-side test control when prerequisites exist. Arrival acknowledgments from the owned experience do not by themselves bind a local PID to an account.

## Gate

Every required case must pass on the same provider configuration/client version. Unknown, simulated, source-only, mismatched or missing evidence blocks the release. The later request authorized a working manager foundation with unsupported operations disclosed; see MANAGER-IMPLEMENTATION.md. That does not approve a finished automation release or substituting foreground cycling for independent background execution.

The same configuration may compose independently versioned input, WGC capture and launch adapters. Each live check requires at least three observed successful trials, two distinct verified account/process instances, observation timestamps no older than 24 hours, and persisted hash-checked artifacts. The current policy has no external success-import override.

## Approved workloads and later acceptance

Start with two authorized test accounts in owned place 6564161046. Compatibility examples are Anime Card Clash (110829983956014), original Bubble Gum Simulator (2512643572), and Bubble Gum Simulator INFINITY (85896571713843). Sixteen accounts will be supplied for staged 2/4/8/16 testing. A passing two-account gate permits implementation; it does not approve a product release or sixteen-account claims.

Both shared-macro playback and physical key/click broadcasting are required. Live broadcasting requires selected macros to be fully stopped, uses matching content dimensions, pauses on leader focus/geometry changes and never buffers stale inputs. Per-client image decisions remain independent. Reconnect fallback is another server of the exact same place; interrupted macros stay stopped.
