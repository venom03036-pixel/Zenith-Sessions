# Per-client GPU numbers (R-instances-15)

Owner decision 2026-11-08: show each client's busiest GPU engine utilization and dedicated GPU memory, from the per-process GPU
performance counters, sampled with the existing telemetry reader and shown next to CPU and memory. Built in lane D6b, test-first.
This reverses the 0.17 decision never to attach GPU rows to a process ([PUBLISHER-GPU-VERIFICATION.md](PUBLISHER-GPU-VERIFICATION.md),
"Per-process GPU attribution remains unavailable because PID-only counter labels cannot prove process incarnation"). The reversal
is recorded in [HANDOFF.md](HANDOFF.md); this page is the design that makes the attribution safe.

## What the user sees

- Settings → Performance profile → **Show each client's GPU use**, a saved option that is off by default (`SessionsSettings.ShowClientGpu`,
  optional within settings schema 1; files without it load unchanged and older builds ignore it). Save settings applies it.
- When on, the Sessions table gets a column "GPU · busiest engine · dedicated memory" right after "CPU (%)", for example
  `12.3% busiest engine · 0.50 GiB dedicated`. Partial knowledge is said as such ("Engine unavailable · 0.50 GiB dedicated",
  "12.3% busiest engine · dedicated unavailable"); anything unknown, expired or not credited is `Unavailable`; while the option is off
  the column says `Off in Settings`. No value is ever shown as 0 unless the counters measured 0.
- The Guide ("Client GPU use") and Settings search ("client-gpu", Performance section) describe it. The Overview GPU panel
  (adapter-wide counters) is unchanged.

## Why a PID alone is not enough, and what proves identity

GPU Engine and GPU Process Memory counter instances are named `pid_<pid>_luid_…_phys_<n>[_eng_<e>_engtype_<t>]`. Windows reuses PIDs,
so a PID observed in a counter array cannot by itself say which process incarnation produced the rows. The sampler therefore brackets
every collection:

1. **Before**: `WindowsDesktopMonitor.SampleProcesses` (the existing inventory, under the sampling gate) records each client it read as
   PID → start time (`_clientStarts`). A failed scan leaves no candidates; a partial scan leaves only the clients it did read.
2. **Collection**: `SampleSystem` calls `WindowsGpuTelemetryReader.Read(GpuClientAttribution, ct)` with those candidates. The reader
   collects the adapter counters exactly as before, plus the `\GPU Process Memory(*)\Dedicated Usage` gauge.
3. **After**: for each candidate PID that has GPU rows, the reader calls the monitor's `ConfirmClient(pid, start, ct)`, which opens one
   `PROCESS_QUERY_LIMITED_INFORMATION` handle (`IClientProcessSource.Read`) and answers true only when the process is still running with
   the same start time. A process alive with one start time on both sides of the collection was alive throughout, so the rows collected
   between belong to it; an ended, reused or unreadable PID gets no row at all.

Only confirmed clients appear in `GpuTelemetrySnapshot.Clients` (`GpuClientTelemetry(ProcessId, ProcessStartUtcTicks, BusiestEnginePercent,
DedicatedBytes)`), in PID order. The shell matches rows by PID and start time (`SessionProcessKey`), never by PID alone.

## What the numbers mean

- **Busiest engine**: the largest of that client's own engine percentages (one row per physical engine it uses). It is a maximum, never
  a sum across engines or adapters, so it cannot exceed 100 and is not a share of the adapter total shown under Overview.
- **Dedicated memory**: that client's dedicated GPU memory in use, added across adapters (bytes add; the sum saturates).
- **Unknown, never zero**: a row with an invalid status, a non-finite or out-of-range value, a duplicate instance name, a first collection
  (rates need a baseline), a gap over 3 s, an adapter or engine over the existing bounds, or an engine whose contributors add up to more
  than 100 makes that client's engine value unknown; the engine array being incomplete (an unrecognized instance) makes every client's
  engine unknown, as it already does for adapters. A memory instance outside the recognized convention makes every client's memory
  unknown; a duplicate memory instance makes that client's memory unknown; no memory instance means unknown. The adapter view and the
  pressure tracker read exactly what they read before.

## Cost and ownership (Rule 18)

- **Off (default)**: nothing changes. No counter is added, no array is read, no extra handle is opened, `Clients` is empty. The packaged
  self-check and layout check pin the off default.
- **On**: one more gauge array per second (one instance per process using a GPU, per adapter) and one query-limited handle per client with
  GPU rows per second (at most 128, the inventory budget). The per-client map is bounded by the candidates, not by the provider; the counter
  arrays keep their existing 8,192-row and 2 MiB bounds.
- The gauge joins the open query on first use and stays owned by it (closed with it); turning the option off stops reading it without
  rebuilding the query, so the engine counter's baseline is kept and no unknown engine sample is published (Z1401 holds). A failed add is
  retried every 30 s, independently of the adapter counters' retry.
- No new thread, timer, dependency or stop step: the existing one-second sampler owns the work and the existing cancellation token runs
  through the confirmation calls; a cancellation during the check publishes nothing and the reader rebuilds its query as it does for any
  cancelled read. Panic and Lock leave monitoring running, as they always have; this adds no automation.
- Secrets: none. Logs and diagnostics are unchanged.

## Tests

- `GpuTelemetryTests` (fixture counter arrays through `IGpuPdhApi`, a fake process source for the monitor): attribution only to a confirmed
  PID and the check after the collection; off adds no gauge; on adds it to the open query without losing the engine baseline, off stops
  reading it; invalid, overflowing, unrecognized, duplicate and missing rows stay unknown; the 30 s gauge retry; cancellation during the
  check; the monitor's bracket (a PID with another start time at the check, an ended client, a non-client PID never opened); the monitor
  off state; a failed inventory leaving no candidates.
- `GpuTelemetryDisplayTests.ClientGpuTextShowsOnlyFreshAttributedNumbersAndSaysWhenTheOptionIsOff` and `ClientGpuIntegrationTests`
  (saved option → monitor → row text; the XAML column beside CPU and the Settings option; the schema-1 optional field).

## Still to confirm live

With a running client and the option on: the row shows a number that moves with the client's load and agrees in order of magnitude with
Task Manager's per-process GPU column; the extra handle per client per second is not visible in the manager's own CPU; a client that
closes mid-sample shows Unavailable for that second and no number is credited to the next process that takes its PID.
