# Account group management and dialog repairs — 0.29

November 6, 2026. Windows Personal development preview only. The preceding goal turn advanced direct verified account setup. This cycle implements library-wide group rename/removal and repairs defects found in the actual interface. Full utility parity, native account launch/arrival, background automation, reconnect and sixteen-client acceptance remain unfinished. Installed editions and real accounts/sessions were preserved.

## Final executed evidence

The final source suite passed **2,633/2,633**. The complete suite passed against the **exact staged App, Core and Windows assemblies**; the affected **105 cases passed twice more**. Final forced builds reported zero warnings/errors. The final packaged self-check passed **33/33 twice** on .NET 10.0.12 and SQLite 3.53.4.

The [verification record](../../artifacts/sessions-manager-verification-0.29.json) pins all 25 manifest entries plus the manifest, with matching lengths/hashes, no extra files and no matches against its recorded private/test filename patterns. This is a bounded filename audit, not a claim about all binary contents.

- App: `F5EC6EA069B892AA9F7EAF682A6B13B5DC73B70F11581F0C63D76E08A6C26B2C`.
- Core: `BBE4FB7849B839BABB0D1EF9172AE263393C79DEDE25187138377090F0179E01`.
- Windows: `B75F3B042C42AAB245B1DBBD8F8094B2069184B0F6C73157A82046B9253615E1`.
- Manifest: `D3692DB0888CF8CD927CB2526704B872B5D3C99AEE0965FA2B6D347F6A626C60`.

## Library-wide membership changes

**Manage groups…** now exposes Rename and Remove. An explicit review reports matching accounts across the entire library, including other pages/filters, plus the number using the source as their primary group. Removal changes memberships and requires a different fallback for primary accounts; it does not delete profiles, credentials or clients. Renaming to another existing name requires explicit merge approval and another review. Case-only renames are supported. Editing review inputs invalidates Apply.

`IAccountGroupLifecycle` is additive. Preview streams bounded validated group metadata in a SQLite read transaction and hashes the exact change, ordered affected IDs/revisions/raw group representation, counts and destination-existence bit. Apply reserves the SQLite writer, recomputes the complete preview and rejects any mismatch before writing. This also detects a newly introduced target group before an unintended merge. Actual updates use fixed 128-row keyset pages in the same transaction; only changed group fields, revision and update time change. No-op rows retain revision/time. There is no global 100-account mutation limit or full-library materialized recipient list.

The existing bounded Shell profile-operation owner serializes the work. Repository operations run off the dispatcher; filter, collection and feedback updates resume on it. Caller cancellation now covers waiting for the profile lane. Stop/Panic/Lock fence active and queued group operations. Shutdown joins actual owners. A committed database change remains authoritative if refresh is cancelled or fails; no compensating account deletion or second write occurs. Current source-group filters follow rename, removal returns that filter to All, and later unrelated selection/filter choices remain intact.

Controlled coverage includes 150 affected accounts across UI pages, exact Unicode/case/additional memberships, explicit merge, primary fallback, stale previews/foreign writers/new members, forged counts, revision exhaustion, malformed bounded data, actual later-row SQLite trigger rollback, cancellation, queued ownership and interrupted refresh. A synthetic protected grant, its metadata and ciphertext hash remain unchanged through removal. This is real owned SQLite/same-user DPAPI fixture evidence, not a real account test.

The packaged case performs rename, rejects the consumed stale review, removes the resulting membership, verifies unrelated profile/cleanup state and preserves the existing fixture cleanup contract.

## Actual UI defects and corrections

The first 0.29 source/exact-binary suites were green, but the interactive check exposed defects:

