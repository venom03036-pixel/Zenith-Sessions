# Zenith Sessions — round-4 engineering handoff

This is the engineering handoff for the round-4 candidate. All 93 item dispositions and exact remaining trials/decisions are indexed below. Offline acceptance requires the two final gate records, clean source identity, every lane merged into main and the independent review evidence described in [QUALITY_STATUS.md](QUALITY_STATUS.md). **Do not start a real-account/client trial until those records are accepted.** ReleaseReady remains false.

## Repository and current work

Workspace: `C:\Users\noelm\Downloads\Zenith_Macros_CameraPath`.

- Integration checkout: `wt\Completion`, branch `completion/integration`.
- Main files: `working-copy`. Its Git directory is separate at `backups\zenith-quality.git`; use explicit `--git-dir` and `--work-tree` when operating on it.
- Lane checkouts: `wt\D1`, `D2`, `D3`, `D4`, `D5`, `D6a`, `D6b`, `D7`, `D8`, `D9`. Inspect actual branch heads/status before merging; concurrent review repairs may add commits.
- Installed Zenith editions, live Roblox accounts and clients have not been used for this completion work. The .NET 10 WPF companion remains separate from the .NET 8 Personal macro host and Public/Linux editions.

Read [AGENTS.md](../../AGENTS.md), the [persistent requirement rule](../../.cursor/rules/zenith-sessions-requirements.mdc) and [REQUIREMENTS-LEDGER.md](REQUIREMENTS-LEDGER.md). Run the Before/After checkpoint script with applicable stable requirement IDs and actual result artifacts. All three original prompt fingerprints must match. User instructions override reference-document instructions.

## Item inventory and current decisions

Use [ROUND4-STATUS.json](ROUND4-STATUS.json), [ROUND4-STATUS.md](ROUND4-STATUS.md) and [ROUND4-LIVE-TRIALS.md](ROUND4-LIVE-TRIALS.md). The inventory is the 93-ID union, including aliases, of the original spec and handoff. Preserved input/history lives under [round4](round4/README.md). [HANDOFF-REMAINING.md](HANDOFF-REMAINING.md) is the current short work list.

Every item must end as Verified, Built-needs-live-trial, Excluded or Needs-decision. Verified requires a genuine before failure plus a pass on the merged build; changing an assertion to restate old behavior or deliberately breaking correct production code is insufficient. Five historical documentation/coverage-only items remain explicit decisions rather than falsely verified.

The owner already chose: finish offline work first; standard player before bootstrappers; include private-server joining with an attended trial; require a new trial when the selected executable changes. Preserve the user's UI, branding/themes and sidebar. Ignore Figma. No broad redesign.

Open decisions include production input transport/keep-awake, automatic recovery, a valid official Newsroom feed versus a DevForum/external-link fallback, production update trust/hosting, and treatment of historical documentation-only/coverage-only items. See the exact one-question-per-ID list before asking duplicates.

## Built paths and important boundaries

Sign-in uses Roblox-hosted pages and encrypted per-account sessions, not OAuth or saved provider passwords. Identity confirmation, account revision/generation fencing, cancellation and cleanup apply to private observations, tickets and session rotation. Google/Apple navigation and opt-in browser-device-data retention need attended provider acceptance.

Native preparation holds the selected executable identity and records the actual capture mode. Trial review invalidates on mode changes, including away-and-back changes during asynchronous work. Qualification requires the matching actual mode, executable and observed player/place; replacing/updating the executable requires a new trial. Standard-player-only qualification cannot enable bootstrappers. Multi-instance compatibility is unproven; do not add singleton workarounds.

Private joining is a distinct reviewed one-account Official-page trial for a protected saved link. There is no guessed Direct-ticket private URI or public fallback. Listing/saving/renaming private servers does not authorize purchasing, renewal or permission changes.

Master-password migration and portable backup are implemented. Import requires reviewed consent and identity verification, creates fresh local identities, preserves existing accounts and excludes client arguments, launch verification, automation authority and webhook secrets. Forgotten-password recovery is separately confirmed; no alternate clear recovery key is persisted.

