# D2 account observations and reviewed friendship actions

Scope: R-api-6/R-accounts-9, R-api-13/R-accounts-14, R-api-16/R-accounts-15. Checkpoint `0.59-accountreads`, November 8, 2026. These are explicit, owned operations over saved Roblox sessions, implemented independently for Zenith. They do not use OAuth, a browser identity disguise, another application's credential store, or copied RoALT source.

## Implemented behavior

- **Presence:** Refresh presence uses checked accounts on the current page, or the selected account when none are checked. Up to 100 accounts are pinned before work begins. Each account asks only about its own verified user ID with its own saved session. Offline, online, in game and Studio are server-reported observations; optional malformed place/job IDs are dropped. They never become launch, PID, window or game-arrival evidence. The retained presentation cache has a 100-account cap.
- **Private details:** Refresh private details requests only the selected account's balance, Premium membership, verified-email flag, enabled two-step methods, ban flag, friend count and follower count. The seven endpoints are fixed. Missing or unsupported fields remain Unknown. Email addresses, recovery codes and unrelated response fields are not exposed. Results have no storage, export or diagnostics path; changing selected account, disconnecting, Lock, Panic and exit clear their presentation. Cancellation fences late responses.
- **Friendship:** Check 2–20 distinct saved accounts, review every pair in a scrollable confirmation, then explicitly choose Befriend these pairs. The immutable review is single-use and cannot be replaced by an equal-looking object. Every request rechecks the reviewed profile/user/generation bindings. The operation reads relation state first, skips existing friends, sends or accepts only the appropriate reviewed requests, and checks relation state again before saying Friends confirmed. Unknown state, challenge, rate limiting or uncertainty stops remaining pairs. Ordinary cancellation retains an honest partial per-pair report; Lock/Panic/exit discard it. Requests already processed by Roblox cannot be undone by cancelling Zenith.

The UI additions stay in the existing account toolbar and More actions inspector. Both existing inspector expanders, themes, removal control position and checked-sign-in/removal adjacency remain intact. Existing account-setup cancellation wording is preserved, with an account-action label during these commands.

## Authority, transport and lifecycle

`RobloxSessionLinkService.AccountData.cs` owns the new operations in the existing single saved-session lane. A request pins an exact `RobloxAccountContext` (profile GUID, verified user ID, generation), checks current vault allocation/commit state and profile existence, and writes a durable SignInRequired fence before sending its credential. Returned rotations pass through the existing same-player verification and conditional write path. Another writer's newer authorization is never overwritten. A 401 from a details endpoint is not treated as proof of session expiry if the authenticated-user check succeeds.

Friendship mutations additionally validate the acting identity **before** the social action. A first rotation is revalidated before use; a second unverified rotation stops the action. The actor's fence and target account are checked again after preflight. Disconnect/removal tracks either member of an active pair, so removing the target also cancels the operation. Cancel, Lock, Panic and shutdown share the command cancellation owner; commands and link-service work are awaited before disposing their HTTP client or vault.

All request URIs are constructed from fixed Roblox endpoints and validated numeric IDs. The production transport disables redirects and cookie storage and uses an honest `ZenithSessions/1.0` user agent. It sends no forged Origin or Referer. Each HTTP operation has a 20-second deadline and 64-KiB response cap. Response buffers and request byte buffers are cleared. Only an explicit Roblox CSRF refusal permits one retry; a response rotation forbids retrying the original cookie. Challenges are never solved or bypassed. Busy/refused operations stop the run; no automatic retry worker is introduced. A non-rotating read refused with 429/503 may restore only its own exact fence. Ambiguous failures retain the fence and may require a new sign-in.

An observation advances the saved authorization generation even when the cookie is unchanged, because it uses durable fencing and verified completion. Previously captured launch/account reviews can therefore become stale and must be reviewed again. This is a deliberate safety tradeoff. No account/vault file format changes or new packages were added.

## Executed evidence

All account identities and cookies in tests are synthetic; stores are temporary. No real account was signed in, no friend request was sent, no Roblox player was launched, and the paid cloud AI test was always excluded.

Three failures were reproduced before their fixes:

1. `private-disconnect-red.trx`: 1 failed. Private details survived removal of the same selected account's saved sign-in. Disconnect now clears all retained account observations.
2. `friendship-cancel-red.trx`: 1 failed. A cancelled run continued to say underway. The runner now reports interrupted and unattempted pairs, while stop-generation guards prevent repopulating cleared Lock/Panic results.
3. `friendship-identity-red.trx`: 1 failed. A mismatched synthetic identity was discovered only after the friendship endpoint had been called. Mutations now require successful preflight identity validation and reject mismatches before dispatch.

Additional broad checks exposed UI integration regressions (toolbar adjacency, a third expander and a missing optional binding on the older UI fixture). The production additions were adjusted to preserve the original inspector structure and cancellation fallback, rather than weakening those tests.

Final regression artifact: `artifacts/sessions-tests-0.59-accountreads/account-reviewed.trx`. It covers account parsing and transport, session fencing/rotation, reviewed friendship batches, private-data and cancellation integration, existing saved-sign-in/removal behavior, snapshot races, stop failure handling and production account workspace layout. The final run passed 502 tests with 0 failures and 0 skips. No whole-product or live-service acceptance is inferred from them.

Rule 18: CPU/work is bounded by 100 presence accounts, seven details fields, or 190 reviewed pairs. Memory is bounded by those lists, the 100-entry presence cache and per-response 64-KiB buffers. Requests are sequential with cancellable spacing; there are no polling tasks or new threads. Existing cancellation and disposable ownership paths are reused. Error presentation is fixed text/type-only, with unknown fields distinct from false values. Test injection replaces both HTTP and identity providers. Existing vault formats and account metadata are unchanged.

## Source evidence and remaining live work

Primary references inspected: [Roblox presence API](https://create.roblox.com/docs/cloud/reference/domains/presence), [Roblox account settings API](https://create.roblox.com/docs/cloud/reference/domains/accountsettings), [Roblox friendship endpoints](https://create.roblox.com/docs/cloud/reference/features/friends), [friendship status schema](https://friends.roblox.com/docs/json/v1), and [two-step configuration schema](https://twostepverification.roblox.com/docs/json/v1). Only public schemas were fetched. The current friendship Swagger omits its mutation paths while Creator Hub lists them; actual availability and any account-specific restrictions remain live-test questions. Unknown or changed schemas stop safely.

An attended trial still must confirm the current Roblox response shapes, same-player cookie rotation across these endpoints, presence accuracy, private-field permissions, CSRF/challenge behavior, request pacing and friendship confirmation for two disposable owner-controlled accounts. The new confirmation dialog still needs a hands-on keyboard/accessibility check in the final packaged app. Full merged-suite, Release and packaged self-check evidence belongs to integration. No live-test-ready claim is made here.

Rollback removes the explicit commands/client while retaining existing encrypted authorizations and metadata. No observation data needs migration or deletion. A fenced interrupted authorization should be recovered with official sign-in; do not restore an older cookie or manually lower its generation. Remote friendship actions, if later run by the owner, are not reverted by rolling back code.
