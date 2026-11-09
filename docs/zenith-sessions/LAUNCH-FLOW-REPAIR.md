# Account selection and launch setup repair (0.80-launchflow)

The owner reported an empty “Who is launching” panel, blocked ordinary launches after signing in, and “No process-start receipt.” The panel rendered only saved-bundle members. Quick join did not check installation qualification before offering ordinary queueing, and the account Launch button could queue a profile with no preferred game (place 0).

## Changes

- The review page now presents the current Accounts page directly, including the same headshot, account identity, selected row and batch checkboxes. Creating a bundle is optional. The count follows checked accounts, falling back to the selected account. Search and paging remain on Accounts.
- Quick join and the ordinary review buttons require the existing exact-installation qualification. A visible “Choose accounts / set up launch” route remains available for a chosen game. This is presentation gating: the queue retains independent checks and still rejects unsupported calls from other entrypoints.
- The review page exposes the existing selected-account trial and record commands. It uses the same one-use review dialog and evidence requirements as the advanced trial. Signing in never fabricates an installation qualification. The selected account, rather than an entire checked batch, is the subject of a trial.
- The account row and inspector Launch actions go to game selection or launch review when the preferred game is missing or the installation is unqualified. Only that account is checked for this single-account action; unrelated batch checks are cleared. Existing pending-work and sign-in recovery behavior runs first.

No native parser, credential storage, installation trust, process dispatch, singleton handling or verification criteria changed. No test was deleted, skipped or relaxed. One exact copy assertion was updated to the new actionable availability sentence; its verification assertions were retained and expanded. The inspector binding assertion now requires the state-aware ordinary account action, exact selected-account parameter and matching label; the prohibition on binding ordinary actions to the trial command remains. The record control remains singular and retains its original evidence guard, moved to review; the advanced section links to it.

## Regression evidence

All files below are under `wt/Completion/artifacts/launchflow-tests`:

- `launchflow-red.trx`: the picker regression fails because the production review page has no picker. Its companion test initially exposed an uninitialized-fixture setup error; that error is not counted as product evidence.
- `launchgate-red.trx`: with the synthetic shell initialized, the ordinary Quick join button is enabled for an unqualified installation, failing the new gate assertion.
- `account-launch-red.trx`: both preferred-game and no-game cases persist a blocked request instead of routing to setup; both new tests fail.
- `launchflow-green.trx`: 97 affected checks pass, including the functional production-XAML picker and unqualified-launch gate tests.
- `launchflow-final-focused.trx`: 85 affected checks pass after the account-action repair, covering account/sign-in isolation, existing queue behavior, and both new account launch cases.
- `launchgate-positive.trx`: a successful synthetic trial enables the new presentation gate; removing selection, editing a pending destination or changing launch mode disables it. Existing fresh process, matching player/place, persisted executable identity and record/forget assertions remain.
- Review found that the inspector's new shared action still had a fixed Launch caption while pending work makes that command cancel. `inspector-action-red.trx` fails with expected Cancel versus Launch selected account. The button now binds the same state-specific label and explanation as the account row. The regression covers queued, stabilizing, review-required and blocked states and keeps the selected-account parameter.

Full qualification is **held**. Two full gate runs on clean code commit `6f5b8134da944adfa4cf0d388766c44f83230dc3` did not pass. Their records are `wt/LaunchFlowCheck/artifacts/launchflow-final-pass-1/checks.json` and `launchflow-final-pass-2/checks.json`. Each ran all six suites with the paid AI test excluded and warning-as-error Release builds. Both withheld packaged release acceptance after the Sessions suite failed. The preliminary `launchflow-full-pass-1` also lacks the pinned Luau tools in the fresh checkout and cannot count; the verified local tools were reused for the subsequent runs. The separate checkout avoids overwriting the owner's running preview. This repair does not change the 93 round-4 item dispositions or establish live/release readiness.

### Remaining offline qualification failures

