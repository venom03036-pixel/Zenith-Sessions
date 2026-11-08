# Owned-page native preparation — 0.24

Personal **0.24.0-preview** passed **2,081/2,081 source-build tests** and the complete suite again against the **exact three staged first-party assemblies**. The **41 new preparation/navigation cases passed twice more**. Packaged checks passed **28/28 twice**, with25 matching manifest files, zero extras and zero private/test filename-pattern matches. Final forced builds reported zero warnings/errors. **ReleaseReady:false**; nothing was installed.

## Implemented integration

`RobloxSessionLinkService` now implements `IRobloxNativeHandoffPreparer`. A pinned queue intent, selected saved grant and owned official-page capture can produce one disposable preparation containing the original opaque candidate, exact final protected snapshot and explicitly updated intent. The production composition still uses **UnavailableLaunchProvider**: no product command yet invokes this preparation service or starts a native client. This increment does not satisfy live-test readiness.

The service takes ownership before its first asynchronous operation. It rejects changed profile revisions, nonmatching player/generation, expired/unlinked grants and changed implicit destinations before opening a browser. Under the existing snapshot transaction it commits a SignInRequired pending generation before giving only that account's credential to the capture adapter. Other account operations share the same actual owner; cancellation/disconnect cannot abandon a still-running browser callback.

After actual capture/cleanup, the service requires the exact context-owned, unconsumed, unexpired candidate and the browser's current credential. It rechecks profile and pending grant, reuses existing authenticated-player, replacement-credential and duplicate-account checks, then commits the Linked successor under the candidate's fresh mutation owner. It reads back the final snapshot and requires matching owner/generation/identity. A later queue still must check current authority immediately around dispatch.

Identity projection may change the verified display name/player ID and its single optimistic revision. Other edits, including edit-then-revert, cannot silently rebase this request. The returned intent changes only that permitted revision and final authorization generation; operation, requested place/job, recipients and original intent remain pinned. This is an operation-local preparation, never a persisted replacement secret or an independently granted reservation.

Failure clears the candidate and fences only this operation's pending/final mutation owners. A newer foreign grant survives obsolete cleanup. Lost/unconfirmed cleanup returns CleanupFailed rather than Ready. Cancellation remains registered through final publication; late validation cannot publish success after cancellation has been observed. Arbitrary browser/HTTP/storage exception text is withheld. Existing credential/profile/export/queue/macros formats are unchanged.

## Browser owner and boundaries

`OwnedRobloxNativeHandoffCapture` owns a dedicated instance of the existing private-browser host. That host shares initialization, private-directory ownership, cookie reading, controller shutdown and actual cleanup joining with the diagnostic. The two public contracts remain separate: the existing inspection returns only sanitized counts, while the new adapter may return a ten-second in-memory opaque candidate. Neither contract starts a native client.

The original diagnostic navigation policy is unchanged. New preparation permits the pinned game page or exact pinned web-start URL, including an explicit job ID. Unknown/reordered/extra target queries, other pages and unrelated schemes are rejected. A native navigation exception exists only after an unconditional external-scheme cancellation handler is installed. That handler sets Cancel first, then passes the real current page, initiating origin and untouched URI to the existing conservative parser. No raw handoff enters diagnostics or normal persistence. An SDK gesture flag is not native account/arrival authority.

The browser uses its own marked, temporary InPrivate profile, disabled password/autofill saving and guarded navigation/popups/downloads/permissions. The selected saved credential is seeded only into that owned Roblox cookie manager; no unrelated browser data is read. The current cookie is read before joined cleanup and independently checked by the service. Cleanup timeout/failure clears candidate publication, retains actual ownership and blocks reuse. Browser initialization/exit APIs cannot be promised hard cancellation. Because of that, Exit waits at most 10 seconds for this owner's capture and its cleanup together (Z1559, owner decision of 2026-11-08, option a): past that it releases the private folder's lease, records the unconfirmed cleanup by type (TimeoutException, Launch) behind the fixed native-trial cleanup notice, and leaves the marked folder for the startup sweep to remove once the browser is gone. A cleanup after a normal cancel is unchanged and still joins the real browser exit, for at most 30 seconds.