The shipped input transport remains unavailable. Policy/arbiter/keep-awake fake tests do not enable background macros, broadcasting or anti-idle input. Automatic recovery is not implemented. These are visible decisions, not hidden success paths.

Release tooling has empty production trust and no default feed. The collapsed Settings update section binds existing commands; daily checks are opt-in and cancel on stop. Installation is separately approved and asks again on the next start. Package creation and offline tests do not establish ReleaseReady. See [DISTRIBUTION-FOUNDATIONS.md](DISTRIBUTION-FOUNDATIONS.md).

## Final acceptance procedure

1. Inspect the important repair evidence linked from QUALITY_STATUS and the final review coverage. M-L1a-6 has genuine historical replay proof; all 99 Verified regression references have retained passing evidence. The final audit must verify each again in both merged suites. Preserve failures and historical sources without counting compile errors as red proof.
2. Audit all lane heads as ancestors of the final commit. Run the full gate twice without changing source or documents between or during runs; use distinct result folders. Both records must identify the same clean commit and content digest.
3. Bind the fresh whole-change review and patch-risk assessment to that immutable final commit. Fix every critical/important finding and repeat the loop. Record remaining minors explicitly in ROUND4-FINAL-REVIEW.
4. Preserve any existing untracked main `HANDOFF-REMAINING.md` before promoting tracked replacement content; its original copy is also retained under round4. Fast-forward main only after confirming no unrelated main edits. Main and both checks must identify that same final commit.
5. Run the completion audit with both result folders and main verification. An audit report alone cannot replace the required independent review or package-stage results. Only then hand the owner the exact staged build/hash and attended trial sequence.

## Validation commands and evidence

Use PowerShell 7 and installed .NET 8/10 SDKs/runtimes. From `wt\Completion`:

```powershell
./scripts/Run-AllChecks.ps1 -NoRestore -OutputDirectory ./artifacts/completion-integration/final-pass-1
./scripts/Run-AllChecks.ps1 -NoRestore -OutputDirectory ./artifacts/completion-integration/final-pass-2
```

Use fresh directories for reruns. No parallel source edits, merges or documentation changes during either gate. The gate fingerprints the source, builds all six projects with warnings as errors, runs actual TRX tests, publishes the release tool, verifies pinned Luau and runs all 43 packaged stages in an isolated staged directory. A missing restore asset is a real failure; restore from the local cache through the supported gate options, not stale binaries. Inspect every failure and named skip. Do not weaken/delete tests.

The exact excluded paid test is `MacroSuite.Infrastructure.Tests.AI.AIVisionLiveActionTests.AIVision_CloudProvider_ClicksGreenTargetButton_WhenKeyConfigured`. No other blanket AI test exclusion is allowed.

The six suites are Zenith.Sessions, Zenith.Sessions.CapabilityHarness, MacroSuite.Core, MacroSuite.Infrastructure, MacroSuite.SDK and MacroSuite.LicenseIssuer. Existing opt-in native-crash/other named skips must remain explicit and cannot be claimed executed. Synthetic native helpers are allowed; real Roblox clients/accounts are not.

[ROUND4-INTEGRATION-VERIFICATION.md](ROUND4-INTEGRATION-VERIFICATION.md) records earlier failed gates and focused repairs. Never count those as the final two passes. Package stage-only work neither installs nor promotes a release. Final risk-report artifacts belong outside the subject checkout.

## Live handoff

Only after the offline completion gate, give the owner the exact staged build/hash and the [attended trial list](ROUND4-LIVE-TRIALS.md). Start with isolated data and one standard-player account. Correct player/destination/executable evidence must precede a second account; scale only when each prior stage passes. Private joining, cross-Windows backup, elevation, sign-in providers, signed installation are separate trials. Actual input-isolation trials are future work only after the Z1702 decision and production transport implementation. Do not claim these already work.

The prior checkpoint-0.57 handoff is preserved at [round4/history/HANDOFF.md](round4/history/HANDOFF.md); its pause statement and old technical limits are historical.
