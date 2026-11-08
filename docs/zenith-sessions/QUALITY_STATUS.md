# Zenith Sessions — round-4 quality status

This is the current completion record for the Windows Personal companion. The older quality narrative is preserved in [round4/history/QUALITY_STATUS.md](round4/history/QUALITY_STATUS.md) as historical evidence only.

The item dispositions below describe the code. Final offline acceptance is established separately by two successful `artifacts/completion-integration/final-pass-*/checks.json` records for this exact clean commit, the final completion audit, lane ancestry on main and the immutable review report. Inspect those records before calling the candidate accepted. No live Roblox, attended installer or production-release acceptance is claimed.

## Item accounting

[ROUND4-STATUS.json](ROUND4-STATUS.json) is the machine-readable item ledger; [ROUND4-STATUS.md](ROUND4-STATUS.md) is its readable index. The union is exactly **93 IDs**: 78 work items, nine explicit aliases and six extra IDs from the supplied handoff. Source inputs remain unchanged under [round4](round4/README.md).

Final item classification: **40 Verified, 40 Built, needs live trial, 3 Excluded, 10 Needs decision**. Each Verified entry names a regression and failing-before evidence; the completion audit must find its test Passed in both final merged suites. Historical “fixed” or “implemented” labels alone are not proof. Documentation-only changes and coverage that already passed the original implementation remain owner decisions.

Exact attended steps and one question per decision are in [ROUND4-LIVE-TRIALS.md](ROUND4-LIVE-TRIALS.md). An alias shares its primary item's disposition. Unsupported production input is explicitly a decision under R-instances-17/Z1702; a fake executor does not qualify live background input.

## Current implementation and limits

- Roblox-hosted saved-session sign-in replaces OAuth for the companion. Passwords stay on the provider page. Session credentials are identity-checked, encrypted locally, fenced by account revision/generation and cleared from transient work on Lock/Panic.
- Optional master-password protection and portable password-sealed backup are built. Import creates fresh local identities, verifies consent and preserves existing accounts; it does not import launch qualification, webhook secrets, extra launch arguments or input authority.
- Official-page and Direct-ticket native launch modes are separate. Ordinary standard-player launches require a recorded attended trial tied to the actual capture mode and exact executable identity. A changed/replaced executable requires a new trial. Bootstrapper launching is deferred; publisher inspection does not enable it.
- Protected private-server saving/listing/rename and an explicit single-account official-page joining trial are built. No speculative direct-ticket private join, public fallback, purchase, renewal or permission mutation is implemented.
- Headshots, matching game media, server browsing, session/window overview, client controls, monitoring, local diagnostics and settings are wired. Synthetic/native-helper evidence is separate from real-client behavior.
- Production automatic recovery and target-directed background input remain decisions. Keep-awake policy and runner are tested but cannot operate with the shipped unavailable transport.
- Optional signed update checking, staging, approval, packaging and per-user installer tooling are built. Settings exposes a collapsed update section. Embedded production trust keys remain empty; no default feed or automatic installation is enabled. ReleaseReady remains false.

## Review and integration evidence

[ROUND4-INTEGRATION-VERIFICATION.md](ROUND4-INTEGRATION-VERIFICATION.md) records every integration failure, its cause and the affected passing rerun. Failed full gates remain failed; focused green results never replace the full acceptance gate.

Material repairs and retained failing-before evidence:

| Boundary | Evidence |
|---|---|
| Executable identity and actual launch mode; stale/ABA review and delayed preparation | [EXECUTABLE-QUALIFICATION-REPAIR.md](EXECUTABLE-QUALIFICATION-REPAIR.md) |
| Password migration, dispatch ownership and portable import authority | [MASTER-PASSWORD-VERIFICATION.md](MASTER-PASSWORD-VERIFICATION.md), [PORTABLE-BACKUP-VERIFICATION.md](PORTABLE-BACKUP-VERIFICATION.md) |
| Process controls, stop ownership and stale results | [PROCESS-CONTROL-REVIEW-REPAIRS.md](PROCESS-CONTROL-REVIEW-REPAIRS.md) |
| Shutdown helper ownership and network-observation epochs | [SHUTDOWN-NETWORK-REVIEW-REPAIRS.md](SHUTDOWN-NETWORK-REVIEW-REPAIRS.md) |
| Signed stage integrity and updater stop precedence | [DISTRIBUTION-FOUNDATIONS.md](DISTRIBUTION-FOUNDATIONS.md) |
| Actual historical self-check lifecycle failures | [LIFECYCLE-REPAIR-VERIFICATION.md](LIFECYCLE-REPAIR-VERIFICATION.md) |
| Initial public-network lock before update scheduling; Settings commands | [ROUND4-INTEGRATION-VERIFICATION.md](ROUND4-INTEGRATION-VERIFICATION.md) |
| Pending settings and startup work cannot undo Panic; Lock retains alert monitoring | [SETTINGS-STOP-REVIEW-REPAIRS.md](SETTINGS-STOP-REVIEW-REPAIRS.md), [STARTUP-STOP-REVIEW-REPAIRS.md](STARTUP-STOP-REVIEW-REPAIRS.md) |
| Elevated upload-limit helpers retain ownership through actual exit | [UPLOAD-LIMIT-OWNER-REVIEW-REPAIR.md](UPLOAD-LIMIT-OWNER-REVIEW-REPAIR.md) |

The final review coverage and remaining findings are recorded in [ROUND4-FINAL-REVIEW.md](ROUND4-FINAL-REVIEW.md). A changed commit requires review of that delta and two new complete gate runs; focused green runs cannot substitute for them.

One recorded minor: a dashboard initialized before another instance enables a vault password may use cached mode at Unlock and omit the password prompt. Credential reads still refresh on-disk control and fail closed. This is a control/presentation mismatch, not demonstrated credential access.

## Final acceptance contract

From a clean candidate commit, run `scripts/Run-AllChecks.ps1 -NoRestore` twice into distinct result folders. Each run must record the same commit and content digest throughout, all six suites with actual TRX results, warning-as-error Release builds, the release-tool publish, pinned Luau and all **43 packaged stages**. Existing named skips must be reported; zero discovered tests, stale artifacts or a failed suite cannot count. The exact paid cloud AI test is excluded by fully qualified name.

Both successful records, all lane ancestry and a fresh whole-change review must refer to the commit promoted to main. Any subsequent code/document change invalidates that pair and requires two new runs. The final risk assessment binds the immutable base/head and patch digest. No claim here substitutes for those external records.

Never use real accounts/clients, paid cloud AI, weaker/deleted tests or invented failing-before evidence to obtain green results. Stop a full round with only minors and list them, or after three rounds with no item-state progress. Do not call incomplete work complete.
