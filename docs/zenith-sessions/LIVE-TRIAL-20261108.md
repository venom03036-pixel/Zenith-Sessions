# November 8, 2026 native launch trial

## Scope and result

The owner authorized using the existing saved profile they called Seth Blaze Man, confirmed the displayed Roblox username, selected **Anime Card Clash**, and permitted replacement of the pre-existing client. This authorization supersedes the earlier offline-only restriction for this specific live trial. The paid cloud AI test remains excluded. The owner was away; no new sign-in, challenge or credential entry was performed by the agent.

The tested offline-accepted candidate was `3294d8a8338c515ec1cae87f6497756915359ea3`, staged at `wt/Completion/artifacts/sessions-manager-review`. Its app DLL SHA-256 was `30B74D02DB9680D8095D432036CBC4768CD5D87356CF8168BB77D553B059B2F9`. Subsequent repairs require their own validation; this live evidence does not automatically transfer to a changed executable.

**Saved-session validation and the signed-in private Roblox page worked. No new native client, selected-account arrival, multi-instance behavior or bootstrapper launch was verified.** No trial was recorded as verified, and ordinary launch qualification remains off. The saved profile survived reopening and remained present after the failed preparation, with sign-in renewal required.

## Actual trial sequence

1. Closed the unresponsive covering AMD AutoUpdate window after explicit owner authorization, terminating only its exact verified process after ordinary Close and Alt+F4 failed. Zenith navigation then worked. No unrelated process was stopped.
2. Used Accounts' normal saved-sign-in check. The UI reported **1 verified**. This establishes the saved authorization check, not a native client's account.
3. Selected the standard Roblox-installed player in `version-f0ea5da9412f4e35`. Searched for and selected Anime Card Clash by RNG Lab, place `110829983956014`, universe `6682487255`.
4. The Official-page trial at **23:04:54 UTC** failed at publisher verification: `Unknown / RevocationUnavailable`. Preparation, browser capture and dispatch were not reached; cleanup completed. The typed export contains no process receipt.
5. Ran an explicit Windows online chain check against the executable's public signing and timestamp certificates. Both chains built with no reported errors using Online revocation, ExcludeRoot, NoFlag, current UTC, separate code-signing/timestamp EKUs and a 15-second retrieval timeout. This allowed normal Windows-managed certificate retrieval/cache updates; no explicit trust-store import or security-policy change was made. The selected file stayed at SHA-256 `F98B351FEBDE8A4F89F8B25B8B07AF62E6DD772CC3EEE374A4F77F0DCF477713`.
6. Zenith's **unchanged** Verify publisher action then verified that exact file's Roblox publisher using cached chain/revocation evidence. The helper's result alone was not treated as launch permission. Windows documents that successful online OCSP/CRL retrieval enters the current user's Cryptnet cache: [Microsoft CRL semantics](https://learn.microsoft.com/en-us/windows/win32/seccrypto/certificate-revocation-list-semantics).
7. Retried Official page at **23:14:50 UTC**. Its isolated Roblox page displayed the expected saved account and Anime Card Clash. After choosing Play, the trial ended at **23:15:28 UTC** with `CaptureRejected / MalformedHandoff / InvalidShape`, publisher verified, no dispatch record, no process receipt, and owned cleanup complete.
8. Selected and saved the existing Direct-ticket mode. Its reviewed start was refused twice before queuing, with the generic review-changed message. This was **not a direct-ticket HTTP/native launch test**. A normal local account-list reload revealed `Sign in required`; the earlier `Saved link` display was stale. No attempt was made to override that protected state or reuse an uncertain saved session.
9. Left the queue paused, with no qualification recorded, and exited Zenith normally before rebuilding. The old Roblox window remained present. Direct ticket is the saved mode for the next explicit review; no launch resumes automatically.

## Established defect and unresolved compatibility

The failed official-page preparation durably fences the grant into a new `SignInRequired` generation before browser work. A malformed capture returns before a prepared intent is published. The queue therefore retains its old intent generation. The shell formerly scheduled a nonsecret account reload only when that intent generation changed, leaving the old linked status visible. A fresh trial's grant comparison correctly refused the stale review. This is a presentation/recovery defect; the authorization check itself failed closed.

The current parser rejected the actual handoff, but the original diagnostic exposed only `InvalidShape`. Reference-source fixtures suggest optional nested parameters as one possible difference; they do **not** prove what this live page emitted. Do not widen the grammar based on that guess. The follow-up adds fixed rejection reasons without raw parameter names, values, tickets or URLs; it does not change accepted launch grammar.

The initial publisher-cache problem was resolved for the selected file by Windows validation. The product still has no explicit online certificate-evidence refresh action; app Verify publisher is an offline check. Do not weaken it to make a launch pass.

## Bootstrapper and multi-client limits

The inspected local Froststrap repository was `f83e01149c9d36f510bb59b99d05bee258ed4590`. It starts a separate player process after wrapper/update work; its private child watcher is not a public request-bound process receipt for Zenith. Its account-switching source also writes shared Roblox sign-in state. No source was copied and no wrapper was launched during this trial.

Zenith needs exact child PID/creation-time/executable association, owned cancellation/reconciliation and qualification bound to both wrapper and player before bootstrapper launching can be enabled. A built-in installer/updater is a separate lifecycle task. Duplicate installations alone establish neither account isolation nor concurrent-client support. The current standard-player-first gate remains intact, and one saved account cannot establish isolation between two different accounts.

## Offline repairs following this trial

The shell now schedules its existing owned, nonsecret metadata reload when preparation leaves `Preparing` for a failed, cancelled, blocked, review-required or uncertain result, even when the queue intent generation did not change. It waits for native cleanup and defers the reload while locked. Trial review also requires a currently linked, unexpired cached authorization; the independent vault comparison remains authoritative. The change does not renew credentials, retry preparation or resume launching.

`NativeTrialMetadataRefreshTests.cs` exercises the real temporary repository, DPAPI storage, saved-session preparation and queue with injected synthetic identity/browser/monitor services. Against the two original production files from `3294d8a`, the final eight tests produced **seven intended failures and one passing healthy control** in `artifacts/completion-integration/live3-metadata-replay/metadata-finalfile-before.trx`. The adjacent `source-replay.json` records the unchanged final test-file hash and exact byte restoration of both production files. The seven failures cover malformed/rate-limited/cancelled preparation, Lock/Panic and known unhealthy/expired trial admission. The passing control preserves a healthy sign-in when preparation fails before grant reservation. An earlier focused fixed-source run passed all eight tests.

The handoff diagnostic repair is documented in [HANDOFF-REJECTION-DIAGNOSTICS.md](HANDOFF-REJECTION-DIAGNOSTICS.md): six runtime failures against the original parser, followed by 174 focused passing checks. Fixed enum reasons reach the existing shape text and redacted export; accepted grammar, grant fencing and dispatch authority are unchanged. The next live trial must identify the actual rejected rule before any compatibility change is proposed.

After exact source restoration, the combined affected suite passed **478 tests, zero failures/skips**, including all eight metadata tests and the diagnostic/provider/export integration paths: `artifacts/completion-integration/live3-metadata-affected-after/metadata-affected-after.trx`. The original parser acceptance checks remain in force. Existing assertions were retained; the strict exported-property assertion adds only the new optional fixed reason.

Neither repair is a successful live launch. Final acceptance of the follow-up is the separate immutable review and two complete clean-commit gate records, not these focused runs. Original failures and compile/setup mistakes remain recorded and are not counted as passing evidence.

## Evidence and next live steps

Detailed local observations and sanitized exports are under `wt/Completion/artifacts/live-trials/20261108-01/`:

- `OBSERVATIONS.md`
- `official-page-publisher-failure.json`
- `public-signer-refresh.json` and its reproducible helper
- `official-page-shape-failure.json`

Exports retain player/place IDs and timestamps but exclude credentials, raw launch addresses and raw client logs. Keep them local; nothing was uploaded.

After the follow-up candidate passes its offline gates:

1. The owner signs the saved profile in again through Roblox's own page and explicitly saves the session. Do not collect a password or copy browser secrets.
2. Verify publisher for the selected current standard player; if the executable changed, a new trial is required.
3. Review Direct ticket for Anime Card Clash. Record the exact process receipt, current process observation and actual client-reported player/place separately. Do not record qualification unless all required evidence is current and matches.
4. Run the Official-page trial separately with renewed authorization and capture the new fixed rejection reason if it still fails. Treat any parser compatibility repair as a new tested change, not an exception to validation.
5. Only after one successful, verified native trial, assess an additional supported client. Report refusal/replacement honestly. Do not manipulate singleton enforcement or infer cross-account isolation from two windows.

Other observed follow-ups: the game icon loaded but the gallery reported pictures unavailable; Settings installation lists had light surfaces with low-contrast text under the dark theme; the failed trial banner retained its queued wording. These observations are not complete visual or gallery acceptance. This trial does not change the 93 round-4 item dispositions or establish ReleaseReady.
