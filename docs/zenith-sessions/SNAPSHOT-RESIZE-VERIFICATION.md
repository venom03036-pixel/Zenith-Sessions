# Saved-sign-in transactions, recovery ownership and client sizing — 0.21

Personal **0.21.0-preview** passed **1,818/1,818 tests**, zero failed/skipped and zero forced-build warnings/errors. Its 118 focused ownership/sizing cases passed **two further runs**. The final binaries passed **27/27 packaged checks twice**, with 25 matching manifest entries, zero extras and zero private/test filename-pattern matches. Nothing was installed; **ReleaseReady:false**.

## Complete saved-sign-in write boundaries

The current saved-session service now reads an operation-local snapshot before sign-in or validation. It pins the selected profile, the entire read-policy record, authoritative highest/committed generations, mutation owner and a digest of the original encrypted file. Record material is excluded from snapshot JSON and its textual representation is redacted. The store compares the snapshot under its existing actual process file lease before reserving and committing its exact successor. Missing records pin deletion tombstones, so a delayed first sign-in cannot reuse a slot that another operation disconnected in the meantime.

Explicit sign-in can replace a fenced same-account record using a fresh official-page session without reusing the retained old cookie. Check saved sign-in conditionally marks its exact old grant SignInRequired before contacting the provider, then verifies its own pending snapshot before validation. Final writes use a fresh operation ID. Cancellation, lost acknowledgment or projection failure can invalidate only that ID's latest reservation; a newer foreign grant survives obsolete cleanup. No weak separate-save/delete fallback exists in this service. Disconnect compares its exact snapshot and advances one fence before removing its protected record and claims.

The encrypted credential envelope remains v2 and generation metadata remains v2. Profiles remain schema3, queue format4, and existing macro/container/settings formats are unchanged. The legacy OAuth service remains retained compatibility code, not a reintroduced setup path or a claim that all legacy authorization operations were redesigned. Native dispatch still requires its own final authorization boundary.

## Reproduced recovery defects and fixes

Two added tests failed against the preceding source and are retained in [the before-fix TRX](../../artifacts/sessions-manager-tests/manager-0.21-before-recovery-fixes.trx):

- A nonzero fully committed generation with both credential and pointer missing was incorrectly reported as an empty slot. It now fails closed regardless of pointer presence. Fresh0/0, valid reserved writes and legitimately deleted high-greater-than-committed states remain distinct.
- Disconnect cleanup removed another operation's newer prepared identity journal before that operation committed its grant. Cleanup now takes the applied deletion-generation bound and preserves higher pending intent.

Prepare, Complete, Replay and Discard now hold a shared per-root projection writer plus an actual exclusive `.writer.lock` file. Compare/read/delete is no longer protected only within one object. Admission is bounded to four calls and a cancellable approximately two-second wait; work retains its existing default fifteen-second deadline and awaits actual ownership completion. Unknown/temp files still count toward the64-file journal cap; only the validated empty coordination file is excluded. Pending JSON stays version1. The old Discard overload requires an absent protected-store snapshot and uses only LastCommitted; metadata-only stores must provide an explicit fence instead of receiving an unsafe fallback.

The current service passes its exact applied fence. Retained legacy disconnect code passes its earlier conservative allocated fence. Source and controlled checks do not establish safe coexistence with an older writer that ignores the new journal lease. **Close older previews before adopting this version**, including0.20 writers that understand the vault format but lack this projection protocol.

An additional service-level test reproduces the full sequence with two actual projection objects and an owned DPAPI vault: A deletes its grant, B prepares a new sign-in, A cleans up, then B commits and projects successfully. Recovery intent and the newer authority remain intact. The staged package exercises the same sequence using synthetic fixtures. Its initial27-case run caught an old synthetic profile revision being reused during final cleanup; carrying forward the verified projected row fixed the fixture. [Failed package evidence](../../artifacts/sessions-distribution-check-0.21-before-fix.json) is retained.

## Explicit physical content sizing

Sessions adds temporary width/height fields, default640×480, and a **Tiny320×240** button that fills fields only. **Apply size** explicitly resizes the checked windows, or the selected window when none are checked. The fields do not save as layout preferences and create no automatic launch behavior.

