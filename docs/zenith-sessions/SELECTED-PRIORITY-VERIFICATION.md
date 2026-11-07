# Selected client priority and stopped-arrival verification

Personal development preview 0.49. ReleaseReady and LiveTestReady remain false.

## Working paths

Sessions now offers Normal, Below normal and Restore original for checked clients, with the selected row as fallback. Each command pins a fresh complete observation and exact process start before the first command notification. One owned worker validates the process handle, Windows user, executable name and current priority. Readback confirms accepted changes even if cancellation arrives. Outcomes and pending restore count remain visible.

At most 128 original settings remain tracked. An observed external conflict is preserved, and an already restored original retires ownership without another write. Windows provides no atomic read-and-set for priority, so a simultaneous external write can race the final check. Exit joins current work and attempts restoration before unrelated asynchronous shutdown work. A refused restoration produces a final count and Task Manager guidance before closing. Original-state tracking is memory-only. A crash may leave an applied setting until changed or the process exits.

The launch queue now verifies the exact active attempt and stop state at serialized final publication. A stop while confirmation waits for the lane or storage cannot publish a late arrival. Later observers cannot receive the earlier success after another observer stops it. Process receipts and unresolved reservations remain.

## Executed evidence

- Source suite **4,155 passed**, then **4,155 passed against the final staged files**. The source suite preceded correction of the new packaged layout verifier. Product priority and queue behavior were unchanged by that verifier correction.
- **83 new cases** comprise 46 backend, 26 presentation/lifecycle and 11 arrival publication cases. **284 affected cases passed twice** against the staged binaries.
- **42 packaged checks passed twice**, including actual production BAML command bindings, sample-mode refusal and expanded-panel geometry. Final forced builds reported zero warnings and errors.
- **11 native API checks passed twice** using a temporary helper created by the fixture itself. The helper's executable name intentionally exercises the production RobloxPlayerBeta name guard. It is not Roblox. Its PID/start, Windows user and class were checked through the production adapter. An independent System.Diagnostics readback confirmed apply, restore, preserved external settings and cancellation. Dispose restored the live helper, and a separate owner retired its exited process. Both graceful child exit and native process signalling were confirmed.
- 29 staged files match the manifest and manifest file. The filename/hash check found no unlisted files, reparse points or private-data filenames.
- App SHA-256 is B44BF955B8907984D1F214BF91F417DF33AB3B3FA8AD979587F1C1A55CC36022. Machine evidence is artifacts/sessions-manager-verification-0.49.json, SHA-256 D591A2B43ABC2DBF37A04FB5D0A6011F554B7277D2BC28F89C09507CE1F78811. Native evidence is artifacts/sessions-priority-native-fixture/final-native-1.json and final-native-2.json, both pinned to the same staged Windows/Core assembly hashes.

## Retained failures and limits

The initial 93-case focused run passed and produced one assertion-style warning, corrected without weakening the assertion. Review caught first-notification Panic and late shutdown restoration issues; three additional cases exercise their fixes. Both preliminary packaged checks stopped at the geometry assertion. The verifier measured a disconnected never-shown Window. The isolated diagnostic retained the exact failure. Measuring the actual production content fixed that verifier, preserving the positive-height and binding assertions. The final package and diagnostic passed.

The first two native helper runs used .NET HasExited/WaitForExitAsync as the retirement precondition and failed that assertion. The diagnostic then observed HasExited=true with the Windows process handle still unsignalled. The fixture now joins that retained handle with a bounded native wait and keeps the same retired-process assertion. Production behavior was preserved. [Microsoft wait semantics](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject) and [.NET exit detection](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Windows.cs#L259-L289) explain the distinction.

No installed app or real Roblox client was changed. No improved CPU use, memory demand, FPS, paging or sustainable capacity result is claimed. Full interactive controls, desktop scaling/focus and actual Roblox compatibility are unverified. The native account/arrival/reconnect evidence source, host control IPC, target-specific visual execution, independent background playback, live broadcasting, sixteen-client acceptance and competitive benchmarking still require work.

[Design](SELECTED-PRIORITY-DESIGN.md)