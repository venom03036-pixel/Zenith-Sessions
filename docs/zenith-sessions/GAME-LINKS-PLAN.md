# Game links Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Link Join a game → Find a game to real Roblox games: picture cards, the account's Roblox favorites, recently played and join-from-client, and signed-in share links.

**Architecture:** Each Roblox data source is a new *optional* Core interface implemented by the existing anonymous `RobloxPublicDirectory` (or, for share links, by `RobloxSessionLinkService`, which already owns one-operation credential reads). Small App view models own one cancellable task each and are wired from `ShellDirectoryFeatures`; `MainWindow.xaml` binds to them. One deliberate deviation from spec §4.1: the existing `RobloxDestinationParser` stays unchanged (it keeps discarding codes); share links get a separate `RobloxShareLinkParser`, which gives the same behaviour with no risk to the existing parser's guarantees.

**Tech Stack:** C# / .NET 10 (SDK 10.0.401), WPF, System.Text.Json, `System.Security.Cryptography.ProtectedData` (already used by the vault), xUnit 2.7.

**Spec:** [GAME-LINKS-DESIGN.md](GAME-LINKS-DESIGN.md)

## Global Constraints

- Paths are relative to `working-copy/`. Test command prefix: `dotnet test tests/Zenith.Sessions.Tests --filter "FullyQualifiedName~<Class>"`.
- No new NuGet packages, timers, polling loops, threads or retries (AGENTS.md Rules 12, 14, 19).
- Public requests are anonymous and go through `RobloxPublicDirectory`'s existing slots/limits: 2 concurrent requests, 64 KiB JSON, 2 MiB image, `TryValidateAvatarUri` host allowlist, rate-limit cooldown.
- Every new interface is optional (`x as IFoo`); never add members to an existing interface.
- New record members are trailing and optional.
- Result states: a malformed/oversized response ("…Invalid" in test names) returns `PublicDirectoryState.Failure` via the existing `InvalidPublicResponseException` path; bad caller input returns `PublicDirectoryState.InvalidInput` and makes no request.
- Share codes and private link codes never appear in logs, exceptions, `ToString()`, favorites, bundles, settings or plain-text files (Rule 10 spirit).
- Keep every existing `x:Name`, binding path, command and event handler in `MainWindow.xaml`.
- Card batch size 12; favorites ≤ 25 shown as 6 chips; recently played ≤ 6; saved private servers ≤ 100.
- User-visible strings are copied verbatim from the spec where it gives them.
- This folder is not a git repository: Task 0 backs files up instead of committing; each task ends by running its tests.

## Review Focus

1. **Quick account switch while favorites load** — chips must belong to the account selected now, never the previous one (Task 6 test `AccountSwitchDropsTheOldAccountsFavorites`).
2. **New search while card pictures are still downloading** — no picture from the old search may appear on a new card (Task 3 test `NewSearchDropsPicturesFromThePreviousGeneration`).
3. **Share link pasted into the step-1 search box** — must go to share-link handling and be cleared, never sent to name search (Task 13 test `ShareLinkInFindBoxIsRoutedAndCleared`).
4. **Thumbnail batch saturating the two request slots** — game details must still load; art waits its turn (Task 3 test `ArtDownloadsOneAtATimeAndYieldsToDetails`).
5. **Running client with an ambiguous or stale log** — Join same game/server must stay disabled rather than use an old place (Task 9 test `StaleOrAmbiguousLogDisablesJoinSame`).

---

### Task 0: Baseline build, test run and backup

**Files:** none changed. Creates `../backups/game-links-2026-11-07/` (sibling of `working-copy`).

- [ ] **Step 1:** Copy `src/Zenith.Sessions.Core`, `src/Zenith.Sessions.Windows`, `src/Zenith.Sessions.App`, `tests/Zenith.Sessions.Tests` (excluding `bin/` and `obj/`) to the backup folder.
- [ ] **Step 2:** Run `dotnet build src/Zenith.Sessions.App`. The UI redesign was applied without compiling; fix compile errors only (no behaviour change) until it builds. Expected: `Build succeeded`.
- [ ] **Step 3:** Run `dotnet test tests/Zenith.Sessions.Tests`. Record the failing test names in `artifacts/game-links/baseline-failures.txt`. Redesign-pinned failures (UI-REDESIGN-SPEC "Tests that pin the current layout") are expected and are handled in Task 15; any other failure is investigated before Task 1.

### Task 1: Search results carry verified badge and votes

**Files:**
- Modify: `src/Zenith.Sessions.Core/PublicRobloxGameSearch.cs` (record `PublicRobloxGameSearchResult`)
- Modify: `src/Zenith.Sessions.Windows/RobloxPublicGameSearch.cs` (`ParseGameSearchPage`)
- Modify: `src/Zenith.Sessions.App/DestinationSearchViewModel.cs` (`PublicGameSearchRow`)
- Test: `tests/Zenith.Sessions.Tests/PublicGameSearchDirectoryTests.cs`, `tests/Zenith.Sessions.Tests/DestinationSearchViewModelTests.cs`

