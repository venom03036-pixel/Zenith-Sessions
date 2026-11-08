# Protected private-server launch trial (0.62)

This increment implements the attended private-server join path requested by the owner. It is **not live verified**. No real account, browser sign-in, Roblox client, service mutation, purchase, subscription renewal, or paid cloud test was used.

## Behavior

Select a checked saved private server under Sessions → Join a game, a saved signed-in account, and a standard Roblox player installation. With **Official page** selected as the native launch mode, **Review private-server trial** shows the account, place, saved server name/code suffix, selected executable, and extra arguments. Confirming opens the app-owned official page with the protected invite; the user must choose Play there. Only that review can authorize the one queued request. Private batch launching, direct-ticket private construction, automatic retry and replay after restart remain unavailable. Existing ordinary launch qualification does not confer private-server consent.

The queue receives only the saved record ID/revision and place, never a private code or URL. A private target has its own server kind and cannot masquerade as a public job. Format 9 is written only when a private reference occurs; older formats with private semantic markers fail closed. Existing nonprivate queues retain their earlier format. An old build must preserve and refuse format 9 rather than replay it as a public request.

The preparer reopens the exact saved record under the store's writer and cross-process file lease. The lease owns the protected code until the native start boundary or cleanup. Missing, removed, wrong-place or changed-revision records fail before opening the browser or advancing the account's grant. The same account identity/generation, final protected mutation, executable trust/qualification, resource admission, pacing, process receipt and cancellation boundaries remain in force. Deleting a saved server first stops pending launch work and pauses the queue, including a request not yet published to the UI. An already started client stays open.

## Protocol scope and limits

The conservative parser extends the existing source-derived PlaceLauncher grammar with **RequestPrivateGame** containing exactly browserTrackerId, placeId, linkCode, joinAttemptId and joinAttemptOrigin=PlayButton. The code must exactly match the held protected record. The facts were inspected in the owner's local RoALT checkout (`src-tauri/src/launcher/uri.rs`); its implementation was not copied. This is source evidence, not verification of Roblox's current live protocol. Access-code-only requests, extra private fields, mismatched codes, duplicate fields, public fallback and direct-ticket private construction are refused. The accepted original opaque request is forwarded once without rewriting it.

The protected invite necessarily appears transiently in the owned official-page navigation and the captured official native request. Neither is saved in queue/configuration/evidence or logged. Managed strings and operating-system process command lines cannot be claimed to be securely erased. The existing ephemeral browser cleanup remains responsible for browser data.

Matching reported player/place IDs do not identify the private server. Private trial evidence therefore cannot qualify ordinary launching or claim private arrival; an attended check in the actual client is still required.

## Executed offline evidence

All artifacts are under `artifacts/sessions-tests-0.62-privatetrial/`.

- `private-lease-red.trx`: one expected failing test before the protected store lease implementation.
- `private-protocol.trx`: 108 passed, 0 failed.
- `private-ownership.trx`: 200 passed, 0 failed.
- `private-clear-red.trx`: one expected failing test reproducing a throwing cancellation subscriber escaping signed-server cleanup and preventing other private account data from clearing. The fix isolates that cancellation failure and records only its exception type.
- `private-reviewed.trx`: 1,168 passed, 4 failed. Three fixtures still labeled version 9 as future after format 9 was added; they now use version 10. The fourth caught a changed status message for a blocked public trial; the original prerequisite behavior was restored.
- `private-final.trx`: 1,173 passed, 0 failed, 0 skipped. Synthetic protected files/account grants, protocol, queue/restart, provider, executable qualification, cancellation, evidence and offscreen production controls; all paid tests explicitly excluded.
- `private-removal.trx`: final focused follow-up for the unconditional removal fence and a preparation deliberately ignoring cancellation; see the artifact counters for the exact result.

Two test-only command/member typos were corrected after compiler failures; those were not counted as behavioral red evidence. `git diff --check` passed.

Rule 18 review: work is explicit and bounded to one prepared launch, one protected record, the existing queue capacity and browser deadline. No new timer, worker loop, package, input path or capture path was added. Each lease has one owner and disposes its protected code and file/writer ownership. Cancellation/refusal paths are exercised with synthetic fakes. The queue format bump is the material compatibility change; rollback must preserve format-9 files for the newer build rather than downgrade them.

## Required attended checks

After the integrated build is ready, use a test account with access to a saved private server. Confirm the official page accepts the invite with Zenith's owned browser, emits the exact supported private request, and starts the trusted executable as that account in that private server. Also try an expired/revoked invite, close/cancel the page, stop during preparation, remove the saved server during preparation, and Lock/Panic. Confirm no public-server fallback occurs, no duplicate start occurs, and unknown/unsupported variants remain explicit. Standard public launch qualification and a fresh trial after executable replacement still apply. **LiveTestReady=false** until the root integration/build/test gates finish; this document grants no real-account automation permission.

## Password-vault integration (0.63)

Merged the password-vault integration from f9f91d2, then tested a locked vault both before private preparation and during the captured browser reply. Neither case returns a prepared start or performs a post-lock account API validation; the private code/file lease is released, and a pending grant stays sign-in-required after unlock. `artifacts/sessions-tests-0.63-privatepassword/private-password-merged.trx`: **969 passed, 0 failed, 0 skipped**. No real password, account, browser or client was used. This is regression evidence, not a new live qualification. The 0.62 final removal follow-up completed with **123 passed, 0 failed**.
The follow-up merge of vault guard repair 2cd9864 was checked with `private-password-guards.trx`: **972 passed, 0 failed, 0 skipped**. Private record acquisition releases its writer even if closing a failed file lease throws; saved-server removal signals the queue stop even if a trial cancellation subscriber throws. Checkpoint `0.63-privatepassword-guards` records the original-source fingerprints and this result.