Content dimensions are physical pixels, independent of monitor scaling; outer frame pixels are extra. The native adapter measures the current client rectangle and its screen position, current outer bounds, PID/start/HWND identity and actual current monitor. The pure planner uses observed frame margins, preserves top-left when possible, otherwise clamps only within that same monitor workarea, and rejects a size that cannot fit. It never guesses scaled content dimensions or retargets a different monitor.

The batch pins selection and dimensions before waiting, rejects duplicate client/window identities, and shares the existing window-operation lane and cancellation generation. Applied requires a valid normal window, unchanged display/workarea/scale, exact observed content width/height and the planned outer bounds. Accepted-but-unconfirmed requests remain explicit. Minimized, maximized, fullscreen, changed or unavailable windows are not silently restored or reassigned. SetWindowPos uses asynchronous/no-activation flags; no input or focus request is introduced. Native client minimum-size/style constraints can still prevent the requested dimensions, and already posted OS requests cannot be recalled.

Lock, Panic, Cancel and Exit fence future work and join actual command completion. Selection/draft changes cannot redirect pending work. The UI separates confirmed content size from outer placement, retains stopped partial results and preserves stop/error messages. The existing128-client per-operation bound is not a saved-profile product limit.

## Executed evidence

The full test run finished **2026-11-06 03:02:19.1931507 UTC**. Packaged passes finished **03:02:39.7162622 UTC** and **03:04:21.8317208 UTC**, with .NET **10.0.12** and SQLite **3.53.4**.

New coverage includes29 snapshot-store cases,12 service races,15 projection-ownership cases,37 resize planner/controller cases,20 resize presentation cases and one additional actual two-process snapshot test. Both competing helpers pin generation1 before readiness; after release exactly one commits generation2 and the other conflicts. The four earlier interprocess cases also pass. The test-only worker is absent from the staged package. Projection tests exercise two instances and held native file sharing; a separate projection child-process test was not performed.

The package's new cases cover snapshot create/delete/recreate, generation-owned projection cleanup, and physical resize planning plus a native zero-HWND rejection. **No real window was resized**, no production UI was rendered or operated, and no Roblox/browser/account test occurred. Source/template and controlled STA tests are not desktop focus, DPI, accessibility or latency acceptance. A filename audit is not comprehensive secret/malware detection.

- App SHA-256: `640AE5CE21D0D7C97FB54E71468A5299B6A7D4A555C881A0673AEACC5628F571`.
- Windows SHA-256: `14B4844B025DA7476C684EE42568D31D58A954A66C4D7CCB9EAB6C2A86F6E73E`.
- Core SHA-256: `5CDD24142C6D09F91FCB0ABDDF5BA63D83DC34F99AE23118DA02B20FE5549F94`.
- Manifest SHA-256: `98F3D2FAE531E0E9009DCA3AA0C21589A6E4520954125F215F412D2B9CAAE3BB`.

[Verification record](../../artifacts/sessions-manager-verification-0.21.json), [full tests](../../artifacts/sessions-manager-tests/manager-core.trx), [focused repeat1](../../artifacts/sessions-manager-tests/manager-0.21-owned-repeat-1.trx), [focused repeat2](../../artifacts/sessions-manager-tests/manager-0.21-owned-repeat-2.trx), [first package check](../../artifacts/sessions-distribution-check-snapshot-first.json), [repeat](../../artifacts/sessions-distribution-check/distribution-check.json), [manifest](../../artifacts/sessions-manager-review/preview-manifest.json).

Rule18: no new dependency, timer, per-profile polling loop or independent input/window pipeline. Native ownership and waits remain bounded/cancellable where their APIs permit; synchronous Windows calls have no hard-stop guarantee. Rule20: this is an incremental extension of existing stores/controllers, with no automatic downgrade or obsolete-secret restoration. The one fixed staging folder is reused; installed editions, user data and reference applications remain unchanged.

## Remaining acceptance

Correct-account native launch/arrival/reconnect, independently targeted background macros, broadcasting, host integration, the owned-game bridge, full custom appearance/mini mode/groups and real2/4/8/16-client tests remain unfinished. Actual client sizing across DPI changes, complete UI performance and equal-workload comparison also remain unverified. No RoALT parity or efficiency-superiority claim is made. Continue building and controlled checks, then notify the owner when the integrated two-account Coloseum live-test build is ready; this increment does not meet that readiness threshold.