**Interfaces:**
- Produces: `PublicRobloxGameSearchResult(..., bool? CreatorVerified = null, long? UpVotes = null, long? DownVotes = null)`; `PublicGameSearchRow.IsCreatorVerified : bool`, `.CreatorLine : string`, `.PlayingLabel : string`, `.RatingLabel : string?`, `.HasRating : bool`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task VerifiedBadgeAndVotesAreReadWhenPresent()
{ // row JSON adds "creatorHasVerifiedBadge":true,"totalUpVotes":920,"totalDownVotes":80
  Assert.True(item.CreatorVerified); Assert.Equal(920, item.UpVotes); Assert.Equal(80, item.DownVotes); }
[Fact] public async Task MissingBadgeAndVotesStayUnknown()  // all three null
[Fact] public async Task NegativeVotesOrNonBooleanBadgeIsAnInvalidResponse() // State == PublicDirectoryState.Failure
[Fact] public void RowLabelsFormatPlayingAndRating()
{ var row = new PublicGameSearchRow(new(1, 2, "Fisch", 7, "Fischling", 85_200, false, true, 920, 80));
  Assert.Equal("by Fischling", row.CreatorLine); Assert.True(row.IsCreatorVerified);
  Assert.Equal("85.2K playing", row.PlayingLabel); Assert.Equal("92% liked", row.RatingLabel);
  Assert.Equal("120 playing", new PublicGameSearchRow(new(1, 2, "G", Playing: 120)).PlayingLabel);
  Assert.Equal("1.2M playing", new PublicGameSearchRow(new(1, 2, "G", Playing: 1_234_567)).PlayingLabel);
  Assert.Null(new PublicGameSearchRow(new(1, 2, "G", UpVotes: 0, DownVotes: 0)).RatingLabel); }
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~PublicGameSearchDirectoryTests|FullyQualifiedName~DestinationSearchViewModelTests"`. Expected: the new tests fail to compile.
- [ ] **Step 3:** Add the record members (validation: votes ≥ 0). Parse `creatorHasVerifiedBadge` with the existing `SearchOptionalBoolean`, votes with `SearchOptionalCount`. Row labels: "by {CreatorName}" (fallback to existing `Creator` text); playing: < 1,000 → "N playing", < 1,000,000 → one-decimal "K", else one-decimal "M", invariant culture, trailing ".0" dropped; rating = round(up × 100 / (up + down)) + "% liked", `null` when unknown or total 0.
- [ ] **Step 4:** Re-run. Expected: PASS, including all existing tests in both classes.

### Task 2: Thumbnail batch directory

**Files:**
- Create: `src/Zenith.Sessions.Core/PublicRobloxGameArt.cs`
- Create: `src/Zenith.Sessions.Windows/RobloxPublicGameArt.cs` (`partial class RobloxPublicDirectory : IPublicRobloxGameArtDirectory`)
- Modify: `src/Zenith.Sessions.Windows/RobloxPublicDirectory.cs` (add `GameThumbnail` to `PublicImageKind`)
- Test: `tests/Zenith.Sessions.Tests/PublicGameArtDirectoryTests.cs`

**Interfaces:**
- Produces (exactly as spec §1.1): `PublicRobloxThumbnailState { Ready, Pending, Blocked, Unavailable }`; `PublicRobloxGameThumbnail(long UniverseId, Uri? ImageUri, PublicRobloxThumbnailState State, DateTimeOffset RetrievedUtc)`; `IPublicRobloxGameArtDirectory.GetGameThumbnailsAsync(IReadOnlyList<long> universeIds, CancellationToken ct)` → `PublicDirectoryResult<IReadOnlyList<PublicRobloxGameThumbnail>>`; `.FetchGameThumbnailAsync(PublicRobloxGameThumbnail thumbnail, CancellationToken ct)` → `PublicDirectoryResult<EncodedPublicGameIcon>`; `PublicRobloxGameArtLimits.MaximumBatch = 12`.

- [ ] **Step 1: Write the failing tests** (own private `Handler`/`Clock`/`Json`/`Anonymous` helpers copied from `PublicGameSearchDirectoryTests`)

```csharp
[Fact] public async Task BatchUsesTheExactAnonymousEndpointOnce()
{ // GET https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds=11,22&countPerUniverse=1&defaults=true&size=384x216&format=Png&isCircular=false
  Assert.Equal(1, handler.Calls); Assert.Equal([11L, 22L], result.Value!.Select(t => t.UniverseId)); }
[Fact] public async Task CompletedPendingAndBlockedMapToStates() // "Completed"→Ready with Uri, "Pending"→Pending/null, "Blocked"→Blocked/null, other→Unavailable
[Fact] public async Task UnknownOrDuplicateUniverseInResponseIsInvalid()
[Fact] public async Task NonRobloxImageHostIsInvalid() // imageUrl https://example.com/x.png
[Fact] public async Task EmptyTooManyDuplicateOrNonPositiveIdsAreRejectedWithoutARequest() // 0 ids, 13 ids, [5,5], [0]
[Fact] public async Task FetchUsesTheSharedBoundedImageTransport() // second fetch of same Uri served from cache: handler.Calls unchanged
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~PublicGameArtDirectoryTests"`. Expected: compile failure.
- [ ] **Step 3:** Implement with `ReadJsonAsync` and `FetchPublicPngAsync(PublicImageKind.GameThumbnail, universeId, uri, …)`; map `EncodedPublicPng` to `EncodedPublicGameIcon` as `FetchGameIconAsync` does.
- [ ] **Step 4:** Re-run plus `PublicGameDirectoryTests` and `PublicGameGalleryTests`. Expected: PASS.

