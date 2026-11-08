# Scoped localization verification

Date: 2026-11-08. Lane D7; requirements PROD-UI-01, PROD-UI-02 and CAP-14-01; owner-approved items R-shell-16 and R-research-14. This completes the scoped infrastructure, not every page's translation or a second shipped language. See [migration scope and remaining pages](LOCALIZATION-MIGRATION.md).

The previously untracked catalog, markup extension, language view-model partial and test helpers were retained and completed. English remains the default. The saved optional schema-1 choice supports English, Follow Windows and two clearly labeled pseudo-locales. Every migrated caption and accessible name reads one inherited catalog; RTL choices set the window's FlowDirection. Language saves use the existing serialized settings writer, preserve unrelated drafts, survive theme and window-placement saves, and join shutdown through the tracked command. No process culture, external service, credential, client or installation changes are involved.

The visual increment is the Appearance language choice. Existing navigation geometry, theme tokens, commands and command IDs are preserved. Settings search finds Language. The migration replaces the sidebar and window/page title, selected header labels, Appearance static labels/descriptions and Guide headings. Remaining dynamic notices, policy-provided choice labels and pages are explicitly listed in the migration document.

Validation (offline, synthetic profiles and monitor):

- `artifacts/sessions-tests-0.60-d7language/d7-language-verified.trx`: **282 passed**, covering catalog completeness, resource references, migrated literal checks, inherited live bindings and accessible names, language/schema persistence, navigation, rendered themes/contrast, pseudo-LTR/RTL caption bounds, settings search, shortcuts, idle lock, Panic and existing Discord settings.
- `artifacts/sessions-tests-0.60-d7language/d7-language-exit.trx`: **86 passed**, covering the final window-placement save merge, localization, manager placement and settings recovery. This run follows the final exit-save change.
- Tests compile with one pre-existing nullable warning at `DiscordAlertTests.cs:54`; production builds treat warnings as errors. No warning was suppressed.
- `Check-ZenithSessionsManager.ps1 -SkipTests -StageOnly -StageFolder sessions-manager-review`: Release build/publish and packaged self-check **43/43 passed**, with no build warnings or errors. Log: `artifacts-d7-package.log`; report: `artifacts/sessions-distribution-check/distribution-check.json`. The stage was not promoted or installed. App SHA-256: `04752FB1B47E6AB30993A5047D2D70BD71DB3E5214C1A6D4BAEABA280B6ACA49`; aggregate first-party identity: `3762100D24597B57A2131CAA7B378EE6146DEFAD5B7E0366401D03B1DD3D40F5`.

The initial runs exposed literal-only assertions in the existing Guide, shortcut and Panic tests and a standalone WPF fragment without its `local` namespace. Those fixtures now resolve the production English catalog and keep checking actual caption/command behavior. The new literal gate found three Appearance descriptions that still needed migration; they now use their existing catalog keys. A layout assertion initially counted a TextBlock's margin as clipped text; it was corrected to measure actual formatted text width. Both pseudo directions then fit the unchanged controls.

Full integrated suites and attended native trials remain separate release gates. No real account, Roblox client, paid cloud test or installer was used.
