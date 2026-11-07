# Zenith Sessions — UI redesign spec

Figma: https://www.figma.com/design/WLGgnKxi4wYzWlfzpZAnKr (page **Screens**, components on **Foundations**)
Style reference: `RedesignStyles.xaml` (next to this file). The styles actually used by the app now live in `src/Zenith.Sessions.App/Themes/SessionsControls.xaml`.

## Status (2026-11-07)

Applied in one pass at the user's request, **not yet compiled or tested** (the session had no .NET SDK):

- `Themes/SessionsControls.xaml`: new keys (`SecondaryButton`, `IconButton`, `Glyph`, `SectionHeading`, `FieldLabel`, `MonoText`, `KeyText`, `ValueText`, `SurfaceCard`, `Divider`, `JoiningBanner`, `KeyCap`, `TableHeader`, `TableHeaderText`, `StepButton`); every unstyled Button now uses `SecondaryButton`; PageTitle 20, MetricValue 24, chips radius 6 with no outline, warning banner without the bright outline.
- `MainWindow.xaml`: new title bar, sidebar, footer (queue merged into the status bar, opens upward), Overview, Accounts (+ docked details, centred headshots), Sessions header/toolbar, Windows header/tiles, Join a game as three steps with a Private server tab, Automation and Diagnostics headers, Settings/Guide headers. All `x:Name`s, bindings, commands and event handlers kept.
- `ShellJoinFlowFeatures.cs` (new): `JoinStep`, `ShowJoinStep`, `ShowServerTab`, `IsPrivateServerTab`; `ShellViewModel.Tab` resets to step 1 when Join a game opens.
- `SidebarTemplateTests`: nav row minimum height 44 → 36.