### Task 3: Card art loader, paging, sort and click-to-use

**Files:**
- Create: `src/Zenith.Sessions.App/GameCardArtLoader.cs`
- Modify: `src/Zenith.Sessions.App/DestinationSearchViewModel.cs`
- Test: `tests/Zenith.Sessions.Tests/GameCardArtLoaderTests.cs`, `tests/Zenith.Sessions.Tests/DestinationSearchViewModelTests.cs` (extend `SearchDirectory` fake with `IPublicRobloxGameArtDirectory`)

**Interfaces:**
- Consumes: Task 1 row labels; Task 2 `IPublicRobloxGameArtDirectory`.
- Produces: `PublicGameSearchRow.Art : ImageSource?`, `.HasArt : bool`, `.IsCurrent : bool` (row implements `INotifyPropertyChanged` via `Observable`); `GameCardArtLoader(IPublicRobloxGameArtDirectory? directory, Action<Exception> failed)` with `Task LoadAsync(IReadOnlyList<PublicGameSearchRow> rows, long generation)`, `void Cancel()`, `ValueTask DisposeAsync()`; on `DestinationSearchViewModel`: `VisibleResults : ObservableCollection<PublicGameSearchRow>`, `ShowMore : Command`, `CanShowMore : bool`, `SortOptions : IReadOnlyList<string>` = `["Relevance", "Playing now", "Rating"]`, `SelectedSort : string`, `UseCard : AsyncCommand` (parameter `PublicGameSearchRow`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public Task FirstTwelveResultsAreVisibleAndShowMoreRevealsTheNextTwelve() // 30 results → 12, ShowMore → 24, again → 30, CanShowMore false
[Fact] public Task SortIsLocalAndStable() // "Playing now" desc, ties keep server order; "Rating" desc, unknown ratings last; no extra Queries
[Fact] public Task ClickingACardUsesItWithoutQueueing() // UseCard(row) → Browser.Destination.PlaceId == row.Result.RootPlaceId; row.IsCurrent; no queue/launch call on the shell fake
[Fact] public Task NewSearchDropsPicturesFromThePreviousGeneration() // hold FetchGameThumbnailAsync, search again, release: old rows' Art stays null, new rows unaffected
[Fact] public Task ArtDownloadsOneAtATimeAndYieldsToDetails() // max concurrent Fetch calls observed == 1; LookupGameAsync for the used card completes while art is held
[Fact] public Task LockPanicLeaveAndDisposeCancelArtAndClearImages() // CancelWork(clearInput:true) → all Art null, loader token cancelled
[Fact] public Task MissingArtDirectoryLeavesPlaceholders() // directory without IPublicRobloxGameArtDirectory → HasArt false, no exception
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~GameCardArtLoaderTests|FullyQualifiedName~DestinationSearchViewModelTests"`. Expected: compile failure.
- [ ] **Step 3:** Implement. The loader owns one tracked `Task` + `CancellationTokenSource`; it requests the batch for the rows' universe IDs, then fetches each `Ready` thumbnail sequentially and decodes with `PublicAccountPresentation.DecodeGalleryPng` on `Task.Run`; it publishes `Art` on the UI context only if `generation` still equals the view model's current generation. `VisibleResults` is rebuilt (sorted, first 12 × n) on page load, ShowMore and sort change, each of which bumps the generation and calls `LoadAsync` for newly visible rows only. `UseCard` sets `SelectedResult` and runs the existing `UseSelectedAsync`. Remove nothing yet from `UseSelected` (XAML stops using it in Task 4).
- [ ] **Step 4:** Re-run. Expected: PASS, including existing `DestinationSearchViewModelTests` and `DestinationSearchIntegrationTests`.

### Task 4: Card grid, sort and Open on Roblox.com in the layout

**Files:**
- Modify: `src/Zenith.Sessions.App/MainWindow.xaml` (step 1, lines ~857–940), `src/Zenith.Sessions.App/MainWindow.xaml.cs`, `src/Zenith.Sessions.App/ShellClientWindowFeatures.cs` (next to `BuildRequestedDestinationLink`)
- Test: `tests/Zenith.Sessions.Tests/DestinationSearchPresentationTests.cs`, `tests/Zenith.Sessions.Tests/JoinWorkspacePresentationTests.cs`

**Interfaces:**
- Consumes: Task 3 `VisibleResults`, `UseCard`, `ShowMore`, `SortOptions`, `SelectedSort`, row labels/`Art`/`IsCurrent`.
- Produces: `ShellViewModel.BuildRequestedGamePageLink() : string?` (= `ServerBrowser.ReadyDestination?.ToGamePageUri().AbsoluteUri`); handlers `OpenGamePageClick`, `CopyGamePageLinkClick`.

- [ ] **Step 1: Write the failing tests** (they load the embedded `ProductionMainWindow.xaml` like the existing presentation tests)

```csharp
[Fact] public void SearchResultsRenderAsATwoColumnCardGrid() // DestinationSearchResults is ItemsControl, ItemsSource binds VisibleResults, panel UniformGrid Columns=2
[Fact] public void CardsShowPictureCreatorPlayingAndRating() // template binds Art, CreatorLine, IsCreatorVerified, PlayingLabel, RatingLabel, IsCurrent; Button Command UseCard
[Fact] public void GamePanelOffersOpenOnRobloxAndCopyLink() // buttons "Open on Roblox.com" Click=OpenGamePageClick, "Copy link" Click=CopyGamePageLinkClick
[Fact] public void GamePageLinkIsOnlyTheRobloxGamePage() // BuildRequestedGamePageLink() == "https://www.roblox.com/games/16732694052" after UsePreferredPlace(16732694052); null with no destination
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~DestinationSearchPresentationTests|FullyQualifiedName~JoinWorkspacePresentationTests"`. Expected: FAIL.
- [ ] **Step 3:** Replace the `DestinationSearchResults` `ListBox` with an `ItemsControl` (same `x:Name`, `AutomationProperties.Name`) using a `UniformGrid` (Columns 2; a `DataTrigger` on the page width sets 1 below 1100). Each card is a `Button` (Command `UseCard`, parameter the row) styled from `SurfaceCard`: 16:9 `Image` (`Stretch=UniformToFill`, clipped radius 8) over a `SurfaceBackground` placeholder; name; `CreatorLine` + verified glyph when `IsCreatorVerified`; people glyph + `PlayingLabel`; `RatingLabel` when `HasRating`; accent border + check when `IsCurrent`. Add the sort `ComboBox` and "{n} results" caption in the results header; "Show more" `QuietButton` bound to `ShowMore`/`CanShowMore`; keep More results and Cancel. Remove the "Use selected experience" button. In the docked panel add Open on Roblox.com and Copy link (Ghost/Quiet). Code-behind opens the link with `Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })` only when it starts with `https://www.roblox.com/games/`; Copy uses the existing clipboard pattern.
- [ ] **Step 4:** Re-run, then `dotnet build src/Zenith.Sessions.App`. Expected: PASS / `Build succeeded`.

### Task 5: Roblox favorites directory

**Files:**
- Create: `src/Zenith.Sessions.Core/PublicRobloxFavoriteGames.cs`, `src/Zenith.Sessions.Windows/RobloxPublicFavoriteGames.cs`
- Test: `tests/Zenith.Sessions.Tests/PublicFavoriteGamesDirectoryTests.cs`

**Interfaces:**
- Produces (spec §2): `PublicRobloxFavoriteGame(long UniverseId, long RootPlaceId, string Name)`; `IPublicRobloxFavoriteGamesDirectory.GetFavoriteGamesAsync(long userId, CancellationToken ct)` → `PublicDirectoryResult<IReadOnlyList<PublicRobloxFavoriteGame>>`; `PublicRobloxFavoriteGameLimits.MaximumItems = 25`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task RequestsTheExactPublicFavoritesEndpoint() // GET https://games.roblox.com/v2/users/156/favorite/games?limit=25&sortOrder=Desc, anonymous, 1 call
[Fact] public async Task ReadsIdRootPlaceAndNameAndIgnoresTheCursor() // data[].id, data[].rootPlace.id, data[].name; nextPageCursor present → still 1 call
[Fact] public async Task EmptyListIsSuccessWithNoItems()
[Fact] public async Task MissingRootPlaceDuplicateIdOrMoreThan25ItemsIsInvalid()
[Fact] public async Task NonPositiveUserIdIsRejectedWithoutARequest()
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~PublicFavoriteGamesDirectoryTests"`. Expected: compile failure.
- [ ] **Step 3:** Implement with `ReadJsonAsync`; names validated with `PublicRobloxGameSearchLimits.SafeText` and the search name limit.
- [ ] **Step 4:** Re-run. Expected: PASS.

### Task 6: Favorites chips (saved + On Roblox)

**Files:**
- Create: `src/Zenith.Sessions.App/RobloxFavoriteGamesViewModel.cs`
- Modify: `src/Zenith.Sessions.App/ShellDirectoryFeatures.cs` (construct, dispose, cancel with `DestinationSearch`), `src/Zenith.Sessions.App/ShellViewModel.cs` (`Tab` setter: load on `IsJoinGame`; `SelectedAccount` setter: `SetAccount`), `src/Zenith.Sessions.App/MainWindow.xaml` (favorites card in step 1)
- Test: `tests/Zenith.Sessions.Tests/RobloxFavoriteGamesViewModelTests.cs`, `tests/Zenith.Sessions.Tests/JoinWorkspacePresentationTests.cs`

**Interfaces:**
- Consumes: Task 5 directory; `ServerBrowserViewModel.UsePreferredPlace(long)`; `AccountRow.KnownUserId`, `.AuthorizationGeneration`, display name.
- Produces: `RobloxFavoriteGamesViewModel(IPublicRobloxFavoriteGamesDirectory? directory, ServerBrowserViewModel browser, Func<bool> canUse, Action<Exception>? failed = null)` with `SetAccount(Guid? profileId, long? userId, long? authorizationGeneration, string? accountName)`, `Task LoadAsync()`, `Refresh : AsyncCommand`, `Use : Command` (parameter `RobloxFavoriteChip`), `Chips : ObservableCollection<RobloxFavoriteChip>` (≤ 6), `MoreCount : int`, `Status : string`, `HeaderLabel : string` ("On Roblox · {account}"), `Cancel()`, `DisposeAsync()`; `RobloxFavoriteChip(PublicRobloxFavoriteGame Game)` with `Name`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public Task LoadsOncePerVisitForTheSelectedAccount() // SetAccount(id,156,3,"Main"); LoadAsync twice → 1 request; Chips.Count==6, MoreCount==19 for 25 items
[Fact] public Task AccountWithoutPlayerIdShowsSignInMessageAndMakesNoRequest() // Status == "Sign in to show this account's Roblox favorites"
[Fact] public Task AccountSwitchDropsTheOldAccountsFavorites() // hold request for A, SetAccount(B), release → Chips empty, B's load required
[Fact] public Task ChipUseSetsThePlaceWithoutLaunching() // Use(chip) → Browser.Destination.PlaceId == chip.Game.RootPlaceId
[Fact] public Task LeavingThePageCancelsAndClears() // Cancel() → token cancelled, Chips empty
[Fact] public void FavoritesRowShowsSavedAndRobloxChipsAndManageLink() // XAML: ItemsControl bound to DestinationFavorites.Favorites (star chips) and RobloxFavorites.Chips; "Manage favorites" Expander contains the existing favorites ListView
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~RobloxFavoriteGamesViewModelTests|FullyQualifiedName~JoinWorkspacePresentationTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement following `DestinationSearchViewModel`'s generation/stop pattern. Shell: expose `RobloxFavorites`; call `SetAccount` from the `SelectedAccount` setter and `LoadAsync` when `Tab` becomes `JoinGame`; `Cancel()` wherever `DestinationSearch.CancelWork` is called. XAML: replace the favorites table card with a chips row (saved favorites as star chips using `DestinationFavorites.Use`, then `RobloxFavorites.Chips`, "+{MoreCount} more" opening the full list in a Popup); move the existing ListView, name box and rename/remove buttons into an `Expander Header="Manage favorites"`.
- [ ] **Step 4:** Re-run, plus `FavoriteDestinationsViewModelTests`. Expected: PASS.

### Task 7: Batch game names

**Files:**
- Modify: `src/Zenith.Sessions.Core/PublicRobloxGames.cs` (new interface), `src/Zenith.Sessions.Windows/RobloxPublicGames.cs`
- Test: `tests/Zenith.Sessions.Tests/PublicGameDirectoryTests.cs`

**Interfaces:**
- Produces: `IPublicRobloxGameBatchDirectory.LookupGamesAsync(IReadOnlyList<long> universeIds, CancellationToken ct)` → `PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>>` (1–6 distinct positive IDs; each item has `RequestedPlaceId == RootPlaceId`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task BatchLookupUsesOneGamesRequest() // GET https://games.roblox.com/v1/games?universeIds=1,2 → 2 games, RequestedPlaceId == RootPlaceId
[Fact] public async Task MoreThanSixOrDuplicateIdsAreRejectedWithoutARequest()
[Fact] public async Task ResponseWithAnUnrequestedUniverseIsInvalid()
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~PublicGameDirectoryTests"`. Expected: FAIL.
- [ ] **Step 3:** Reuse the existing single-lookup game JSON parsing for each element.
- [ ] **Step 4:** Re-run. Expected: PASS.

### Task 8: Recently played chips

**Files:**
- Create: `src/Zenith.Sessions.App/RecentlyPlayedViewModel.cs`
- Modify: `src/Zenith.Sessions.App/ShellDirectoryFeatures.cs`, `src/Zenith.Sessions.App/ShellViewModel.cs` (rebuild sources when `Sessions` log views or `RecentClients.Rows` change while `IsJoinGame`), `src/Zenith.Sessions.App/MainWindow.xaml`
- Test: `tests/Zenith.Sessions.Tests/RecentlyPlayedViewModelTests.cs`

**Interfaces:**
- Consumes: Task 7 batch lookup; `IPublicRobloxGameDirectory.LookupGameAsync`; `RecentClientHistoryRow.Entry.LastKnownDestination`; `SessionRow.Log.ReportedPlaceId` (current, leased only).
- Produces: `RecentlyPlayedSource(long PlaceId, long? UniverseId, DateTimeOffset ObservedUtc)`; `RecentlyPlayedViewModel(object? directory, ServerBrowserViewModel browser, Func<bool> canUse, Action<Exception>? failed = null)` with `Task UpdateAsync(IReadOnlyList<RecentlyPlayedSource> sources)`, `Chips : ObservableCollection<RecentlyPlayedChip>` (≤ 6), `Use : Command`, `Cancel()`, `DisposeAsync()`; `RecentlyPlayedChip(long PlaceId)` with settable `Name` (default "Place {id}").

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public Task DedupesByPlaceNewestFirstAndKeepsSix() // 9 sources incl. duplicates → 6 chips ordered by ObservedUtc desc
[Fact] public Task NamesComeFromOneBatchPlusSingleLookupsForMissingUniverse() // batch called once with universe IDs; LookupGameAsync only for UniverseId==null
[Fact] public Task AlreadyNamedPlacesAreNotLookedUpAgainThisVisit()
[Fact] public Task ChipUsesThePlaceOnlyNeverTheOldServer() // Browser.Destination.JobId == null
[Fact] public Task UnnamedPlaceShowsPlaceId() // lookup Unknown → Name == "Place 123"
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~RecentlyPlayedViewModelTests"`. Expected: compile failure.
- [ ] **Step 3:** Implement; XAML adds "Recently played" heading and chips next to Favorites with `ToolTip="From your Roblox clients' logs on this PC. Not verified."`.
- [ ] **Step 4:** Re-run plus `RecentClientHistoryViewModelTests`. Expected: PASS.

### Task 9: Join same game / Join same server

**Files:**
- Modify: `src/Zenith.Sessions.App/SessionLogProjection.cs` (`BuildCurrentLink` gains `bool includeServer = true`), `src/Zenith.Sessions.App/ShellClientWindowFeatures.cs`, `src/Zenith.Sessions.App/MainWindow.xaml` (selected-client panel ~line 777 and the client table row `ContextMenu`), `src/Zenith.Sessions.App/MainWindow.xaml.cs`
- Test: `tests/Zenith.Sessions.Tests/ReportedDestinationHandoffTests.cs`

**Interfaces:**
- Produces: `ShellViewModel.JoinSameAsReported(SessionRow row, bool sameServer) : bool` — builds the click-time link, sets `ServerBrowser.LinkInput`, calls `ServerBrowser.ResolveInput()`, navigates to `JoinGame` and `ShowJoinStep` 3; returns false (and changes nothing) when the link is null. `ReviewReportedDestination` stays for existing callers. Handlers `JoinSameGameClick`, `JoinSameServerClick`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void JoinSameGameSetsPlaceOnlyAndOpensReadyStep() // Destination.JobId == null, IsJoinGame, IsJoinReadyStep, no queue entry
[Fact] public void JoinSameServerSetsPlaceAndJobAndOpensReadyStep() // Destination.JobId == reported job
[Fact] public void StaleOrAmbiguousLogDisablesJoinSame() // expired lease / ambiguous → returns false, destination unchanged, tab unchanged
[Fact] public void PanelAndRowMenuOfferBothActions() // XAML: buttons "Join same game"/"Join same server" bound IsEnabled Log.HasReportedDestination; same in row ContextMenu; "Copy reported server link" kept
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~ReportedDestinationHandoffTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement. Rename the "Review reported server" button's text to "Join same server" (keep `ReviewReportedServerClick` wired to it, now calling `JoinSameAsReported(row, true)`); add "Join same game". Tooltip on same server: "Unverified · public servers only".
- [ ] **Step 4:** Re-run. Expected: PASS.

### Task 10: Share-link parser and protected codes

**Files:**
- Create: `src/Zenith.Sessions.Core/RobloxShareLinks.cs`
- Test: `tests/Zenith.Sessions.Tests/ShareLinkParserTests.cs`

**Interfaces:**
- Produces: `RobloxShareLinkType { ExperienceDetails, Server, Other }`; `ProtectedShareCode : IDisposable` (holds chars, `Reveal(Action<ReadOnlySpan<char>>)`, `ToString()` = "[share code]", zeroes on dispose); `RobloxShareLink(ProtectedShareCode Code, RobloxShareLinkType Type) : IDisposable` with redacted `ToString()`; `RobloxShareLinkParser.TryParse(string? input, out RobloxShareLink? link) : bool`; `ProtectedPrivateServerCode` (same shape as `ProtectedShareCode`, plus `Last4 : string`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void ParsesExperienceAndServerShareLinks() // https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server → Server; type=ExperienceDetails → ExperienceDetails; roblox.com host accepted
[Fact] public void UnknownTypeIsOtherAndMissingCodeOrTypeFails()
[Fact] public void RejectsOtherHostsHttpExtraParametersAndBadCodes() // http://, roblox.com.evil.com, ro.blox.com, extra query key, code with non-alphanumerics or > 64 chars
[Fact] public void CodeNeverAppearsInToStringOrAfterDispose() // ToString() lacks the code; Reveal after Dispose throws ObjectDisposedException
[Fact] public void ExistingDestinationParserStillDiscardsShareCodes() // RobloxDestinationParser.Parse(link).ToString() lacks the code; Kind == UnresolvedShareLink
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~ShareLinkParserTests"`. Expected: compile failure.
- [ ] **Step 3:** Implement with the same exact-host, HTTPS, no-userinfo, port-443 checks as `RobloxDestinationParser`; path exactly `/share`; query keys exactly `code` and `type`; code 1–64 ASCII letters/digits.
- [ ] **Step 4:** Re-run plus `DestinationTests`. Expected: PASS.

### Task 11: Share-link resolution (signed in)

**Files:**
- Create: `src/Zenith.Sessions.Windows/RobloxShareLinkClient.cs`
- Modify: `src/Zenith.Sessions.Core/RobloxShareLinks.cs` (resolution types + `IRobloxShareLinkResolver`), `src/Zenith.Sessions.Windows/RobloxSessionLinkService.cs` (implements `IRobloxShareLinkResolver` using the same `Begin`/credential read as `InspectHandoffAsync`)
- Test: `tests/Zenith.Sessions.Tests/RobloxShareLinkClientTests.cs`, `tests/Zenith.Sessions.Tests/RobloxSessionLinkServiceTests.cs`

**Interfaces:**
- Consumes: Task 10 types; `RobloxSessionCredential`.
- Produces: `RobloxShareLinkOutcome { Experience, PrivateServer, Expired, Invalid, SignInRequired, Unavailable, Cancelled, Busy }`; `RobloxShareLinkResolution(RobloxShareLinkOutcome Outcome, long? PlaceId = null, long? UniverseId = null, long? OwnerUserId = null, ProtectedPrivateServerCode? PrivateServerCode = null) : IDisposable` with redacted `ToString()`; `IRobloxShareLinkResolver.ResolveShareLinkAsync(Guid profileId, RobloxShareLink link, CancellationToken ct)`; `RobloxShareLinkClient(HttpMessageHandler handler, TimeSpan? timeout = null)` with `Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link, RobloxSessionCredential credential, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task PostsToTheExactEndpointWithCookieOnlyThere() // POST https://apis.roblox.com/sharelinks/v1/resolve-link, body {"linkId":code,"linkType":"Server"}, Cookie header present, no redirect following
[Fact] public async Task CsrfHandshakeRetriesExactlyOnce() // 403+x-csrf-token → second call carries X-CSRF-TOKEN; second 403 → Unavailable; Calls == 2
[Fact] public async Task UnauthorizedMeansSignInRequired() // 401
[Fact] public async Task ExperienceResponseResolvesPlaceAndUniverse() // {"experienceDetailsInviteData":{"status":"Valid","placeId":5,"universeId":6}}
[Fact] public async Task ServerResponseResolvesProtectedLinkCode() // {"privateServerInviteData":{"status":"Valid","placeId":5,"universeId":6,"ownerUserId":7,"linkCode":"98765432109876543210"}} → Last4 == "3210"
[Fact] public async Task ExpiredInvalidAndUnknownShapesAreMapped() // status "Expired" → Expired, "Invalid" → Invalid, missing object / oversize / redirect → Unavailable
[Fact] public async Task CodesNeverAppearInResultsExceptionsOrToString() // assert code and linkCode absent from ToString() and every exception message
[Fact] public async Task ServiceWithoutSavedCredentialReturnsSignInRequired() // RobloxSessionLinkServiceTests
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~RobloxShareLinkClientTests|FullyQualifiedName~RobloxSessionLinkServiceTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement. `HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }` like `RobloxSessionClient`; 64 KiB body cap; `Cookie: .ROBLOSECURITY=…` header built inside `credential` reveal scope; dispose the credential and the link after the call. The response shape above is the observed web-client shape and is unverified until a signed-in check on your PC: unknown shapes return `Unavailable`, never guesses.
- [ ] **Step 4:** Re-run. Expected: PASS.

### Task 12: Saved private servers store

**Files:**
- Create: `src/Zenith.Sessions.Core/PrivateServers.cs`, `src/Zenith.Sessions.Windows/PrivateServerStore.cs`
- Test: `tests/Zenith.Sessions.Tests/PrivateServerStoreTests.cs`

**Interfaces:**
- Produces: `SavedPrivateServer(Guid Id, string Name, long PlaceId, long UniverseId, long? OwnerUserId, string Last4, long Revision)`; `IPrivateServerStore` with `Task<IReadOnlyList<SavedPrivateServer>> ReadAsync(CancellationToken ct)`, `Task<SavedPrivateServer> AddAsync(string name, long placeId, long universeId, long? ownerUserId, ProtectedPrivateServerCode code, CancellationToken ct)`, `Task DeleteAsync(Guid id, long revision, CancellationToken ct)`, `Task<ProtectedPrivateServerCode> ReadCodeAsync(Guid id, CancellationToken ct)`; `PrivateServerStore(string dataRoot)` writing `private-servers.dat`; `PrivateServerStoreCapacityException` at 100.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task RoundTripsMetadataAndCode() // Add then ReadCodeAsync reveals the same code; ReadAsync shows Last4 only
[Fact] public async Task FileNeverContainsTheCodeInPlainText() // raw bytes of private-servers.dat lack the code (UTF-8 and UTF-16)
[Fact] public async Task CapacityIsOneHundred()
[Fact] public async Task StaleRevisionDeleteIsAConflict()
[Fact] public async Task CorruptFileIsPreservedAndReported() // garbage file → read throws storage exception, file bytes unchanged
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~PrivateServerStoreTests"`. Expected: compile failure.
- [ ] **Step 3:** Implement like `FavoriteDestinationsStore` (atomic temp-file replace, per-record revision) but the whole JSON payload is `ProtectedData.Protect(..., entropy UTF-8 "Zenith.Sessions.PrivateServers.v1", DataProtectionScope.CurrentUser)`.
- [ ] **Step 4:** Re-run. Expected: PASS.

### Task 13: Share-link and private-server presentation (05c)

**Files:**
- Create: `src/Zenith.Sessions.App/ShareLinkViewModel.cs`
- Modify: `src/Zenith.Sessions.App/ShellDirectoryFeatures.cs`, `src/Zenith.Sessions.App/DestinationSearchViewModel.cs` (`SearchOrUseAsync` routes share links), `src/Zenith.Sessions.App/MainWindow.xaml` (step 2 private tab ~lines 960–980; the private-link `TextBox` binds `ShareLink.LinkInput`)
- Test: `tests/Zenith.Sessions.Tests/ShareLinkViewModelTests.cs`, `tests/Zenith.Sessions.Tests/DestinationSearchViewModelTests.cs`

**Interfaces:**
- Consumes: Tasks 10–12; `ServerBrowserViewModel.UsePreferredPlace`; selected account profile id.
- Produces: `ShareLinkViewModel(IRobloxShareLinkResolver? resolver, IPrivateServerStore? store, ServerBrowserViewModel browser, Func<Guid?> selectedProfile, Func<bool> canUse, Action<Exception>? failed = null)` with `LinkInput : string`, `Check : AsyncCommand`, `Task CheckAsync(RobloxShareLink link)`, `ResultTitle`, `ResultDetail`, `HasPrivateResult`, `SaveAsFavorite : AsyncCommand`, `SaveName : string`, `Saved : ObservableCollection<SavedPrivateServer>`, `Remove : AsyncCommand`, `Status`, `Cancel()`, `DisposeAsync()`; `DestinationSearchViewModel` gains optional constructor parameter `Func<RobloxShareLink, Task>? shareLinkHandler = null`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public Task ShareLinkInFindBoxIsRoutedAndCleared() // SearchOrUse with a share link → handler called once, Browser.LinkInput == "", no SearchGamesAsync query
[Fact] public Task CheckClearsTheInputBeforeTheRequest() // LinkInput == "" while resolver is held
[Fact] public Task ExperienceResultBecomesTheRequestedPlace()
[Fact] public Task PrivateResultShowsLast4AndDisabledJoin() // ResultDetail contains "Link code …3210"; Status/tooltip "Joining private servers needs launcher support (coming next)"
[Fact] public Task NoSelectedAccountAsksToSignIn() // resolver not called
[Fact] public Task SaveAsFavoriteStoresAndListsIt() // Saved.Count == 1, Last4 shown
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~ShareLinkViewModelTests|FullyQualifiedName~DestinationSearchViewModelTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement; XAML private tab: link box + Check link, result row (icon via existing `GameDetails` lookup by place), Save as favorite with name box, Saved private servers list with confirmed Remove, caption "Anyone with this link can join, so treat it like a password."
- [ ] **Step 4:** Re-run, then `dotnet build src/Zenith.Sessions.App`. Expected: PASS / `Build succeeded`.

### Task 14: Figma frames

**Files:** Figma file `WLGgnKxi4wYzWlfzpZAnKr`, page Screens. Load `figma:figma-use` before any `use_figma` call.

- [ ] **Step 1:** 05a: cards with picture, verified glyph, playing, "92% liked", current-destination check; sort Relevance/Playing now/Rating; Favorites row with star chips + "On Roblox · Main" chips + "+19 more"; Recently played chips; Open on Roblox.com and Copy link in the docked panel; "Manage favorites" link. Foundations components and variables only.
- [ ] **Step 2:** 05c: game share-link result, private-server result (owner, "Link code …3210", disabled Join with the launcher note), Saved private servers list.
- [ ] **Step 3:** 03 Sessions: row ⋯ menu and panel with Join same game / Join same server.
- [ ] **Step 4:** Screenshot each changed frame and compare with the built app's layout tests; fix mismatches in Figma (not code).

### Task 15: Full verification and records

**Files:** tests named in UI-REDESIGN-SPEC "Tests that pin the current layout"; `docs/zenith-sessions/REQUIREMENTS-LEDGER.md` (CHAT-23, PROD-UI-17); `docs/zenith-sessions/ui-redesign/UI-REDESIGN-SPEC.md` (Status).

- [ ] **Step 1:** `dotnet build src/Zenith.Sessions.App -c Release`. Expected: `Build succeeded`, 0 warnings added by this work.
- [ ] **Step 2:** `dotnet test tests/Zenith.Sessions.Tests`. For each failure from `baseline-failures.txt`: update the test only where the redesign deliberately changed the visual contract; otherwise fix the code. Expected: all tests pass.
- [ ] **Step 3:** Render check at 960 × 640 and 1320 × 840 in two themes via the existing packaged layout check (`PackagedLayoutCheck`); save screenshots under `artifacts/game-links/`.
- [ ] **Step 4:** Update the ledger entries and the redesign spec status with exact evidence: tests run, what is synthetic versus live, and the remaining gap (private-server launching, live signed-in share-link check).
