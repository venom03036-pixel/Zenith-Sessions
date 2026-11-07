# Direct saved-account setup and responsive rows — 0.28

November 6, 2026. Windows Personal development preview only. The previous goal turn made verified progress on pacing, request evidence and startup composition. This increment advances the requested account workflow and responsive interface. No installed edition, real account/browser/client, published experience or reference executable was changed or operated. Native account selection, verified arrival, background operation, reconnect and full release acceptance remain unproved.

## Executed evidence

The final forced build passed **2,528/2,528 source-build tests** and the complete suite against **exact staged App, Core and Windows assemblies**. The 30 direct-creation service cases, 17 Shell integration cases and two compact-row geometry cases passed **49/49 twice more** against those assemblies. Both final builds reported zero warnings/errors. The packaged self-check passed **32/32 twice** on .NET 10.0.12 and SQLite 3.53.4.

The [verification JSON](../../artifacts/sessions-manager-verification-0.28.json) records actual timestamps, test/report hashes, retained failures and assembly identities. All **25 manifest entries plus the manifest (26 files)** matched their recorded lengths/hashes, with zero extra files and no matches against the explicitly recorded private/test filename patterns. This is a bounded filename check, not proof about all binary contents.

- App SHA-256: `732DCE42C88232AA292DD7BA9736CD9AF9CD638D332FBBA5EAF23882B045CBEF`.
- Core: `2AFB6A7D3B1F061774C2024FFC14F12A2E95EB0AA4F2C72346D2E777A8917C56`.
- Windows: `C35516BA83F0360F1F81DBCFFF4FE5096BDB4F8E271D8CA1193FDD0BB02BC6F6`.
- Manifest: `A9964AF115E8CE1F1035C1E326E18C15D795F0796DD678C88243EB5E0C7051B0`.

All sign-in/client responses in these tests are synthetic. SQLite, same-user Windows DPAPI, file persistence and actual operation joining run against owned temporary fixtures. A packaged case traverses the real direct-add service, rejects a duplicate, verifies default metadata and removes only its exact synthetic profile through the normal guarded cleanup path. No real Roblox authorization or native launch is inferred.

## Direct account workflow

The primary account-page action is now **+ Add account**. It invokes `IRobloxSessionLinkService.AddAsync` on the existing service owner rather than requiring an alias/profile dialog first. The owned official sign-in window and explicit Save remain unchanged. There is no new password form, browser-secret scraping, cookie-paste path or OAuth registration UI.

The operation allocates a fresh internal profile GUID before asynchronous work; the private browser can use that owner without a database row. It verifies the authenticated user response, validates canonical context and username, independently verifies any rotated replacement, checks expiry and performs duplicate preflight. Only then does it insert a default profile named from the verified username, with that username/player ID and default preferences. The selected account's settings and identity are not inherited.

The existing metadata projection journal, exact protected-store snapshot comparison, final writer-side identity claim validation and owned-mutation cleanup are reused. A duplicate indexed Roblox identity, including a contextual legacy OAuth claim, is rejected without replacing its grant. Direct-add feedback advises using the existing saved row; existing Link/Check behavior remains unchanged. The preflight lookup is not treated as the final concurrency boundary.

Cancellation or verification failure before metadata creation creates no profile. Once metadata has committed, interruption preserves an identifiable sign-in-required profile and any protected recovery files; it never blindly deletes possibly newer work. If a grant write was issued but success was not acknowledged, only that operation's owned mutation is fenced. Shared service ownership and actual browser/network/storage cleanup are joined before reuse or disposal.

Raw legacy v1 records without identity indexes remain preserved. Their player identity cannot be discovered globally through this indexed add path without inventory decryption, which this change deliberately does not introduce. Entered usernames/UserIds never substitute for verified duplicate authority.

## Shell integration and truthful feedback

`AddRobloxAccount` acquires the shared authorization cancellation owner before its first await. Readiness, Lock, shutdown, another sign-in/check and a busy native trial gate entry. It does not create another authentication lane or grant launch permission.

The resulting GUID comes from the service, never from later UI selection. Reload uses the real library and does not plant a delayed `_selectAfterReload` value. A still-current operation can select its new row when the previous selection remains unchanged; changing selection cannot redirect its grant or force a later selection. An added row outside current filters may remain unselected. Stop/Panic/Lock and shutdown fence late success, selection and errors. Closing the sign-in window is normal cancellation even when its provider does not cancel the caller's token. A failed list reload retains error feedback instead of claiming a successful refresh. Disposal and disconnect join the Add command alongside existing operations.