- Full pass 1: the other five suites passed; Sessions recorded **7,185 passed, 1 failed, 1 pre-existing opt-in skip**. `DistributionSelfCheckSharedStoresTests.AStartThatStallsInsideTheVaultIsReportedAtTheCoordinatorsShutdownNotAtTheVaultsRefusedDisposal` failed its exact cause assertion: expected `TaskCanceledException`, actual `TimeoutException`. Its ten-second start barrier timed out before the intended cancellation path was established. The same unchanged test passed alone (`artifacts/launchflow-investigation/shutdown-investigation.trx`); that isolated pass does not erase the integrated failure.
- Full pass 2: the other five suites passed; Sessions recorded **7,184 passed, 2 failed, 1 pre-existing opt-in skip**. The same shutdown fixture failed again. `SavedSessionPublicationRaceTests.LockOrPanicDuringCommittedSignInReloadCannotPublishLateSuccess(false, true)` also exceeded its unchanged five-second `shell.InitializeAsync` limit, before entering the sign-in publication race.
- A third, diagnostic full Sessions run recorded **7,185 passed, 1 failed, 1 pre-existing opt-in skip** at `artifacts/launchflow-investigation/full-diagnostic/sessions-diagnostic.trx`. Only the same saved-session initialization case failed. Temporary diagnostics added timestamps/task state to the shutdown fixture's failure path; its diagnostic patch is retained in `artifacts/launchflow-investigation/setup-diagnostics.patch`, and the instrumentation was removed afterward. No timeout, assertion, test membership or production code was changed.

The root cause of those setup delays is not established. Suite scheduling/resource contention is a hypothesis, not a proven explanation. After three unsuccessful verification attempts without a qualification-state change, stop the rerun loop rather than retrying until green. Next engineering work is to capture the initialization/start-barrier delay, fix a demonstrated cause without relaxing the tests, then rerun the complete gate twice on the final commit and obtain a fresh independent review. The current delta review found and repaired the inspector caption issue above; it is a self-review, not a new independent approval.

A separately packaged test candidate may be provided with its focused-test and synthetic self-check receipts. Such receipts do not replace the failed full gates, authorize promotion to the accepted build, or prove native startup. Keep this candidate on the integration branch until qualification is complete. The final package receipt is external to tracked source so packaging results can name the exact clean commit without rewriting that commit.

## Current live state and owner trial

During read-only inspection and ordinary queue recovery, the app showed `@sethplayzfan` with a saved sign-in. The old blocked, never-started request was cancelled through the UI; the queue then showed zero blocked or pending requests. Failed historical rows remain as history. A fresh Roblox client was not started in this cycle. The owner elected to test the rebuilt app personally; the earlier failed live handoff is not claimed resolved by these UI changes.

1. Exit the old Zenith preview through its menu (the close button may only hide it), then open the rebuilt package with its accompanying files.
2. In Accounts, select `@sethplayzfan` and choose **Launch**. With no preferred game, choose Anime Card Clash, place `110829983956014`. If the action instead says Cancel, Details or Review, resolve that existing request first.
3. Open **Choose accounts / set up launch**, select the account in **Who is launching**, and use **Test selected account launch**. Cancel any old blocked request for that account first. If the test button is disabled, check the displayed sign-in and installation state.
4. Review the exact account/game/installation. With Official page mode, choose Play on the Roblox page. With Direct ticket mode, the existing direct preparation path runs instead. Do not switch modes merely to hide a failure.
5. If no client starts, inspect **Launch evidence and diagnostics** and provide the fixed stage/reason, never a cookie or ticket. “No process-start receipt” means Zenith did not obtain a start receipt; it is not a request to reinstall or sign in repeatedly.
6. Only when a fresh client reports the expected account and game will **Record this trial as verified** be available. After recording it, ordinary launches become available for that exact executable and launch mode. An updated executable needs a new test.

Native account arrival, persistence through provider rotation, and simultaneous sessions still require their recorded live trials. Bootstrapper support remains deferred; these changes do not introduce an embedded bootstrapper or claim multi-client compatibility.

Final focused review verification: `launchflow-review-green.trx` passes **469 checks, zero failures or skips**, including the state-aware inspector regression. The earlier failed review finding is fixed; its red artifact is retained.

The preliminary full run also detected five markup contract failures after the layout change. The recording control was consolidated to one original guarded button on the review step, and the inspector assertion now checks its deliberate state-aware action and exact parameter instead of the former unconditional queue binding. `launchflow-wiring-green.trx` passes all 48 affected cases; the ordinary-command/no-direct-trial distinction and all verification guards remain tested. The existing six-theme/two-width reachability test additionally covers the newly exposed trial and record actions.
