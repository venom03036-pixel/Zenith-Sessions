# Linking Find a game to real Roblox games — design

November 7, 2026. Requirements: CHAT-23 (name/direct input and joining), PROD-UI-17 (destination favorites), plus the
redesign spec ([UI-REDESIGN-SPEC.md](ui-redesign/UI-REDESIGN-SPEC.md), frames 05a and 05c). Approved in chat before
writing; this document is the reviewed record and must be approved again before an implementation plan is written.

## Goal

Join a game → Find a game already searches real public Roblox games (omni-search), accepts place IDs and
`roblox.com/games/…`, `/games/start?placeId=…&gameInstanceId=…` and `roblox://placeId=…` links, and shows a public
icon, gallery and server list. This cycle adds four ways to reach real games, matching Figma 05a/05c:

1. **Website-style result cards** — real 16:9 thumbnails, verified creator, playing count, like ratio, sort, Open on
   Roblox.com.
2. **The account's Roblox favorites** — the selected account's public favorites from its Roblox profile.
3. **Recently played, and joining from a running client** — games Zenith observed in client logs; Join same game /
   Join same server.
4. **Share links (signed in)** — `roblox.com/share?code=…&type=…` resolved for the selected account.

Out of scope: registering Zenith as the `roblox-player:` protocol handler (it would replace the system launcher and
conflict with Bloxstrap/Froststrap/Fishstrap), and launching into a private server (see §4.4).

## Facts checked live (anonymous, Nov 7 2026)

| Endpoint | Result |
|---|---|
| `GET apis.roblox.com/search-api/omni-search` | Game entries also carry `creatorHasVerifiedBadge`, `totalUpVotes`, `totalDownVotes`. |
| `GET thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds=…&countPerUniverse=1&defaults=true&size=384x216&format=Png&isCircular=false` | One 16:9 thumbnail per universe, `tr.rbxcdn.com` URLs, `state` per item. |
| `GET games.roblox.com/v2/users/{userId}/favorite/games?limit=25&sortOrder=Desc` | Public, no sign-in. `data[]` has `id` (universe), `name`, `rootPlace.id`, plus a cursor. |
| `GET games.roblox.com/v1/games?universeIds=…` | Batch names/creator/playing (already used for single lookups). |
| `POST apis.roblox.com/sharelinks/v1/resolve-link` | Answers 403 + `x-csrf-token` without a session: needs the signed-in cookie and the CSRF handshake. |

These are observed first-party behaviour, not promised stable schemas. Fixtures are saved sanitized (no account data).

## 1. Website-style result cards (05a)

### 1.1 Data

- `PublicRobloxGameSearchResult` gains trailing optional `bool? CreatorVerified`, `long? UpVotes`, `long? DownVotes`.
  The omni-search parser reads `creatorHasVerifiedBadge`, `totalUpVotes`, `totalDownVotes` (negative or wrong type →
  invalid response, as for existing fields). Existing positional callers are unaffected.
- New optional capability in Core (existing directory mocks keep compiling):

  ```csharp
  public enum PublicRobloxThumbnailState { Ready, Pending, Blocked, Unavailable }
  public sealed record PublicRobloxGameThumbnail(long UniverseId, Uri? ImageUri, PublicRobloxThumbnailState State,
      DateTimeOffset RetrievedUtc);
  public interface IPublicRobloxGameArtDirectory
  {
      // 1–12 distinct positive universe IDs; one request; one thumbnail per universe.
      Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxGameThumbnail>>> GetGameThumbnailsAsync(
          IReadOnlyList<long> universeIds, CancellationToken ct);
      Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameThumbnailAsync(
          PublicRobloxGameThumbnail thumbnail, CancellationToken ct);
  }
  ```

- `RobloxPublicDirectory` implements it with the existing transport: 2 shared request slots, 64 KiB JSON cap,
  `TryValidateAvatarUri` host allowlist, 2 MiB image cap, 64 MiB/512-entry encoded cache, rate-limit cooldown.
  Unknown/duplicate universe IDs in the response → invalid response. `Pending`/`Blocked` items have no URI.