1. **Blank editable group names.** The shared base ComboBox template had only a selection presenter and no `PART_EditableTextBox`. Accessibility exposed the bound value while the screen showed a blank field. A companion-only `SessionsEditableComboBox` now supplies the actual editor/content host, themed arrow/popup, focus/disabled behavior and recycling suggestions. Both group dialogs use it. The shared host theme and installed Windows editions were not changed. Nine controlled cases cover actual glyph rendering across five presets, binding, editing, selection and accessibility.
2. **Closing the dialog terminated the preview.** A synchronously completed disposal caused `Close` to re-enter WPF's current Closing callback. The owned application's .NET Runtime event 1026 confirms `VerifyNotClosing` → `AccountGroupManagerDialog.JoinOnClosing`. The handler now joins work, unconditionally yields to the dispatcher and only then requests the final close; the redundant Close-button handler was removed. Two controlled callback regressions and actual subsequent modal closures passed.
3. **Successful rename cleared the picker.** Rebinding suggestions removed the old selection. The completed operation now retains the resulting destination as the source for the next edit, while clearing the previous review. A production-dialog binding regression and the final interactive case-only rename/restore verify the visible value remains populated.

The optional profile dialog now has a fixed heading and Save/Cancel footer, with only its fields scrolling. Long validation text has a separate bounded viewport. Draft validation, accessible error feedback, initial focus and pending-save close guards remain. Eight controlled cases exercise short forms, real overflow, errors and pending-save failure without showing a native window.

The [limited interactive record](../../artifacts/sessions-ui-verification-0.29.json) distinguishes build identities. On the final App hash, the 1320 × 840 sample manager opened the 548 × 584 group dialog, reviewed/applied eight primary memberships for a case-only rename, retained the new visible value, restored the original spelling, closed the dialog to a still-running 16-profile page and exited normally. The preceding same-version `0CF9657D…` App also performed an ordinary rename/restore and inspected the unchanged profile-dialog implementation at 528 × 724 and 478 × 420. Its Save/Cancel remained visible while the field scroller moved to the notes section; Cancel preserved all 16 accounts. That preceding profile inspection is not mislabeled as final-binary interactive evidence. The final build retains the same profile implementation and controlled layout cases.

These checks used isolated sample metadata and read-only desktop resource observations. No sign-in/browser, Roblox input/focus/close, capture picker, real launch or installed edition was operated. Screenshots were inspected inline through Computer Use. This is not all-page/theme/DPI/monitor, keyboard, latency, soak or large-library visual acceptance.

## Retained failures and limits

- Two initial oversized stored-NUL cases lacked native stored-byte preconditions. Raw-byte insertion plus exact native byte length/NUL position/full-blob assertions now establish the intended invalid data before strict rejection and unchanged-byte assertions. Production bounds were not weakened. Small below-limit raw stored-NUL cases were not separately executed; exact dependency source review preserves the full byte span, and no truncation defect was established.
- A first exact-binary resource-rendering test hit a pack-URI initialization-order failure. The test fixture now uses `Application.LoadComponent`, without constructing another global Application or weakening glyph assertions. Production source did not change for that correction.
- One complete exact-binary run returned `BudgetExceeded` from the existing two-second owned Restart Manager observation instead of a positive holder result. The strict isolated positive and subsequent complete run passed without source, budget or assertion changes. The cause is unproven; native observation timing remains environment-sensitive. Exceeding the budget continues to mean unknown diagnostic evidence, never account/writer authority.

All corresponding reports and the original UI crash event remain retained. Green checks do not erase these limits.

## Compatibility and remaining work

No database schema, protected credential format, queue format 6, macro document/SDK, installed host or production dependency changed. This is an additive API and UI change. Existing string memberships remain readable by earlier builds; rollback uses preserved libraries, not deletion or automatic migration. Rule 18 review covers bounded memory/pages/queues, no new polling workers, cancellation/joining, transactional failure, protected-data separation and exact-binary checks. No measured performance superiority or capacity improvement is claimed.

Independent empty groups, group colors/order, complete custom appearance/mini mode, native account/process/arrival and eligible multi-client integration, background macros/broadcast, correct-account reconnect, host/owned-game integration and real scale benchmarks remain unfinished. Modal fallback shortcut reachability belongs to the broader transport/UI acceptance pass. **Normal native launching, LiveTestReady, Installed and ReleaseReady remain false.** Notify the owner for the two-account Coloseum procedure only after the integrated prerequisites are ready.
