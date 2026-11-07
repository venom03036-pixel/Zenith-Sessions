# Shared authoring capture — 0.47 verification

Reviewed November 6, 2026, local time. This is verified development work. **ReleaseReady=false; LiveTestReady=false.** No installed edition, user library, credentials, or real Roblox session was changed. The [design](AUTHORING-CAPTURE-DESIGN.md) and [requirements ledger](REQUIREMENTS-LEDGER.md) retain the scope and remaining acceptance gates.

## Verified build

- **4,006/4,006** manager tests from source and again against the exact staged assemblies; **350/350** affected manager cases twice.
- **758/758** Core regressions on the .NET 8 test target, **669/669** Personal editor/store/input checks, and **317/317** affected Personal checks twice.
- **42/42** isolated packaged checks twice (9.137 s and 7.823 s reported self-check time). All 28 payloads match the stage manifest; the manifest makes 29 files. The bounded filename/reparse/hash check is not a full security scan.
- **92 new controlled cases**: 30 preparer, 20 dialog, 24 editor, and 18 real in-memory OpenCV encoder cases. The broader selection also includes nine existing authoring cases outside the previous filter. Neither synthetic capture nor hidden dispatcher tests count as a real desktop test.

Final builds reported no compiler warnings or errors. Three shared assemblies from the separately built Personal output initially differed in bytes from the manager output. After copying the six exact tested manager assemblies into the isolated Personal test directory, all 669 and both 317-case runs passed again. Core and SDK hashes also match the dedicated Core test directory. The final source snapshot contains 54 entries, including the affected authoring code and its established input dependencies.

- Staged `Zenith.Sessions.App.dll`: `8553AF66C41664A7DBC5802882F63886883866311843DBE0F133CA8A96F4F851`.
- Tested `MacroSuite.App.dll`: `ED67B31914F572CCCB75EA99E69C51F3FADA73E83E7ECAF2FB53B0C245C24D1E`.
- Tested `MacroSuite.Infrastructure.dll`: `551E4801D47450C60139C496FCB479D7BEA4C649D0766CF5E8B99DDD3103B753`.
- Machine evidence: [sessions-manager-verification-0.47.json](../../artifacts/sessions-manager-verification-0.47.json), SHA-256 `20B68AAEE551AD9917A6B806EB3A847D6B0A69EE7EE2F60625AB65F2ECA666D2`.

## What changed and what was checked

Image source dialogs return intent. The editor uses its existing shared capture service for screen templates, exact pixels, Visual Wait, and F10. Bounded capture/import/encoding/validation runs off the UI thread. Screen selection must convert both corners to valid physical coordinates; a failed conversion cancels. A failed placement cleanup takes priority over an earlier ordinary placement error.

An accepted capture stays owned until its actual result and frame disposal finish. Stop fences publication without using early cancellation of a request task as proof of cleanup. The compatibility bridge pumps the existing dispatcher without another window; a controlled heartbeat ran during gated capture. An early frame exit retains the actual observer and worker. Managed modal cancellation closes the captured window, while a native file chooser may require dismissal. The bounded hover path joins its active request before taking the final exact clicked pixel. Failed sampling cannot create a fabricated color.

Controlled Add, conversion, Apply, control-flow, replacement and import cases retain embedded image bytes, including an encrypted save/reload. Exact document, step, and draft identity fence a stale result. Playback and recording image preflight commits the stored action before fresh producer admission and preserves the original target decision, including a deliberate legacy no-window target. F10 uses the same prepared bytes and physical region; the raw-frame compatibility helper retains caller ownership. Tests exercise the F10 helper and insertion, not a real MainWindow hotkey interaction.

The unchanged compiled memory-only probe decoded both PNG and JPEG as RGB `[7,91,203]` against verified 0.46 encoder binaries for input `[203,91,7]`. It decoded both as `[203,91,7]` against the 0.47 encoder. The raw probe's historical source label stays unchanged; the surrounding evidence records the actual 0.47 binary identity. No live desktop frame was used.

## Retained failures and limits

The initial Personal compile caught an ambiguous WPF `MouseButton`; the next compile caught a fixture's private member reference and two unobserved dispatcher operations. The alias, public draft-field assertion, and joined operations corrected those issues without loosening the test deadlines. Both build logs are retained. All executed final test runs passed. The Public-disabled development build again contains no companion host references, payloads, dependencies, or catalog properties. No Public release was installed or qualified.

Actual compositor freshness, capture exclusion, mixed-DPI selection, focus, and perceived UI speed remain unverified on a real desktop. This cycle does not add production execution IPC, per-client execution capture, or a Roblox background provider. Native account/PID association, verified joining/reconnect, independent background runs, live broadcasting, sixteen-client capacity, and comparison benchmarks remain open. Ordinary and group native-account launching stay disabled; explicit trials remain Unverified. No live-test invitation is issued yet.
