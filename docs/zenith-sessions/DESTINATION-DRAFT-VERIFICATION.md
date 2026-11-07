# Server destination drafts and queue verification

Personal development preview 0.50. ReleaseReady and LiveTestReady remain false.

## Working paths

Typing or reviewing a new server link preserves the previous request for reference. While the draft is pending, Zenith pauses actions that would consume the previous request. The Join a game page shows how to choose the new destination or discard the draft. Discarding makes the previous request available again. The same rule applies to checked-account requests, saved bundles, link copying, favorite saves, browsing, and native trial or inspection entry.

When a new destination replaces the previous one, its place and server data commit together before availability returns. A checked-account command pins the exact requested destination, account revisions and sign-in generations before notifying command subscribers. Bundle commands retain their existing pinned recipient and destination behavior. Panic invalidates commands captured before the stop and still waiting to submit their requests. A server response superseded while publishing rows cannot leave an old continuation cursor or status on the new place.

The change adds no dependencies, polling, credentials, storage formats, or new launch authority. Updates stay on the existing presentation and queue lanes. Server page processing remains limited to 25 results per request. The candidate build uses a separate intermediate output folder to avoid files held by an open development preview.

## Executed evidence

- **4,169 source tests passed** and **4,169 passed against the exact staged first-party assemblies**. The affected 319-case suite passed twice more. Fourteen new cases cover both queue routes, exact server selection through Search / Use, draft discard, command callbacks, favorite saves, pending browser actions and superseded responses.
- **42 packaged checks passed twice**. Both forced builds reported zero warnings and errors. The manifest check verified all 29 staged files and matched all staged first-party DLLs to the files used in the second full test run. No private-data filenames or redirected paths appeared in the candidate.
- App SHA-256 is `44DF884E551554A1B10E718F8AF399E7ECEBFFD1F3BE5464D73FAE28E3E74249`. The [machine verification record](../../artifacts/sessions-manager-verification-0.50.json) has SHA-256 `A42127182CAEB0B7379F62DEAC61FB1C8F85AE6EF1CFF33B8AB1DEEA4673DBFB`.

## Reproduced failures

- In 0.49, two isolated regressions reproduced writes for the previous request and a synchronous copy callback that returned the previous link.
- With the first destination correction in place, a separate regression reproduced a previous place's server rows attaching to the replacement place during publication.
- The first full 0.50 run hit the existing Windows file-holder query's time budget. Its isolated retry passed, and the next full run passed. The query and its deadline were preserved.
- The subsequent candidate rebuild found the shared build folder locked by an existing development preview. The script now uses a separate fixed build folder for the candidate.

The destination tests exercise requests and their queue receipts using isolated data. They do not establish native account selection, successful game or exact-server arrival, unattended reconnect, background macros, or Roblox load and performance. Those remain live acceptance items. No installation or real Roblox session was changed.

## Backend 0.55 exact batch commit

Call `LaunchCoordinator.CaptureBatchReview` with the resolved destination and selected saved profiles with their sign-in bindings. The review retains the place, optional exact server, profile IDs, revisions, priorities, sign-in bindings and queue stop generation. It accepts one submission on its original queue. Duplicate selections for the same profile are coalesced when their captured data match. A conflicting selection requires a fresh review.

After explicit confirmation, pass the review and the caller's cancellation token to `EnqueueBatchAsync`. It returns one ordered result per reviewed account, including already-pending, queue-full and unavailable-forecast results. For recorded rows, inspect the operation state to distinguish queued, blocked, or cancelled. The command does not resume the queue. All starts still use the ordinary provider gate, exact authorization validation, resource admission and arrival checks. Existing single-account and trial entry points keep their contracts.

The queue writes the accepted subset together and wakes the scheduler after the durable commit. A stop or cancellation during that write produces a durable cancellation of only the newly accepted entries. Shutdown waits for the writer and that correction. Restarted nonterminal requests still require manual review. There is no queue-format migration or new worker, dependency, credential read or native authority. The batch API can be wired into the separate UI work when it is ready.

- **567 focused checks passed twice** after a build with zero warnings and errors. Eleven new cases cover exact-link batch persistence, ownership, stop/cancellation, storage failure, queue capacity, and per-account dispatch gates.
- The sixteen-account case used the actual temporary Windows queue store and counted one batch write. The earlier backend reproduced the shutdown race against the existing single-request API.
- The [verification record](../../artifacts/sessions-launch-verification-0.55.json) has SHA-256 `6B5A7CE9D31F741D08E392226F4DE53B7642BFFD8FC549FFBA24009C25CBCF66`. The staged preview remains **0.50**. No actual Roblox client or arrival was exercised.

## Backend 0.56 exact server retry

`CaptureBatchRetryReview` takes the selected prior operations and their current profiles and account bindings. Every operation in a review must have requested the same exact place and server. Only a provider-rejected start with no process receipt qualifies. The review pins the original server and the current profile revision and authorization generation for the same Roblox player.

Submit that review once with `EnqueueBatchAsync`. The queue compares each source to the latest retained operation for its profile under the same lock that owns the write. An altered, superseded, or missing source returns `RetryNoLongerEligible`. A pending or unresolved operation returns `AlreadyPending`. The eligible rows share one write, and ordinary admission, authorization and provider gates apply at dispatch. Panic invalidates an unsubmitted review. Queue history is bounded, and older attempts can age out. Startup failures that might have opened a client require reconciliation. The feature requires explicit review and preserves queue pause state. The queue file format stays the same.

- **580 focused checks passed twice** after a build with zero warnings and errors. Thirteen new cases cover the retry rules, including a synthetic batch with one successful arrival, a wrong or stale source, a changed account binding, Panic and a reopen through the actual temporary Windows queue store.
- The [verification record](../../artifacts/sessions-launch-verification-0.56.json) has SHA-256 `2B209579E400E9E66ED7128D9AF99C0EDD77A2608BC3C51F5693EBF4DE29C4C7`. The separately staged app remains **0.50**. UI wiring, real native launches and verified live arrivals remain pending.