Controlled STA tests cover direct addition without any pre-profile, default metadata, protected reload, duplicate/invalid/expired/cancelled sign-in, selection changes, Lock/Panic before verification and during a postcommit reload, shared Add/Link/Check ownership, failed startup and actual shutdown joining. The native-busy condition is source-reviewed; the new tests do not force a private native-provider state.

## Interface corrections from actual observation

Manual **Profile only…** remains an optional metadata path. The unselected inspector now shows a useful explanation and Add account action instead of blank field/control sections. Launch evidence has an explicit empty message rather than a blank selector. The in-app guide explains verified direct creation, interruption and manual metadata.

An interactive 960 × 640 check exposed normal-row buttons clipped by a fixed status column. Compact rows now move authentication/launch status beneath the name/game and release that column's width, preserving the primary and overflow buttons. The subsequent dense check exposed names truncated to indistinguishable prefixes. Compact dense rows release the separate group column, retaining group filters and full group information in the normal presentation/details. The name gains usable width while authentication and the primary action remain visible. Wider layouts retain their original separate columns.

The new tests apply actual production templates through a `ContentPresenter` at a 400-unit row width, retain long names, assert action bounds/width and exercise the compact triggers. They create no native window or input. The first fixture attempt measured a hidden Window and produced zero layout; the second used standalone `LoadContent`, which does not install DataTemplate triggers. These fixture failures are retained. The final presenter-based fixture keeps strict geometry and trigger assertions; it does not weaken them to pass.

The [final limited interactive check](../../artifacts/sessions-ui-verification-0.28.json) opened the exact staged application with 16 isolated sample profiles, reusing the previous preview's data folder. At observed captures of 1320 × 840 and 960 × 640, account controls and empty inspector rendered; compact normal actions and dense sample names were visible. Dense was toggled and restored, clearing its genuine pending setting. The optional metadata dialog was opened, scrolled and cancelled; the count stayed at 16 and Settings saved remained shown. Exit manager closed the preview normally. The new sign-in buttons were not invoked.

This does not verify every page/dialog, live signed-in rows, all themes, DPI calibration, monitor transitions, stress scrolling or latency. The optional profile dialog's Save/Cancel controls currently require scrolling in a short view; a fixed footer remains a polish task. Broader technical wording, custom appearances, mini mode and full interface acceptance also remain unfinished.

## Compatibility, failures and resource review

The change is additive to the existing pre-release service, Shell and templates. No profile schema, queue writer version 6, macro document, credential format, installed host, Public or Linux release changed. Existing Link/Check/Inspect paths share the same validation and ownership behavior. No new production dependency, row worker, capture loop or automatic API polling was introduced. The one sign-in owner, bounded credential validation, paged metadata and existing writer limits remain in use.

The initial compilation missed the diagnostics enum namespace import; it was corrected before final builds. An initial 31/32 package run failed because the new synthetic profile added a third pending cleanup record to the old exact-two fixture assertion. The check now verifies and drains only its newly created profile before the unchanged final assertion. The cleanup itself had completed; this was fixture accounting, not a demonstrated production cleanup failure.

Rule 18 review covers explicit ownership/cancellation, preserved failures, no secret-bearing diagnostics or plain profile secrets, one operation rather than per-account workers, actual temporary storage and backward-compatible formats. Native/visual performance and production HTTP/browser acceptance remain unmeasured. No capacity, memory-saving, parity or superiority claim is made.

Automatic approval review rejected removal of the obsolete generated `artifacts/sessions-layout-diagnosis` binary folder before the command started, stating “blocked by policy.” It was left untouched and was not retried. The rejected command did not modify data or the verification record; the rejection was recorded separately afterward.

## Remaining acceptance

The native provider remains Unverified and ordinary/group launching remains blocked. The owner has not been invited to the two-account Coloseum test. Continue native account/process/arrival integration, eligible multi-client and background provider work, correct-account reconnect, host/owned-game connection, remaining utilities, complete UI/DPI/soak and measured 1/2/4/8/16-client benchmarks. Preserve real accounts and installed editions while those development checks continue. **LiveTestReady, Installed and ReleaseReady remain false.**