### 1.2 Presentation

- `PublicGameSearchRow` gains `Art` (decoded `ImageSource?`), `HasArt`, `CreatorLine` ("by Fischling ✓" with the
  badge only when `CreatorVerified == true`), `PlayingLabel` ("85.2K playing"), `RatingLabel` ("92% liked", hidden
  when votes are unknown or zero).
- `DestinationSearchViewModel` shows cards **12 at a time** over the retained ≤50 results (Show more reveals the next
  12 locally; More results still fetches the next server page). Sort is local to loaded results: **Relevance**
  (server order), **Playing now**, **Rating**.
- A new `GameCardArtLoader` (App) owns one tracked task per visible set: one thumbnail batch request, then images
  fetched **one at a time** and decoded with the existing `PublicAccountPresentation.DecodeGalleryPng` limits. A new
  search, page, sort change, Lock, Panic, leaving Join a game or disposal cancels it, bumps the generation and
  clears decoded images. Results from an older generation are discarded. No retries, timers or polling.
- **Clicking a card uses it**: it sets the requested destination to that result's root place and loads the docked
  game panel, exactly as Use selected experience does today. It never launches or queues. The Use selected experience
  button is removed; keyboard Enter on a focused card does the same.
- Docked game panel gains **Open on Roblox.com** (shell-opens only `RobloxRequestedDestination.ToGamePageUri()`,
  i.e. `https://www.roblox.com/games/{placeId}`) and **Copy link** (same URI, IDs only).

### 1.3 Layout

Replace the `DestinationSearchResults` ListBox template with a 2-column card grid (`ItemsControl` + `UniformGrid`
Columns=2, ≤12 items, so no virtualization is needed). Card: 16:9 picture (placeholder gradient token while loading),
name, creator line, playing + rating, accent border and check when it is the current destination. Keep the
`DestinationSearchResults` x:Name and `AutomationName`s. Narrow widths (< 1100 px content) use one column.

## 2. The account's Roblox favorites

- New optional capability:

  ```csharp
  public sealed record PublicRobloxFavoriteGame(long UniverseId, long RootPlaceId, string Name);
  public interface IPublicRobloxFavoriteGamesDirectory
  {
      // Public profile favorites; at most 25, newest first. No sign-in, no cursor follow-up.
      Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxFavoriteGame>>> GetFavoriteGamesAsync(
          long userId, CancellationToken ct);
  }
  ```

- Account eligibility follows the headshot rule: the account selected in Accounts with `KnownUserId > 0` and a
  current `AuthorizationGeneration`. Otherwise the row says "Sign in to show this account's Roblox favorites".
- `RobloxFavoriteGamesViewModel` loads **once when Join a game opens** (one request) and on the refresh icon; leaving
  the page or changing the selected account cancels and clears it. Together with the recently played names (§3.1)
  these are the only automatic requests in this design: public, bounded, one each per page visit, and visible
  ("Loading favorites…").
- Favorites row: Zenith's saved favorites first (star chips, existing `FavoriteDestinationsViewModel`), then
  "On Roblox · {account}" chips (first 6, "+N more" opens the list). Clicking a chip sets the requested destination to
  that root place. The existing favorites table, rename and remove move behind a "Manage favorites" link.

## 3. Recently played, and joining from a running client

### 3.1 Recently played chips

- Source: `SessionEvidenceSnapshot.CurrentDestination` for observed clients plus `RecentClientHistoryEntry.LastKnownDestination`
  for ended ones. Deduplicate by place, newest `ObservedUtc` first, keep 6.
- Names: one batch `games.roblox.com/v1/games?universeIds=…` request for entries with a universe ID, through a new
  optional capability `IPublicRobloxGameBatchDirectory.LookupGamesAsync(IReadOnlyList<long> universeIds, CancellationToken ct)`
  (1–6 distinct IDs, returns `PublicRobloxGame` per universe with `RequestedPlaceId = RootPlaceId`), and the existing
  `LookupGameAsync(placeId)` for an entry without one. Unnamed entries show "Place {id}".
