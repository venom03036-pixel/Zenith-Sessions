# Saved sign-in completion — 0.59-signin

Scope: M-L1a-6, R-accounts-7 and R-accounts-8. The earlier Z1559 10-second native handoff-probe exit change remains at 823b9b8; this cycle does not change it.

## Behavior

A share-link resolver 401 now performs authenticated identity confirmation inside the same saved-session operation. It conditionally fences the exact sent record before contacting the identity endpoint, then uses the existing same-player, generation-bound verify-and-save path. A valid account stays Linked, including a separately verified replacement credential, and the share-link answer becomes Unavailable. Refusal, identity mismatch, timeout or other unconfirmed response leaves SignInRequired. A NotProcessed confirmation also leaves the fence because the earlier resolve refusal has not been disproved. Newer grants are never overwritten. Provider errors report types only; no credential, share code or response text is logged.

Top-level sign-in navigation additionally permits the exact HTTPS/default-port/no-userinfo hosts accounts.google.com and appleid.apple.com. Roblox subdomains remain permitted. Explicit Save is separately limited to Roblox pages and reads only the app-owned Roblox cookie manager. Downloads, pop-ups and external app schemes remain refused. No OAuth application registration is introduced.

Keep sign-in browser device data is optional and off by default. Only an explicit Save settings applies its draft. The app-owned signin-browser-data/kept folder uses a non-InPrivate WebView2 profile. Session-cookie deletion uses WebView2 DeleteCookies with the exact .ROBLOSECURITY name and no URI, removing that name across domains/paths without enumerating provider cookies. Deletion runs before navigation and before browser shutdown. The profile is marked reusable only after cleanup and confirmed browser exit. Startup or the next admission removes an interrupted/dirty kept folder before reuse. Cleanup failures remove the folder after browser exit, or preserve it with its lease for restart/review if exit cannot be confirmed.

Google/Apple navigation deliberately discards the whole browser folder at close, because provider sign-ins may use storage beyond cookies. This keeps their sessions out of retained device data. Ordinary Roblox sign-ins can retain the cache. No claim of reduced captchas has been measured. Forget kept sign-in browser data uses the same bounded, marked-folder/lease/reparse checks; it refuses an active browser and leaves saved accounts intact. Panic/Lock/Exit cancel and track the Forget operation. Disabling reuse does not silently delete retained data; the UI names the separate Forget action.

## Compatibility and ownership

The new settings Boolean is optional within schema 1; missing means false. Existing account records and vault formats are unchanged. Older builds do not recognize the named kept browser folder and may refuse browser admission; use Forget before downgrading. There are no new dependencies, polling loops or per-account browser owners. Work remains bounded by the existing browser deadline, cleanup limits and folder file budget. Secret data remains under the owned browser and protected vault rather than exported by the application.

## Verification

Final focused run: 160 passed, 0 failed, 0 skipped, using synthetic accounts, temporary folders and injected services. TRX: artifacts/sessions-tests-0.59-signin/signin-complete.trx. Earlier iterations passed 134, 150 and 159 checks; an initial compile error in a newly added test used a nonexistent enum member and was corrected before these runs. The paid cloud test was explicitly excluded. The SDK required the approved unrestricted test token because its Service Control Manager lookup is denied under the restricted token.

Coverage includes valid/refused/mismatched/cancelled identity confirmation, rotation, newer-record preservation, expected failure redaction, provider host lookalikes/ports/userinfo and Roblox-only Save, kept-folder restart/reuse/crash/lease/default-off behavior, provider/cleanup discard, explicit settings save, and Forget preserving account metadata. Existing saved-sign-in, handoff cleanup and publication-race cases also run.

No real account, browser login, native client, third-party sign-in, captcha measurement or live endpoint was exercised. Attended trials must confirm the current Roblox/Google/Apple flows (including whether Roblox requires a pop-up), cookie deletion/flush behavior, and actual retained-cache effects. This cycle does not declare LiveTestReady or release acceptance. Integrated full-suite and packaged checks remain the parent integration gate.