Actual WebView startup, redirect behavior, exact-job compatibility, sign-in challenges, cleanup timing and current emitted URI grammar remain **unverified**. The source-derived grammar and documented web destination are different evidence; neither establishes a current native account-selecting contract. Browser shutdown plus validation may exhaust the candidate lifetime; that returns Expired instead of extending or replaying the request.

## Executed checks

The27 new service cases use actual owned temporary SQLite/DPAPI files with synthetic browser/HTTP callbacks. They cover pending/final generation ownership, same-player verification, explicit job preservation, projection-only revision changes, stale/ineligible intent, missing/foreign/expired candidate or cookie, cleanup refusal, mismatched rotated identity, foreign replacement during capture/verification, user edits, cancellation/disconnect joining, duplicate-operation refusal, disposal/redaction and end-to-end fake startup through the existing final grant boundary. Fourteen pure navigation cases cover pinned pages/web-start, rejected targets/schemes and unchanged diagnostic behavior.

The packaged synthetic handoff case now traverses the real preparation service before the final coordinator. Its browser callback, ticket, authenticated response, admission and process starter are deliberately synthetic; there is no real network/browser/native start. Existing actual temporary file-sharing/identity tests remain in the full suite. No new runtime failure was reproduced; source review added a final cancellation-publication check and regression before the final complete runs.

Source-build suite finished **2026-11-06 04:53:12.8437641 UTC**. Exact-staged-assembly suite finished **04:54:33.5004896 UTC**. Packaged checks finished **04:53:34.3612431** and **04:55:39.9079985 UTC**. Runtime: .NET10.0.12; SQLite3.53.4.

- App SHA-256: `A388FAF01817B3D0471EF147AD6DC734694CADF61A1DDC0D0C70984A37D86430`.
- Core SHA-256: `79238E6F6F58DD54FABA4ED3B9EC703902096B8F48DD00C95FF2778ACE624405`.
- Windows SHA-256: `F1D3C5D7DFA5CBBC399A03D14ABFAD336D03A9041124B43F3279D438B9FEFF08`.
- Manifest SHA-256: `59E6A9473383007728DA67DBF7C7A8C54700D98E07B7E41D2E8931CCF2BE2931`.

[Verification record](../../artifacts/sessions-manager-verification-0.24.json), [source suite](../../artifacts/sessions-manager-tests/manager-0.24-source.trx), [exact staged suite](../../artifacts/sessions-manager-tests/manager-0.24-final-binaries.trx), [repeat1](../../artifacts/sessions-manager-tests/manager-0.24-preparation-repeat-1.trx), [repeat2](../../artifacts/sessions-manager-tests/manager-0.24-preparation-repeat-2.trx), [first package](../../artifacts/sessions-distribution-check-0.24-first.json), [repeat package](../../artifacts/sessions-distribution-check/distribution-check.json).

Rule18: one service/browser owner, bounded candidate and existing queue/storage limits; no dependency, service, capture/input loop or per-account worker added. Rule20: incremental extension and shared browser implementation, unchanged public inspection signature and saved formats. No installed app/data migration occurred. The review stage is reused. Controlled checks do not prove the shared host's actual desktop/browser behavior; that regression check remains required. Filename matching is not a comprehensive secret or malware scan.

## Next required work

Connect preparation to a queue phase that holds **no memory reservation while awaiting a page/sign-in**. Adopt a final intent only from that queue operation's owned preparation receipt, then revalidate profile/authorization, atomically reserve fresh capacity, and run the existing exact-grant/held-executable boundary. Cancellation, uncertain starts and historical receipts must remain owned through actual completion. Do not silently adopt another grant or hold a reservation through user interaction.

Native account/process/arrival correlation, two-client feasibility, background input, reconnect/broadcast, full UI/performance and2/4/8/16-client acceptance remain unfinished. No RoALT code ran, no real account or production GUI opened, and no installed edition or live client changed. Notify the owner only when the integrated two-account Coloseum trial is ready.