- Chips are labelled **Recently played** with the tooltip "From your Roblox clients' logs on this PC. Not verified."
  Clicking one sets the requested destination (place only, never the old server).
- Rebuilt when Join a game opens and once each time the shell publishes a new session/history snapshot while the page
  is open, after the history rows and log views are both updated (never once per history row); names are fetched only
  for places not already named this visit. No polling or timer is added.
- Panic and Lock clear the chips and keep Recently played stopped, even after Unlock, until Join a game is opened again
  or the selected account changes, the same actions that reload Roblox favorites and saved private servers. A desktop
  sample alone never restarts the name lookups. The favorites row then reads "Stopped. Choose Refresh to load
  favorites again."

### 3.2 Join same game / Join same server

- Sessions client table row menu (⋯) and the selected-client panel gain **Join same game** and **Join same server**,
  enabled only while that client has a `CurrentDestination`.
- Both set the requested destination (`PlaceId`, plus `JobId` for same server) through the existing
  `ServerBrowser` boundary and open Join a game at step 3 (Ready when you are). Nothing is queued until the user
  confirms there. Same server is labelled "Unverified · public servers only" because logs cannot tell a private or
  reserved server apart; Roblox decides.

## 4. Share links (signed in)

### 4.1 Parsing

- `RobloxDestinations` keeps classifying `roblox.com/share?code=…&type=…` as a share link, but instead of discarding
  the code it returns a `RobloxShareLink(ProtectedShareCode Code, RobloxShareLinkType Type)` alongside the
  unresolved result. `Type` ∈ `ExperienceDetails`, `Server`, `Other`. `ProtectedShareCode` holds the code in a
  pinned char array, redacts `ToString()`, is disposed after use and is never serialized, logged, put in a favorite
  or bundle, or shown again. `ro.blox.com` deferred links stay unresolved (no code to resolve).
- The text box is cleared as soon as a share link is recognised.

### 4.2 Resolution

- New optional capability implemented by `RobloxSessionLinkService` (which already owns reading a saved credential
  for one operation, as `InspectHandoffAsync` does):

  ```csharp
  public interface IRobloxShareLinkResolver
  {
      Task<RobloxShareLinkResolution> ResolveShareLinkAsync(Guid profileId, RobloxShareLink link, CancellationToken ct);
  }
  ```

- Runs only on the user's **Check link** click, for the account selected in Accounts. The credential is read from
  the vault for that one request and disposed afterwards. Request: `POST https://apis.roblox.com/sharelinks/v1/resolve-link`
  `{ "linkId": code, "linkType": type }`, cookie sent only to that exact host, no redirects, 64 KiB cap, one CSRF
  retry (403 with `x-csrf-token` → repeat once with the token), then stop. 401 → SignInRequired.
- Outcomes: `Experience(placeId, universeId)`, `PrivateServer(placeId, universeId, ownerUserId?, ProtectedPrivateServerCode)`,
  `Expired`, `Invalid`, `SignInRequired`, `Unavailable`, `Cancelled`. Nothing in the result or its `ToString()`
  contains the share code or the link code.

### 4.3 Using the result

- `Experience` → becomes the requested destination (place) and loads the game panel, like a pasted game link.
- `PrivateServer` → the 05c result row: game name/icon (public lookup), owner when known, "Link code …{last 4}".
  **Save as favorite** stores it in a new `PrivateServerStore`: DPAPI `CurrentUser` protected JSON in the writable
  data folder, at most 100 entries, per-record revision like favorite destinations, code bytes only inside the
  protected blob. Saved private servers list: name, game, last 4, Remove (confirmed).

### 4.4 Launching (not in this cycle)

The native handoff accepts only `RequestGame`/`RequestGameJob` today. Joining a private server needs
`RequestPrivateGame` with a link code through the launch authorization path, which is its own launcher change with
its own review (redesign migration step 8). Until then the private result's primary button is disabled with
"Joining private servers needs launcher support (coming next)". The saved code is kept so it works once that lands.

