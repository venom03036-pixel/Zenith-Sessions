# Launch-click and list-selection repair (0.82)

The owner reported that the 0.81 candidate still did not launch, and that the account picker and selected queue rows became white and unreadable. This follow-up keeps the existing layout and saved-session storage. The owner will perform the next real Roblox test; no real account, browser sign-in, Roblox process or paid AI test was used here.

## What was reproduced

Read-only queue metadata and the bounded diagnostic journal showed the latest request waiting without a process receipt, then being cancelled with `ProfileChanged` at the recorded account-disconnect event. The subsequent account link was later. This message was not evidence that a ticket request rejected the saved session: that request had not reached native preparation. No protected session contents were read or altered.

All three visible launch routes reproduced the underlying problem with synthetic saved accounts: Quick join, the review-page action and the account's Launch button merely enqueued a request into the initially paused queue. A separate Resume was required despite the button promising a launch.

The local RoALT sources were re-read, particularly `src-tauri/src/launcher/mod.rs` and `src-tauri/src/roblox_api/mod.rs`. RoALT reads the saved session, negotiates the assertion/ticket using current cookies and starts the selected player from the launch action; there is no separate paused-queue prerequisite. This follow-up does not claim a refreshed reference revision and copies no reference code. Zenith's 0.81 encrypted-session, identity, cookie-rotation and direct-ticket fixes remain in place.

The account picker inherited a white Windows list background. Selected queue containers inherited Windows' active blue/inactive white fills. The companion now owns list/container colors and templates using its existing theme resources, including selected primary and secondary text.

## Behavior and limits

- Quick join, **Launch checked accounts**, and an account's preferred-game Launch now submit and start their captured selection together. Ordinary Direct ticket starts do not require a historical trial record.
- A fresh explicit launch may start from an empty paused queue. It cannot silently resume unrelated older paused requests: resolve those with Resume or Cancel first. Queue-only bundle actions retain their existing behavior.
- Pause, Panic, cancellation, shutdown, current authorization, resource admission and executable publisher checks still apply. A stop during the gesture or durable batch write prevents the new batch from starting. Restart, sign-in and settings changes do not resume work automatically.
- A receipt still means a reported process start, not verified account identity in the game or verified arrival. Saved-account cancellation after disconnect remains intentional.
- No new dependency, worker or timer is introduced. Work and allocations remain bounded by the existing maximum batch/page sizes. Tracked commands join shutdown; failures and unrelated pending work remain visible. Storage formats and credential handling are unchanged.

## Evidence

- `artifacts/launch-click-fix/red/launch-click-red.trx`: three expected launch-route failures before the production change. Its six original UI fixture failures are not counted as product reproduction.
- `artifacts/launch-click-fix/red-theme-final/theme-red.trx`: six corrected production-fragment tests failed on white/default selection colors before the theme repair.
- `artifacts/launch-click-fix/pause-race-red/pause-race-red.trx`: three late-Pause regressions failed before capturing the stop epoch at the launch gesture.
- The initial wider run also exposed a WPF `VirtualizationMode` change after layout. The unnecessary implicit mode setter was removed; locally declared recycling settings remain intact.
- `artifacts/launch-click-fix/final-focused/final-focused.trx`: 753 passing checks, no failures/skips, covering launch routes, native trial separation, batches, directory behavior, navigation, layouts and selection colors. This final focused run includes the actual Window-scope list-container and header resources. The intermediate override check (`window-style-red/window-style-red.trx`) failed for all three queue themes before the Window style inherited the new themed template. Final synthetic renders are in `artifacts/launch-click-fix/rendered-final/`.

The earlier full Sessions run (`artifacts/launch-click-fix/full-sessions/sessions-full.trx`) recorded **7,219 passed, 2 failed, 1 environment skip**. Failures were the previously observed missing-transport Keep Awake timer and `CoalescedQueuePresentationCannotReplayAnOlderProducerSnapshot` (an empty snapshot in its controlled scheduling assertion). The ordering case passed unchanged in the final focused rerun. These failures are retained and are not converted into a clean full-suite result; no tests were disabled or relaxed. The one skip is the existing Windows fault-record environment case.

Final source identity, full-suite results, rendered screenshots and packaged self-check results belong to this repair's artifacts and delivered build receipt. Historical round-4 dispositions and earlier failed gates are not rewritten. Main is not promoted by a focused pass or a candidate package.

## Owner test

1. Exit the old preview, then run the new candidate with all its accompanying files. Keep the existing saved account; signing in again is unnecessary unless Zenith reports an expired sign-in.
2. Select the saved account, choose Anime Card Clash, then click Quick join or Launch checked accounts. With no older paused requests, this click must start the request without Resume queue.
3. Confirm the queue advances to preparation/start, a Roblox window opens, and the intended player joins the intended game. If an earlier paused request is present, cancel it or explicitly resume it first.
4. Select the picker and queue rows, then focus another control. Names and details must remain readable on the theme background.
5. If startup fails, retain the queue row and its Launch evidence from this app run before closing Zenith. A missing receipt alone does not identify the failing stage. Actual multi-account behavior still needs separate attended testing with distinct signed-in accounts.
