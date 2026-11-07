# Native account provider: official evidence

Reviewed November 5, 2026, against current public Roblox documentation and dated first-party statements. Scope is the separate Personal companion. This is a bounded documentation review, not a live authorization, native-client compatibility or policy approval result. No account endpoint was exercised, browser credentials read or client changed.

Current conclusion: Roblox documents identity linking and Open Cloud access, plus ordinary signed-in browser → Play. The reviewed sources do not establish a supported third-party handoff from saved Open Cloud OAuth grants to a selected native player account. Zenith's production native provider therefore remains unavailable; this is not a claim that every possible future integration is impossible.

## OAuth identity and public clients

Roblox's implementation guide requires PKCE for public clients that cannot retain a confidential secret. Authorization-code exchange uses client ID and verifier instead of a client secret. The existing desktop exchange is consistent with that documented distinction; adding an embedded secret would not resolve native launch. “Public client” describes secret-handling ability and is separate from an app's reviewed/public distribution mode. [OAuth implementation](https://create.roblox.com/docs/cloud/auth/oauth2-develop)

The endpoint reference supports `prompt=select_account` for the authorization screens and identifies `/userinfo.sub` as Roblox UserId. These support the current identity-linking adapter, not selection of the native player's signed-in account. Access tokens authorize scoped API resources; ID tokens provide identity. Refresh tokens are single-use and have a documented 90-day lifetime. Refresh/revocation reference examples and discovery authentication examples still show client secrets, while the implementation guide supports public PKCE; the exact current public-client refresh/revoke behavior remains live-unverified, not a proven missing-secret defect. [OAuth endpoint reference](https://create.roblox.com/docs/cloud/auth/oauth2-reference)

Registration documentation permits localhost HTTP callbacks for local debugging and custom application schemes, requires registered redirect URLs and limits private apps to ten unique users pending review. This does not establish production approval of Zenith's present localhost callback. Registration, approved scopes/callback and the real consent/refresh/revocation flow remain acceptance gates. [App registration](https://create.roblox.com/docs/cloud/auth/oauth2-registration)

## Native authentication and destinations

The official Accounts index lists `GET auth.roblox.com/v1/client-assertion` as producing an assertion for auth-ticket generation, with `Cookie None` authentication labels and no OAuth label. No current authentication-ticket request contract was found in the reviewed Auth index; legacy official Swagger documentation could not be retrieved through the browsing tool. Neither fact proves endpoint removal. The listed external identity-provider native SSO routes also supply no documented bridge accepting arbitrary third-party Roblox Open Cloud tokens. No ticket request or redemption was attempted. [Accounts index](https://create.roblox.com/docs/cloud/reference/features/accounts), [Auth domain index](https://create.roblox.com/docs/cloud/reference/domains/auth)

Roblox supports manual website sign-in and Play through the registered player handler. Its web account switcher supports up to five accounts, without documenting a third-party native-account-selection API. Separate embedded browser storage or successful OAuth consent cannot be treated as native sign-in. [Install and Play](https://en.help.roblox.com/hc/en-us/articles/204473560-How-to-Install-and-Play-Roblox), [Account Switching](https://en.help.roblox.com/hc/en-us/articles/21037888001044-Account-Switching)

Destination links do not choose the authenticated account. In the deep-link reference, `userId` means the user to follow; place/job/private-server values describe the destination. That public deep-link process is now deprecated in favor of share links. A request or handler launch still cannot acknowledge account identity or arrival. [Deep links](https://create.roblox.com/docs/production/promotion/deeplinks), [Share links](https://create.roblox.com/docs/production/promotion/share-links)

## Simultaneous players and later statements

- **May 8, 2025:** Roblox said simultaneous player clients were unsupported and that it would investigate a development/testing solution. The narrower statement about the modified-client enforcement change did not promise compatibility or support. [Roblox response](https://devforum.roblox.com/t/an-update-on-automated-action-against-modified-clients/3640609/381)
- **April 6, 2026:** TatersPreci0us said there were no plans to support multiple clients on the same computer and that the unintended Store/web combination had been fixed. The April 9 reply asks about the earlier investigation; it adds no support commitment. [Dated thread](https://devforum.roblox.com/t/the-ability-to-run-2-roblox-clients-at-once/28334)
- **September 18, 2026:** the fall roadmap describes multiple client views alongside a server in one playtest, planned for late 2026. It does not demonstrate simultaneous signed-in production players. No later official reversal was found in this bounded review. [Fall roadmap](https://devforum.roblox.com/t/creator-roadmap-2026-fall-update/4880208)

## Capability gates retained

- **CanLinkIdentity:** documented API mechanism; Zenith registration and live flow unverified.
- **CanSelectSavedAccount:** no compatible native selection provider demonstrated.
- **CanLaunchNative:** ordinary manual website Play documented; automated account-specific handoff unavailable.
- **CanReconnectNative:** OAuth refresh is not native reconnect; supported handoff and fresh arrival proof missing.
- **CanVerifyArrival:** diagnostic log facts and resource-holder evidence do not authorize actions or establish authenticated arrival.
- **CanMultiInstance:** no supported same-machine production-player contract demonstrated; sixteen-client compatibility/capacity and performance remain unverified.

Future provider decisions must recheck current primary documentation and independently demonstrate selected identity, native handoff, destination and fresh arrival. Keep requested destinations, observed diagnostics and protected authorization separate.

## Bootstrapper discovery facts

The independent read-only inventory uses a small factual surface. Current Bloxstrap and Fishstrap application source identifies their current-user uninstall keys and InstallLocation, and Paths identifies executable/Versions names. Current Froststrap application source identifies the Froststrap uninstall key/installation location. These are nonexhaustive metadata hints, not verified startup arguments, publisher trust or permission to change the registered handler. No reference code was reused. [Bloxstrap application](https://raw.githubusercontent.com/bloxstraplabs/bloxstrap/main/Bloxstrap/App.xaml.cs), [Bloxstrap paths](https://raw.githubusercontent.com/bloxstraplabs/bloxstrap/main/Bloxstrap/Paths.cs), [Fishstrap application](https://raw.githubusercontent.com/fishstrap/fishstrap/main/Bloxstrap/App.xaml.cs), [Fishstrap paths](https://raw.githubusercontent.com/fishstrap/fishstrap/main/Bloxstrap/Paths.cs), [Froststrap application](https://raw.githubusercontent.com/Froststrap/Froststrap/main/Froststrap/App.axaml.cs).

Windows drive-type classification runs before filesystem/version queries to reject mapped remote, unknown and unavailable volumes. Resource size is queried before a bounded native version buffer is allocated; a readable version/product name remains an unverified file claim. Actual filesystem/native calls have no promised hard cancellation deadline. [GetDriveTypeW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdrivetypew), [GetFileVersionInfoSizeW](https://learn.microsoft.com/en-us/windows/win32/api/winver/nf-winver-getfileversioninfosizew), [VerQueryValueW](https://learn.microsoft.com/en-us/windows/win32/api/winver/nf-winver-verqueryvaluew).