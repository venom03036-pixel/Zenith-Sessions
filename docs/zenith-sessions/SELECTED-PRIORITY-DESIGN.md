# Selected client priority and stopped-arrival publication

Cycle 0.49 increment follows the verified 0.48 request flow. It adds an actual Windows process control and repairs a final launch publication race. Native account association and independent background execution remain separate acceptance work.

## Selected process control

Sessions offers Normal, Below normal and Restore original for the explicitly checked clients, falling back to the selected row. Pin at most 128 unique PID/start identities from the current complete inventory before asynchronous work. A process Windows would not open, such as another Windows user's client, appears as an identity-unavailable row (no start time) and makes the monitor call the scan incomplete; that row is skipped instead of blocking every selection, though its PID still counts in the duplicate check, and selecting it is refused with that reason. Trim now uses the same pin. Refresh the process identity immediately before reading or changing it. No account label substitutes for process identity.

One backend owns a serial mutation lane and a bounded in-memory map of original and last-applied priority. A native handle is short-lived and checked for the exact PID/start, current Windows user and RobloxPlayerBeta executable. Use only the required query/set rights. Normal and Below normal are the only requested classes. Refuse an existing class outside those supported settings rather than changing it. Confirm the readback after an accepted set, even when cancellation arrives, and preserve a truthful partial result. Cancel prevents later mutations. Restore changes only a still-matching process whose current priority equals the last setting applied by this owner. An observed conflicting external change is reported and preserved. An already restored original is acknowledged without another write. Windows provides no atomic compare-and-set, so a simultaneous external change can race the final check.

The UI reports each outcome and pending restore ownership. Lock and Panic cancel current changes. Exit cancels and joins work, then attempts to restore the owner's outstanding settings through the same lane. A crash or refused Windows operation can leave an applied setting until that process exits or is changed explicitly, and the interface and guide explain this. Exit reports any remaining restoration failure before closing. No profile, account credential or startup automatic application is added.

Changing scheduling priority does not establish lower memory use, FPS control, CPU percentage cap or additional client capacity. Its benefit still needs equal-workload measurement. Microsoft documents the process access requirements and scheduling semantics. [SetPriorityClass](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass), [GetPriorityClass](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getpriorityclass).

## Arrival publication

After an arrival confirmation finishes, retain the same serialized boundary for verifying the active operation, stop generation, paused state and cancellation and for publishing its next state. Panic or Stop cannot be overwritten by an old Arrived result queued behind storage work. Retain already observed process receipts and uncertainty reservations. Test with a controlled supported provider and gated persistence, without promoting the native provider or diagnostic log authority.

## Rules 18 and 20

The changes reuse the shared process inventory and existing view-model lifecycle. There is no new poller, capture owner, input pipeline or dependency. All native work runs off the UI thread, one bounded operation at a time, and actual completion is joined. Original-state tracking is capped at 128 process instances and unresolved ownership is preserved instead of evicted. Failures have typed outcomes and no raw paths or credentials in status.

This is incremental. Existing queue and window behavior, custom settings, saved formats and installed editions remain intact. Tests cover exact identity, denied access, readback, cancellation and restore conflicts, plus the launch stop race. Public and Linux composition do not gain these Personal companion controls. Rollback uses the preceding development files because no data format migration is introduced.
