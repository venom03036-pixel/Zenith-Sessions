# Scoped localization migration

Owner decisions R-shell-16 and R-research-14 authorize English resources, Follow Windows, pseudo-locales and right-to-left infrastructure. English is the only shipped translation. This increment preserves the current layout, commands, theme tokens and navigation IDs; it replaces display strings in the sidebar, window/page title, Appearance's static labels/descriptions and Guide headings, and adds a language choice to Appearance.

Rule 20 migration: move these strings incrementally into `Localization/ShellStrings.resx`; use the inherited, window-local catalog instead of changing process-wide culture. Non-migrated pages continue in English. Existing schema-1 settings default to English, unknown choices fall back to English, and older builds ignore the optional language value. Removing the language bindings and preference hook rolls back this increment without changing vault, profile or macro formats.

Regressions to check: English captions and accessible names remain identical, navigation uses stable command IDs, language autosaves do not persist unrelated drafts, the chosen language survives a theme change/restart, placeholder sets match, and longer/RTL captions remain reachable. The completeness gate checks every migrated static XAML string and every shipped culture's keys and composite-format placeholders. Brand names, account names, player IDs, file paths and user-created content are not translation keys.

Remaining migration, explicit follow-up:

- Overview contents and its dynamic statuses.
- Accounts rows, inspector, dialogs, menus, validation and account-operation notices.
- Sessions, Join a game, server browsing, launch review/queue and launch/trial notices.
- Windows overview, previews, layout controls and their statuses.
- Automation, macro library/assignments, broadcasting and capability notices.
- Diagnostics, exports and error descriptions.
- Settings outside Appearance, theme preset names, choice labels supplied by core policies, settings search and integration status messages.
- Guide body paragraphs, footer, Lock overlay and remaining shell menus/dynamic notices.

Pseudo-locales are explicitly test modes, not translations. Follow Windows uses its display culture and direction, while unavailable catalogs fall back to English. Dynamic operational notices remain English until their individual services are migrated. No language provider, network request, worker, timer or new dependency is introduced. The catalog changes only when the user changes language; the immutable catalog view is inherited by visual children. Disconnected popup content must explicitly bind the placement target's catalog when it is migrated later.

Rule 18: bounded static resources and choice lists; ordinary resource lookup on binding changes; language saves use the existing serialized settings writer and join shutdown through the command's tracked execution. There is no credential or execution-path change. Native/live release readiness is unchanged.

Implementation and focused offline evidence: [localization verification](LOCALIZATION-VERIFICATION.md). The completeness gate is part of `ShellLocalizationTests` in the normal Sessions suite; it compares all `ShellStrings*.resx` files to English and checks composite-format placeholders and every migrated XAML reference. With only English shipped, the additional pseudo tests exercise expansion, rebinding and RTL without pretending that a translation exists.