## 5. Figma

Update the Screens page (file `WLGgnKxi4wYzWlfzpZAnKr`), using existing Foundations components and variables only:

- **05a Find a game**: card anatomy from §1.3 (picture, verified badge, playing, rating, current-destination
  check), sort Relevance/Playing now/Rating, Favorites row with star chips and "On Roblox · account" chips,
  Recently played chips with the tooltip note, Open on Roblox.com and Copy link in the docked panel.
- **05c Browse servers · Private**: Check link result for a game share link and for a private server (last 4,
  owner, disabled Join with the launcher note), Saved private servers list.
- **03 Sessions**: row ⋯ menu with Join same game / Join same server.

## 6. Rule 18 checklist

| | |
|---|---|
| CPU | One JSON parse per batch (≤12 thumbnails, ≤25 favorites, ≤6 names); PNG decode off the UI thread with existing limits. |
| Memory | ≤12 decoded card images at a time, cleared on change; existing 64 MiB encoded cache; favorites ≤25; recents ≤6; ≤100 saved private servers. |
| Allocations | Not on capture/input hot paths. |
| Threads/timers | None added; each loader owns one tracked task. |
| Cancellation | Every request takes the owner's token; new search, account change, leave page, Lock, Panic and disposal cancel and join. |
| Errors | Explicit states per row/chip ("Picture unavailable", "Favorites unavailable", "Sign in required", "Link expired"); cooldown is shown, never retried automatically. |
| Testability | Fake `HttpMessageHandler`/`TimeProvider`, sanitized fixtures, view models tested without WPF windows. |
| Security | Public calls are anonymous. Only §4 uses a credential: one explicit click, one host, no logging. Share and link codes are protected types, never logged/serialized in plain text; the private store is DPAPI-protected. |
| Compatibility | Optional interfaces, trailing optional record members, new store file; no schema migration of existing data. |

## 7. Rule 20 migration

- **What changes:** Core (optional contracts, record fields, share-link parse result), Windows (directory/link
  service implementations, `PrivateServerStore`), App (view models, `MainWindow.xaml` step 1/step 2 private tab and
  Sessions row menu), tests, Figma. The launcher, queue, vault format and macro engine are untouched.
- **Strategy:** incremental, one independently revertible step each: (1) cards, (2) Roblox favorites, (3) recently
  played + join same game/server, (4) share-link parsing and resolution, (5) private server store and 05c UI.
- **Possible regressions:** search result selection semantics (click now uses), stale art after a new search, request
  slot contention between art and details, favorites appearing for the wrong account after a quick account switch,
  share codes leaking into logs/favorites, tests that pin the old ListBox template.
- **Detection:** new and updated unit tests per step; a log/serialization scan test asserting no share or link code
  appears; `dotnet build` and the Sessions test project after each step; rendered layout check at 960 × 640 and
  1320 × 840.
- **Rollback:** revert the step's files; no persisted data needs reversal except deleting `private-servers.dat`,
  which nothing else reads.

## 8. Tests

- Parser: omni-search extra fields (present, missing, wrong type, negative); thumbnail batch (ready, pending,
  blocked, unknown/duplicate IDs, bad host, oversize); favorites (normal, empty, cursor ignored, malformed, 25 cap);
  batch names; share-link URL parsing (types, missing code, extra params, `ro.blox.com`).
- Resolver: CSRF handshake once, no second retry, 401 → SignInRequired, cookie only to the exact host, redirects
  refused, expired/invalid bodies, cancellation; code never in exception messages or `ToString()`.
- View models: generation fencing (stale art/favorites dropped), cancel on Lock/Panic/leave/account change, click
  uses the destination without queueing, sort stability, chips dedupe/limit, Join same game/server enablement and
  navigation to step 3.
- Store: protect/unprotect round trip, cap, conflict, corrupt file preserved, code never in plain text on disk.
- Layout: update `DestinationSearchPresentationTests` and `JoinWorkspacePresentationTests` for the card grid; keep
  every existing x:Name.
