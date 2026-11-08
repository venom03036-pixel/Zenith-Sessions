# Roblox client log entries in the support report

Diagnostics > Save report can include recent Roblox client log tails, as typed entries only, when the user ticks "Include
Roblox client log entries" before saving. It is Zenith's own version of a capability the RoALT reference has (its support
report appends the last few hundred raw lines of its own log and of up to five Roblox logs, each read from the last 4 MB, with
keyword redaction); no RoALT code was used, and the owner's decision of 2026-11-08 shaped it: opt-in at save time, passed
through an allowlist filter (only line shapes Zenith already parses, with anything URL- or token-like removed), bounded per
client, with the report's privacy statement and the Guide updated. This reverses the earlier recorded design that the report
carries "no raw log files" only in the sense that the report can now carry parsed facts from those files; it still never
carries a raw line.

## The allowlist is the correlator's own parser

`RobloxClientLogTails` reads the end of each recent Player log and hands every complete line to
`IncrementalClientLogReader.Parse`, the one parser the session log correlator already uses. A line is either one of the
recognised shapes (join with place and server, identity, connection, teleport, disconnect with its numeric reason, human
check) and yields a `ClientLogObservation`, or it yields nothing. From that observation only the time, the kind, the place ID,
the server (job) ID and the disconnect code are copied into a `ClientLogTailEntry`; the player ID the identity line carries is
dropped, and so are the file's path and name. No line text is kept at any point, so "anything URL- or token-like removed" is
met in its strongest form: a URL, a ticket, a cookie, a chat message or a server address cannot appear in the report because
no text from a log appears in it. The bytes read are cleared before the rented buffer is returned.

## What it reads, and the bounds

- Only the current user's documented Roblox log roots below LocalApplicationData (`RobloxClientFolders.LogRoots`, the same
  two folders the log probe and the log cleanup use), each below that anchor with no reparse point on the way.
- Only `*.log` files directly inside them whose name says a Player client wrote them (`_Player_`), not Studio logs and not
  the crash handler's; only logs written within the last day (`MaximumAge`).
- The newest 8 of those (`MaximumLogs`), by last write; the last 256 KiB of each (`TailBytes`), skipping a line the window
  starts inside and a partial last line still being written; the newest 200 recognised lines of each
  (`MaximumEntriesPerLog`), with the count of older recognised lines dropped reported as `EntriesDropped`.
- One read inspects at most 8,192 directory entries and runs within 5 seconds; it says so (`BudgetLimited`) when it stopped
  at a limit. Each file is opened sharing read, write and delete, so a running client is never disturbed, and its final path
  is checked against the enumerated one (`IncrementalClientLogReader.ValidatePhysicalPath`) before any byte is read.
- A log that cannot be read is still listed, with `Outcome` AccessDenied or ReadFailed and no entries, so the report says
  what was tried. A folder that cannot be enumerated is counted in `FoldersUnreadable`. A log gone between discovery and
  read is left out.

## In the report

Format 3 of the Diagnostics report adds a `ClientLogs` section: `Included: false` on every report saved or copied without
the tick, and otherwise the read time, folder and log counts, the bounds as a `Policy` sentence, and per log its order (1 is
the newest), whether the Store package wrote it, last write, size, bytes and lines read, dropped count, outcome and the
entries (`LoggedUtc`, `Fact`, `PlaceId`, `JobId`, `DisconnectReason`). The `Scope` sentence changes with it. Copy diagnostics
is the clipboard report and never includes client log entries, whatever the tick says.

## Shell, stopping and sample mode

`ShellDiagnosticsFeatures` owns `IncludeClientLogTails` (off at start, never saved to settings) and `CanIncludeClientLogTails`.
`ExportDiagnosticsAsync` reads the tails through `IClientLogTailReader.ReadAsync` under the export's own stop token before it
builds and writes the report; `CancelDiagnosticExport` is already a `Step` in `StopOperations`, so Panic and Lock end the
read, nothing is written, and the stop keeps its own notice. The saved-report notice adds how many recognised entries from
how many logs were included (or that none were found, or that no Roblox logs folder exists), always with "no raw log lines,
player IDs, URLs or paths". The tick clears after each report it was included in, so each save is its own opt-in. In the
sample profile with no injected reader the option is unavailable and cannot be turned on, so the sample profile never reads
the real Roblox folders; the packaged layout self-check verifies the control, its binding, its off default and that boundary.

## Rule 18

CPU and time: at most 8 file reads of at most 256 KiB each and at most 8,192 directory entries within 5 seconds, once per
explicit save. Memory: one rented 256 KiB buffer, at most 8 × 200 small entries. Threads: one pool task per call, no timer,
no poller, no new dependency (System.IO only). Errors: a denied or failed log is reported by outcome, a denied folder by
count, never swallowed; cancellation propagates. Testability: the reader is tested against synthetic files in a temporary
anchor (`RobloxClientLogTailsTests`), including text no report may carry; the shell is tested over a fake reader
(`DiagnosticReportClientLogTailsTests`). Security: no credentials, no AI, no network, no write; the only new data a report
can carry is parsed facts the Sessions page already shows. Backwards compatibility: the report's format counter moved from 2
to 3 (an added optional section); no settings, queue or history format changed.

## What the live trial must confirm

That a real client's current log opens with read, write and delete sharing while the client writes it, and that the
recognised line shapes (an Unverified format, see `IncrementalClientLogReader.FormatSupport`) still match what today's client
writes, so a real report carries the joins and disconnects a support reader expects.
