# Macro catalog pipe deadline classification repair

Cycle `0.76-pipedeadline`, based on `dd1c29890636de4b38f304736ecb2d1ab903106c`.

## Failure and cause

The second integrated full gate at that source commit retained a failure in `MacroHostPipeTests.ConnectedHostThatMissesTheDeadlineIsBusyNotDisconnected`: expected Busy, actual Disconnected, duration **3.0686494 seconds**. The synthetic source had entered. The original artifact remains under `wt/Completion/artifacts/completion-integration/final-pass-6/`; the test log is `13-Zenith.Sessions.Tests-tests.log`.

The client and server each use the same three-second request duration. When the server's timer cancels its source, it disconnects the request pipe. The client previously recognized a connected timeout only if its own cancellation callback had already run. Due timer callbacks have no guaranteed delivery order. Consequently, server timeout can deliver EOF/IOException after the client's budget has elapsed but before its timer callback runs, incorrectly producing Disconnected. The same delayed notification allowed an otherwise valid response arriving after the budget to publish Ready. The full-gate log did not retain callback timestamps; the source-derived race is reproduced deterministically below rather than inferred as a complete runtime trace.

## Change

`MacroCatalogPipeClient` now records a monotonic start timestamp and uses actual elapsed time, as well as its timeout signal, to determine whether the unchanged three-second budget expired. This applies to connected EOF/IOException classification and the final publication of a valid page. The public constructor still uses `TimeProvider.System`; one internal constructor supplies the clock/timer for deterministic testing.

- Before-deadline EOF remains Disconnected. An absent host, local disposal and already-disposed client remain Disconnected.
- Caller cancellation retains precedence and throws cancellation. Local disposal retains precedence over elapsed time.
- Malformed or mismatched response data remains Incompatible, including after the budget elapsed. It is not converted into Busy.
- The server, protocol, maximum frame sizes, connect timeout, request duration, source cancellation/join, and one-call admission are unchanged. There are no retries, sleeps, extra response grace periods or new dependencies.

Busy here means that a connected request missed the client's deadline. The existing protocol cannot distinguish remote shutdown exactly at/after that deadline from a timeout, and this result does **not** prove that the server is still alive. Early known closure and local stop/disposal remain distinct.

## Genuine failing-before evidence

`artifacts/sessions-tests-0.76-pipedeadline/pipe-deadline-red.trx` records **3 failed, 5 passed, 0 skipped** before the classification repair. Only the internal TimeProvider constructor/timer seam had been added to production; classification and successful-response publication still used the original conditions.

The fixture uses a unique current-user pipe and synthetic catalog metadata. It accepts and parses the actual request, advances a monotonic test clock while withholding the due timer callback, then supplies EOF or a response. It does not alter the production duration or the existing flaky test's timing/assertions.

- At 3,000 ms and 3,500 ms, EOF returned Disconnected instead of Busy.
- At 3,000 ms, a valid response returned Ready instead of Busy.
- The five controls passed already: EOF at 2,999 ms, valid response at 2,999 ms, malformed response at 3,500 ms, caller cancellation after elapsed time, and client disposal after elapsed time.

The final fixture also explicitly delivers a due timeout callback and verifies the existing Busy path. All clock-owned timers are observed disposed. The original real-time test is unchanged, including its Busy, empty-items, explanation, subsequent Ready response and source-call-count assertions.

## Passing-after evidence

All commands used the Release `Zenith.Sessions.Tests` project with `-m:2 -nr:false` and explicit filters:

- `pipe-deadline-green.trx`: **69 passed, 0 failed, 0 skipped**, the entire `MacroHostPipeTests` class, including all original cases plus nine controlled cases.
- `pipe-deadline-affected.trx`: **112 passed, 0 failed, 0 skipped**, `MacroHostPipeTests`, `MacroHostCatalogTests` and `MacroHostCatalogViewModelTests` against the same built fix. This adds catalog/presentation coverage, including stale outcomes and request ownership.

The three artifacts are retained under `wt/D3/artifacts/sessions-tests-0.76-pipedeadline/`. Before/After checkpoint `0.76-pipedeadline` records matching fingerprints for all three source prompts, scopes `PROD-UI-03`, `CAP-13-01` and `CAP-13-04`, and binds those artifacts. `git diff --check` passed. No original tests were removed, disabled, weakened or given longer timeouts.

Constitution Rules 11–13 and 18 were considered: each operation still owns and disposes its timer, linked cancellation and pipe; caller/local stop takes precedence; concurrency remains one request; no loop, thread, credential access or persistent state was added. No real account, production Personal endpoint, Roblox client or paid cloud test was used. Integrated repeated full gates remain the parent integration's separate work; this focused evidence is not live or release acceptance.
