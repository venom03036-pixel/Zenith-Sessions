# Publisher and leftover CPU-cap guidance

Cycle `0.75-guidance`, based on clean source commit `643cf0eea5e801bb53b2aa015f774a94f93f15a3`.

## Changes

- `ClientExecutableTrustResult.Summary` now distinguishes accepted publisher evidence from launch permission in the accepted, unaccepted and changed-signer outcomes. Each says bootstrapper launches and trials are deferred and directs the user to the standard Roblox player. The accepted outcome retains the explanation that the bootstrapper manages its own settings and Zenith does not write client files. No publisher policy, provider gate or launch behavior changed.
- The startup recovery notice in `ShellClientPriorityFeatures` now reports a possible leftover CPU cap. The query can confirm the recorded process still exists in a Windows job, but cannot read the former job's rate limit. A record can survive an unconfirmed apply or cleanup, so the message no longer claims a definite cap or a prior crash. Recovery remains conditional: close and reopen the affected client if a cap remains. No native resource, ledger, cleanup or process-control behavior changed.

The existing layout and themes are unchanged. This is status and recovery wording, not new native capability.

## Failing-before and passing-after evidence

Both runs used the Release `Zenith.Sessions.Tests` project with `-m:2 -nr:false` and explicit focused filters. No paid cloud test, real account, Roblox client, administrator prompt or process-cap mutation was used.

- `artifacts/sessions-tests-0.75-guidance/guidance-red.trx`: **3 failed, 2 passed, 0 skipped**, with unchanged production source. The existing bootstrapper summary/diagnostic test failed its added deferred-launch guidance assertion. The existing singular/plural CPU notice cases failed their corrected exact-string contracts. Unreadable/uncheckable recovery controls still passed.
- `artifacts/sessions-tests-0.75-guidance/guidance-green.trx`: **157 passed, 0 failed, 0 skipped**. Includes the strengthened summary/diagnostic contract, bootstrapper selection/policy checks, executable trust and shell trust integration, the corrected recovery notices, and synthetic process-priority/CPU-cap ownership regressions.

Existing provider-authorization, signer, diagnostics, startup-ready and event assertions were retained; no tests were deleted or disabled. The CPU notice method was renamed to reflect its corrected uncertainty contract. `git diff --check` passed.

All three prompt fingerprints matched in `artifacts/sessions-requirements-review/before-0.75-guidance.json`. The matching After checkpoint records both TRX files and scopes `PROD-BOOT-02`, `PROD-UI-03` and `CAP-11-03`. The reviewed clauses require explaining disabled actions and representing unknown state without inventing data. Engineering Constitution rules 13 (resource ownership) and 18, plus the agent contract's smallest-correct-change rule, were considered: no new dependencies, loops, threads, persistent state, native ownership or credential access are introduced.

These focused checks do not replace the integrated full recheck, and do not qualify bootstrapper launching, native leftover-cap behavior, live readiness or release acceptance. Parent integration owns the requested repeated full-suite/package checks.

## Denied-query and unreadable-record completion (0.75-guidancequery)

Independent review identified the same overstatement in the denied-query and unreadable-record branches: a recorded attempt is not proof that an old cap was applied. Those notices now describe a possible cap and conditional persistence until client exit. A plural denied-query case was added alongside the existing singular and unreadable-record cases; the other assertions are unchanged.

- `artifacts/sessions-tests-0.75-guidance/query-guidance-red.trx`: **3 failed, 2 passed, 0 skipped** before this follow-up's production change. Singular denied query, plural denied query and unreadable record each failed its exact corrected wording contract; both confirmed-job cases passed.
- `artifacts/sessions-tests-0.75-guidance/query-guidance-green.trx`: **97 passed, 0 failed, 0 skipped**, including all recovery notice cases and existing synthetic priority/CPU-cap ownership cases.

Before/After checkpoint `0.75-guidancequery` verifies the three prompt fingerprints and records these two artifacts under `PROD-UI-03` and `CAP-11-03`. This follow-up changes three message strings only; resource ownership, query behavior and launch gates are unchanged. No real clients, native cap mutation or paid test was used.