**Update (2026-11-07, later the same day):** built and tested on Windows with the .NET 10.0.401 SDK. The applied XAML compiled unchanged (Release build, 0 warnings). The Sessions suite went from 37 pre-existing failures to 13: the 24 layout tests that pinned the old Join/Search/Accounts/native-trial surfaces were updated to the stepped page and the merged `SessionsControls.xaml` styles (`DestinationSearchPresentationTests`, `JoinWorkspacePresentationTests`, `AccountTemplateTests`, `NativeTrialPresentationTests`); the remaining 13 failures predate the redesign (5 out-of-process worker tests that never reach readiness here, 2 `SessionLogProjectionTests` on the Core evidence registry's identity-conflict/disconnect replay) and are untouched. The packaged `--self-check` passes 42/42. The game-links work described in [GAME-LINKS-DESIGN.md](../GAME-LINKS-DESIGN.md) (picture cards, Roblox favorites, recently played, join from a running client, share links and saved private servers) is implemented on top of this redesign; Figma frames 05a, 05c and 03 were updated to match. Rendered review snapshots (960 × 640 and 1320 × 840, graphite-mint and midnight) are under `artifacts/game-links/`.

## Principles

1. **One primary action per screen.** Everything else is Secondary (`QuietButton`), Ghost or an icon button.
2. **No disclaimer sentences in the layout.** A row shows a short status ("Responding", "Unverified", "Stale · 16s"); the full explanation moves to `ToolTip`. Keep at most one plain caption per section.
3. **Tables, not stacked boxes.** Rows 44–52 px with a bottom divider; bulk actions appear in a selection bar only when rows are checked.
4. **Docked right panel** (360 px, full height, left border) for details: Account details, game detail, pinned server.
5. **Tokens only.** Every colour is an existing `DynamicResource` key from `ThemeCatalog`, so all six themes keep working. The Figma variables have the same names.
6. **Type:** Segoe UI Variable Text (Figma shows Inter as a stand-in). Title 20 SemiBold · Section 15 SemiBold · Body 13 · Caption 12 · Mono = Cascadia Mono 12 for PIDs, IDs and timestamps.

## Shell

| Area | Change |
|---|---|
| Title bar | 44 px (was 56). Panic stop stays a filled Danger button, compact, with an `F12` key cap beside it (Rule 11: always visible). Lock and More become icon buttons. |
| Sidebar | 200 px. Nav rows 34 px, filled active state with accent icon; no left rail. Counts only on Accounts and Sessions. "Diagnostics active…" moves to the Diagnostics page. |
| Footer | Launch-queue bar and status bar merge into one 32 px bar: queue state dot + counts on the left; "Settings saved", **Open queue**, **Resume queue** on the right. Open queue expands the queue panel upward over the page. |

## Screens

| Figma frame | Notes |
|---|---|
| 01 Overview | Title "Overview" + "Updated just now". One counts strip (3 cells with dividers). Two columns: **System** (free memory, committed, CPU — value, sparkline, meter) and **Running clients** (top 5 rows, "Open Sessions"). Recent activity as time + text rows. Capacity details become a Ghost link instead of an inline expander. |
| 02 Accounts | Toolbar: search, group, sort, view options. Table: checkbox · avatar + name/@username (star for favourite) · group · session (status + game) · ⋯. Selection bar: Launch · Run macro · Add to group · More. **Docked Account details** (from the user's concept): header, 56 px avatar, name, @username, favourite star, group + status pills; Session (game, server, connected, client, **Focus client**); Assigned macro (status pill, name, cycle, Pause / Stop); Group / Favorite / Sign-in; Notes; View details. |
| 03 Sessions | Segmented Table / Previews / History. Toolbar: Minimize, Restore, Window size, CPU priority (Ghost) · monitor picker · **Tile N clients**. Client table + selected-client panel with facts and 2-minute sparklines. |
| 04 Windows | 4-column tile grid, name + PID + age; stale/minimised tiles dimmed with a warning status; selected tile has an accent border; Focus client in a bottom bar. |
| 05a Find a game | Large search, Favorites / Recently played chips, 2×2 result cards. Docked game panel: hero, title, playing now, place ID, "Joining with" banner, **Quick join** + server choice, **Browse servers**, Favorite. |
| 05b Browse servers | Back to game · Refresh. Joining banner. Tabs **Public servers / Private server**. Sort, player range, More filters; "Room for all N accounts", "Hide full servers". Server table: ID + region, players with meter, free slots, checked, Join. Docked **Pinned for this batch** → **Review launch**. |
| 05c Browse servers · Private | Paste private server link → **Check link** → result row → optional "Save as favorite". Saved private servers list. Note: "Anyone with this link can join, so treat it like a password." |
| 05d Ready when you are | Three cards: **Who is launching** (per-account outcome, excluded accounts in red with a fix action), **Where they're joining**, **What happens afterward** (Launch only / Launch and run + macro + start condition). Warning band, then Launch only · N and **Launch and run · N**. |
| 06 Automation | One warning notice while playback isn't wired. Saved macros table + Assign panel (Account / Group / Bundle) with current assignments. |
| 07 Diagnostics | Filter + All / Warnings / Errors; logging state on the right. Event table: time (mono) · level dot · event · area. Copy / Open logs folder / Save report… in the header. |
| 08 Settings | Section index on the left. Label-left / control-right rows. Theme swatches. Sticky save bar with unsaved-change count. |
| 09 Guide | Contents list + one 640 px reading column; numbered steps; shortcut table. |

## Profile pictures

Implemented in `MainWindow.xaml` (rows 32 px, compact rows 28 px, details 56 px): the headshot is an `Image` with
`Stretch=UniformToFill`, centred, inside a `Grid` clipped by a rounded `RectangleGeometry`. Every Roblox headshot therefore fills
the square, stays centred and gets rounded corners at any size. The existing person-outline placeholder shows until `Avatar` loads
(`HasAvatar`). Data: `Avatar` / `HasAvatar` on `AccountRow` (`ShellDirectoryFeatures`, 150×150 headshots).
Optional later: initials instead of the outline (needs a string `Initials` on `AccountRow`; see `AccountAvatar` in `RedesignStyles.xaml`).

## New behaviour the design needs (not just styling)

| Feature | Current code | Needed |
|---|---|---|
| Private server join (05c) | `RobloxDestinations` returns `UnresolvedPrivateServer` and discards the code ("needs an authorized Roblox flow"). | An authorised flow that resolves a share link / access code, a view-model property for the pasted link, a Check-link command, and storage for saved private servers. Treat the code as a secret: never log it or show it in full after entry. |
| Launch and run (05d), Pause / Stop in Account details | Companion macro backend exists (0.51); UI wiring is pending. | View-model commands bound to `CompanionMacroOperations` and per-account run state. |
| Account details "Session" block | Process observation is separate from verified identity. | Show only verified facts; fall back to "Not running" or "Unverified" with the explanation in a tooltip. |
| Initials fallback | — | `Initials` on account rows. |

## Tests that pin the current layout and will need updating

`SidebarTemplateTests` (nav height ≥ 44 and a visible `Rail`), `OverviewWorkspacePresentationTests` (`OverviewNavigation` WrapPanel button labels, 3 sparklines at height 40, `OverviewDetails` expander), `AccountTemplateTests`, `AccountsWorkspaceTests`, `JoinWorkspacePresentationTests`, `DestinationSearchPresentationTests`, `NativeLaunchDiagnosticLayoutTests`, `SettingsSearchIntegrationTests`, `GroupManagerThemeTests`. Keep every `x:Name`, binding path and command when restructuring, so tests change only where the visual contract deliberately changed.

## Migration plan (AGENTS.md Rule 20)

- **What changes:** XAML view layer only (`App.xaml` merge, `MainWindow.xaml`, `SessionsControls.xaml`), plus the small view-model additions above. Engine, Core and Windows layers are untouched.
- **Strategy:** incremental, one screen per commit: 1) merge `RedesignStyles.xaml` (restyles every page with no layout change); 2) shell (title bar, sidebar, footer); 3) Accounts + avatar; 4) Overview; 5) Sessions + Windows; 6) Join flow (05a–05d, public servers first); 7) Automation, Diagnostics, Settings, Guide; 8) private servers once the authorised flow exists.
- **Possible regressions:** keyboard focus order, Ctrl+1–7 and F12 bindings, narrow-width (960 × 640) wrapping, high-DPI clipping, theme contrast.
- **Detection:** run the Sessions test project after each step; capture the existing layout screenshots (960 × 640 and 1320 × 840, all themes) and compare with the Figma frames.
- **Rollback:** each step is one commit; removing the `RedesignStyles.xaml` merge restores the previous look in one line.
