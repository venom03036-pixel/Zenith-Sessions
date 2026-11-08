using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public enum SessionLogCorrelationStatus
{
    Unknown, ResourceHolderObserved, Missing, AccessDenied, Ambiguous, SourceChanged, BudgetExceeded, Unavailable
}

/// <summary>Resource use is not writer proof, verified login, or permission to act on an account.</summary>
public sealed record ProcessLogDiagnostic(SessionProcessKey Process, SessionLogCorrelationStatus Correlation,
    ClientLogStreamId? SourceStamp, ClientLogBatch? Batch, DateTimeOffset ObservedUtc, DateTimeOffset? LeaseExpiresUtc = null)
{
    public bool CanAuthorizeAccountAction => false;
}

public sealed record SessionLogDiscoverySnapshot(long Revision, DateTimeOffset ObservedUtc,
    IReadOnlyList<ProcessLogDiagnostic> Sessions, int CandidateCount, bool BudgetLimited)
{
    /// <summary>One launch-to-log row per offered launch, in offer order; empty when none was offered.</summary>
    public IReadOnlyList<LaunchLogLink> Launches { get; init; } = Array.Empty<LaunchLogLink>();
}

/// <summary>
/// A launch to look for in client log heads: its operation, the validated browser tracker its native request carried
/// and the start of the process its receipt names. A log created before that start cannot be this launch's, and one
/// created once a later offered launch with the same tracker had started is taken to be that later launch's.
/// The tracker is not a secret, unlike the ticket, and is corroboration only (HANDOFF.md): it proves neither the account nor arrival.
/// </summary>
public readonly record struct LaunchLogExpectation(Guid OperationId, long BrowserTrackerId, long NotBeforeUtcTicks)
{
    public const int MaximumLaunches = 32;
    internal bool IsValid => OperationId != Guid.Empty && BrowserTrackerId is > 0 and <= LaunchProcessReceipt.MaximumBrowserTrackerId
        && NotBeforeUtcTicks >= DateTime.FromFileTimeUtc(0).Ticks && NotBeforeUtcTicks <= DateTime.MaxValue.Ticks;

    /// <summary>The queue's valid receipts that carry a tracker, most recently recorded first, at most MaximumLaunches.</summary>
    public static LaunchLogExpectation[] From(LaunchQueueSnapshot? queue)
    {
        if (queue?.Operations is not { Count: > 0 } operations) return [];
        var found = new List<(DateTimeOffset Recorded, LaunchLogExpectation Launch)>();
        foreach (var operation in operations)
            if (operation?.ProcessReceipt is { IsValid: true, BrowserTrackerId: { } tracker } receipt && operation.Intent is { } intent)
                found.Add((receipt.RecordedUtc, new(intent.OperationId, tracker, receipt.ProcessStartUtcTicks)));
        found.Sort(static (left, right) => right.Recorded.CompareTo(left.Recorded));
        var result = new LaunchLogExpectation[Math.Min(found.Count, MaximumLaunches)];
        for (int index = 0; index < result.Length; index++) result[index] = found[index].Launch;
        return result;
    }
}

public enum LaunchLogLinkStatus
{
    /// <summary>
    /// No client log names its tracker in the part read so far that was created since the launch began and before any
    /// later offered launch with the same tracker began; or one does, but the last head read of another log written since
    /// the launch began has not succeeded (no read yet, or the latest one failed). Another offered launch with the same
    /// tracker that began at the same instant changes nothing on its own: the tie counts only once such a log names it.
    /// </summary>
    NotLinked,
    /// <summary>
    /// The last head read of every log written since the launch began succeeded, exactly one such log names it, no other
    /// offered launch with the same tracker began at the same instant, and that log names the tracker of no other offered
    /// launch that had begun when the log was created (a launch that began later cannot own it).
    /// </summary>
    Linked,
    /// <summary>
    /// More than one such log names it; one does and another offered launch with the same tracker began at the same
    /// instant; or the launch would otherwise be Linked, but its log also names the tracker of another launch that had
    /// begun when the log was created.
    /// </summary>
    Ambiguous,
    /// <summary>Client log discovery, or the whole pass, is unavailable.</summary>
    Unavailable
}

/// <summary>
/// Launch-to-log, kept separate from the holder binding: Status says whether one client log's head names this launch's
/// browser tracker; Process is the process that currently holds that log (the holder binding), or null when none is bound.
/// EarlierLaunchMayOwnLog is set on a Linked row only, when an earlier offered launch with the same tracker has no log of
/// its own (NotLinked): the log credited here may be that launch's, created only after this launch began (the OwnerOf
/// tradeoff recorded in HANDOFF.md), so the row says so for as long as that holds.
/// A linked log is corroboration only: never writer proof, account identity, arrival or permission to act.
/// </summary>
public sealed record LaunchLogLink(Guid OperationId, LaunchLogLinkStatus Status, SessionProcessKey? Process = null, bool EarlierLaunchMayOwnLog = false)
{
    public bool CanAuthorizeAccountAction => false;
}

/// <summary>
/// ReadsPerPass bounds the turns that may query holders or bind new logs. SharedReadBytes is the per-pass
/// content/anchor budget for those turns; a pass may raise it to one BytesPerReader read per bound reader
/// (never above MaximumPassReadBytes) and spend the extra on further reads of logs that still have unread bytes.
/// </summary>
public sealed record SessionLogCorrelatorOptions(int MaximumReaders = 16, int MaximumCandidates = 64,
    int MaximumProcesses = 128, int ReadsPerPass = 4, int SharedReadBytes = 64 * 1024,
    int BytesPerReader = 16 * 1024, int ReplayFactsPerReader = 128)
{
    internal const int MaximumPassReadBytes = 1024 * 1024;
    internal SessionLogCorrelatorOptions Validated()
    {
        if (MaximumReaders is < 1 or > 64 || MaximumCandidates < MaximumReaders || MaximumCandidates > 256
            || MaximumProcesses is < 1 or > 1024 || ReadsPerPass is < 1 or > 16
            || SharedReadBytes is < 1024 or > MaximumPassReadBytes || BytesPerReader is < 1024 or > 65536
            || ReplayFactsPerReader is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(SessionLogCorrelatorOptions));
        return this;
    }
}

internal enum ClientLogVariant { Win32, Uwp }
internal sealed record ClientLogCandidate(string Path, ClientLogVariant Variant, DateTimeOffset ModifiedUtc);
internal sealed record ClientLogCandidates(IReadOnlyList<ClientLogCandidate> Files, bool BudgetLimited, bool Unavailable = false);
internal readonly record struct ResourceHolder(int ProcessId, long ProcessStartUtcTicks);
internal sealed record ResourceHolderResult(SessionLogCorrelationStatus Status, IReadOnlyList<ResourceHolder> Holders);
internal interface IClientLogProbe : IDisposable
{
    ClientLogCandidates Discover(int maximum, CancellationToken ct);
    bool IsCurrentClient(SessionProcessKey process, ClientLogVariant variant);
    ResourceHolderResult QueryHolders(string path, CancellationToken ct);
}

/// <summary>
/// One owned latest-input worker. Offer performs no file/native work and never blocks the essential sampler.
/// Reconciliation is driven by offers, not a new timer. All paths stay inside this Windows implementation.
/// RM calls are synchronous: cancellation/deadlines are checked between native calls, not hard interrupts.
/// DisposeAsync cancels and awaits the actual owner; it never abandons native work or starts replacement workers.
/// A failed pass is retried by the same owner on a later offer; only consecutive failures end it. A failed discovery
/// keeps every bound reader and its published facts; only a failure after a reader was touched resets them all.
/// Each failure the owner recovers from is passed to the optional recoveredFailure callback as its exception type only,
/// on the worker thread, at most once per pass; the callback must not block. The failure that ends the owner is not
/// passed to it: that one surfaces from DisposeAsync.
/// Launch-to-log: when launches are offered, each pass also reads at most HeadReadsPerPass bounded log heads (after
/// every other read, inside the same deadline) and links a launch to the one log created since it began whose head
/// names its browser tracker. That link is independent of the PID, so it survives a client restarting under a new one.
/// </summary>
public sealed class WindowsSessionLogCorrelator : IAsyncDisposable
{
    private sealed record Input(SessionProcessKey[] Processes, bool BudgetLimited, LaunchLogExpectation[] Launches);
    private sealed class Reader(ClientLogCandidate candidate)
    {
        public readonly ClientLogCandidate Candidate = candidate;
        // Launch-to-log: the last head read of this file (null before the first, or after a failed read, and then the
        // launches it could belong to stay unlinked), when a read was last attempted (it paces retries only), and the
        // rotation order of head reads. A complete head is never read again for the same file, and nor is a settled one
        // (HeadSettled): Unheld says the last holder query found no process holding the log, and HeadUnchanged counts
        // the head checks in a row, while it was unheld, that found the same file at the same length.
        public IncrementalClientLogReader.ClientLogHead? Head;
        public long? HeadAt;
        public long LastHead;
        public bool Unheld;
        public int HeadUnchanged;
        public IncrementalClientLogReader? Log;
        public List<ClientLogObservation>? Facts;
        public ClientLogBatch? Batch;
        public SessionProcessKey? Process;
        public SessionProcessKey[] AffectedProcesses = [];
        public IncrementalClientLogReader.FileIdentity? Identity;
        public SessionLogCorrelationStatus Status;
        public DateTimeOffset QueryUtc, LeaseUntil;
        public long? QueryAt;
        // LastVisit orders only the regular rotation; LastRead also counts extra backlog reads.
        public long LastVisit, LastRead;
        public long ReplayFloorOffset;
        // The last read stopped on its byte cap, not its fact cap, and the file still has bytes past the cursor.
        public bool UnreadBytes;
    }

    // A holder observation is trusted for this long. Holders are still re-queried on every visit made
    // 5 s or more after the last query (QueryDue), so the lease never extends without a fresh observation.
    // With 16 bound readers, one scout slot per pass leaves 3 visits per pass, so each bound reader is
    // revisited every 5-6 passes: 25 s covers passes of up to about 4 s (10 s lapsed at 2 s passes).
    // It stays under the 30 s lease cap that SessionEvidenceRegistry and SessionLogProjection enforce;
    // the 15 s display freshness check there is separate and does not discard facts.
    private static readonly TimeSpan HolderLease = TimeSpan.FromSeconds(25);
    // The fifth failed pass in a row ends the worker, so a persistent fault still reaches DisposeAsync.
    private const int MaximumConsecutivePassFailures = 5;
    // At most 4 x 64 KiB of head bytes per pass, only while some log written since a launch began is unread or may still
    // grow. An incomplete head is checked again no sooner than HeadRecheck: each check opens the file and reads its final
    // path, identity and length, and its bytes are read again only when that length changed. Checks stop once the last
    // holder query found no process holding the log and SettledHeadChecks checks in a row since then found it unchanged,
    // because nothing can still be writing it (a log that ended before its first join line is never complete). They
    // resume when a holder query finds a process holding it again or the file is replaced; the regular holder visits
    // (at most ReadsPerPass per pass, each opening the log for its identity) see both.
    private const int HeadReadsPerPass = 4;
    private const int SettledHeadChecks = 2;
    private static readonly TimeSpan HeadRecheck = TimeSpan.FromSeconds(2);
    private readonly SessionLogCorrelatorOptions _options;
    private readonly TimeProvider _clock;
    private readonly IClientLogProbe _probe;
    private readonly Action<Type>? _recoveredFailure;
    private readonly Channel<Input> _inputs = Channel.CreateBounded<Input>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Reader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _worker;
    private Input _latest = new([], false, []);
    private string[] _watched = [];
    private SessionLogDiscoverySnapshot _snapshot = new(0, DateTimeOffset.MinValue, Array.Empty<ProcessLogDiagnostic>(), 0, false);
    private ClientLogCandidates _candidates = new(Array.Empty<ClientLogCandidate>(), false);
    private long? _lastPass, _lastDiscovery;
    private long _revision, _visit;
    private int _liveReaders, _disposed, _consecutiveFailures;
    private bool _scoutFirst;
    // One turn reserves one full per-reader read, which includes the reader's bounded rewrite anchor.
    private int TurnBytes => Math.Min(_options.BytesPerReader, _options.SharedReadBytes);
    private int ReaderByteBudget => Math.Max(1, TurnBytes - 64);

    public WindowsSessionLogCorrelator(SessionLogCorrelatorOptions? options = null, TimeProvider? timeProvider = null,
        Action<Type>? recoveredFailure = null)
        : this(new WindowsClientLogProbe(), options, timeProvider, recoveredFailure) { }

    internal WindowsSessionLogCorrelator(IClientLogProbe probe, SessionLogCorrelatorOptions? options = null, TimeProvider? timeProvider = null,
        Action<Type>? recoveredFailure = null)
    {
        _options = (options ?? new()).Validated(); _clock = timeProvider ?? TimeProvider.System;
        _recoveredFailure = recoveredFailure;
        _probe = probe; _worker = Task.Run(RunAsync);
    }

    public SessionLogDiscoverySnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Any thread. The client logs this owner is watching as of its last discovery: every discovered candidate (written
    /// within the last day) and every log still bound to an offered process, whether or not it is being read right now. The
    /// log cleanup leaves each of these alone. Kept apart from <see cref="Snapshot"/>, which carries no path.
    /// </summary>
    public IReadOnlyList<string> WatchedLogPaths => Volatile.Read(ref _watched);

    public void Offer(DesktopSnapshot snapshot, string? hostId = null, IReadOnlyList<LaunchLogExpectation>? launches = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var keys = new List<SessionProcessKey>(_options.MaximumProcesses);
        bool limited = false;
        foreach (var session in snapshot.Sessions)
        {
            var key = new SessionProcessKey(hostId, session.ProcessId, session.ProcessStartUtcTicks);
            if (!key.IsValid || session.Problem is not null) continue;
            if (keys.Count == _options.MaximumProcesses) { limited = true; break; }
            if (!keys.Contains(key)) keys.Add(key);
        }
        keys.Sort((left, right) => left.ProcessId != right.ProcessId ? left.ProcessId.CompareTo(right.ProcessId)
            : left.ProcessStartUtcTicks.CompareTo(right.ProcessStartUtcTicks));
        var input = new Input(keys.ToArray(), limited, Expected(launches));
        Volatile.Write(ref _latest, input); _inputs.Writer.TryWrite(input);
    }

    // Valid launches only, the first of each operation, at most MaximumLaunches, in offer order. Pure: no file work.
    private static LaunchLogExpectation[] Expected(IReadOnlyList<LaunchLogExpectation>? launches)
    {
        if (launches is not { Count: > 0 }) return [];
        var kept = new List<LaunchLogExpectation>(Math.Min(launches.Count, LaunchLogExpectation.MaximumLaunches));
        for (int index = 0; index < launches.Count && kept.Count < LaunchLogExpectation.MaximumLaunches; index++)
        {
            var launch = launches[index];
            bool seen = false;
            foreach (var other in kept) seen |= other.OperationId == launch.OperationId;
            if (launch.IsValid && !seen) kept.Add(launch);
        }
        return kept.ToArray();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var input in _inputs.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (input.Processes.Length == 0)
                {
                    ClearReaders(); _candidates = new(Array.Empty<ClientLogCandidate>(), false); _lastDiscovery = null;
                    Publish(input, []); continue;
                }
                if (_lastPass is { } prior && _clock.GetElapsedTime(prior) < TimeSpan.FromSeconds(1)) continue;
                _lastPass = _clock.GetTimestamp();
                Type? recovered = null;
                try
                {
                    var discoveryFailure = await PassAsync(input, _stop.Token).ConfigureAwait(false);
                    if (discoveryFailure is null) _consecutiveFailures = 0;
                    // The pass already kept every reader and published. A failed discovery still counts as a failed
                    // pass, so a persistent probe fault ends this owner on the fifth in a row and surfaces at DisposeAsync.
                    else if (++_consecutiveFailures == MaximumConsecutivePassFailures) discoveryFailure.Throw();
                    else recovered = discoveryFailure.SourceException.GetType();
                }
                catch (Exception failure) when (!_stop.IsCancellationRequested && _consecutiveFailures < MaximumConsecutivePassFailures - 1)
                {
                    // A pass that failed after touching a reader is not terminal: drop every cursor, mark all rows
                    // Unavailable, and rediscover on the next offer, which the 1 s pass gate already rate-limits. A failed
                    // discovery never reaches here (PassAsync keeps the readers). Repeated failures still end this owner.
                    _consecutiveFailures++;
                    ClearReaders(); _candidates = new(Array.Empty<ClientLogCandidate>(), false); _lastDiscovery = null;
                    Publish(Volatile.Read(ref _latest), [], SessionLogCorrelationStatus.Unavailable);
                    recovered = failure.GetType();
                }
                // A recovered failure is reported once, by type only (Rule 10), outside the recovery handler: a fault in
                // the report itself ends this owner and surfaces at DisposeAsync instead of being taken for a pass failure.
                if (recovered is not null) _recoveredFailure?.Invoke(recovered);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch
        {
            Publish(Volatile.Read(ref _latest), [], SessionLogCorrelationStatus.Unavailable);
            throw; // Owned worker failure remains observable when disposed; no raw exception text is published.
        }
        finally { ClearReaders(); _probe.Dispose(); }
    }

    /// <returns>The failure of this pass's discovery, or null when discovery succeeded or was not due.</returns>
    private async Task<ExceptionDispatchInfo?> PassAsync(Input input, CancellationToken ct)
    {
        ExceptionDispatchInfo? discoveryFailure = null;
        if (_lastDiscovery is null || _clock.GetElapsedTime(_lastDiscovery.Value) >= TimeSpan.FromSeconds(5))
        {
            _lastDiscovery = _clock.GetTimestamp();
            ClientLogCandidates? found = null;
            try { found = _probe.Discover(_options.MaximumCandidates, ct); }
            catch (Exception failure) when (!ct.IsCancellationRequested)
            {
                // Discovery runs before any reader is touched (for example ERROR_OPERATION_ABORTED surfacing as an
                // OperationCanceledException). Keep every reader, cursor, holder lease and bound row: only discovery is
                // unavailable, so unbound rows publish Unavailable. It is retried on the next offer (1 s pass gate).
                discoveryFailure = ExceptionDispatchInfo.Capture(failure);
                _lastDiscovery = null;
                _candidates = _candidates with { Unavailable = true };
            }
            if (found is not null)
            {
                _candidates = found;
                var candidates = _candidates.Files.Take(_options.MaximumCandidates).ToArray();
                var keep = candidates.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var reader in _readers.Values.Where(reader => !keep.Contains(reader.Candidate.Path)
                    && (reader.Log is null || reader.Process is not { } process || !input.Processes.Contains(process))).ToArray())
                    Remove(reader);
                foreach (var candidate in candidates)
                    if (!_readers.ContainsKey(candidate.Path))
                        _readers.Add(candidate.Path, new(candidate));
                Volatile.Write(ref _watched, _readers.Keys.ToArray());
            }
        }

        foreach (var reader in _readers.Values)
            if (reader.Process is { } process && !input.Processes.Contains(process))
                Reset(reader, SessionLogCorrelationStatus.Unknown);

        // Explore unbound files even when all read slots are in use. Alternate which lane goes
        // first so one slow holder query cannot starve the other lane at the pass deadline.
        var readers = _readers.Values.ToArray();
        var active = readers.Where(static reader => reader.Log is not null).OrderBy(static reader => reader.LastVisit).ToArray();
        var scouting = readers.Where(reader => reader.Log is null && QueryDue(reader)).OrderBy(static reader => reader.LastVisit).ToArray();
        int count = Math.Min(active.Length + scouting.Length,
            Math.Min(_options.ReadsPerPass, _options.SharedReadBytes / TurnBytes));
        int activeIndex = 0, scoutIndex = 0;
        bool limited = _candidates.BudgetLimited || scouting.Length > 0 && count < active.Length + scouting.Length;
        if (active.Length > 0 && scouting.Length > 0) _scoutFirst = !_scoutFirst;
        int scoutTurn = _scoutFirst ? 0 : count > 1 ? count - 1 : -1;
        long began = _clock.GetTimestamp();
        for (int n = 0; n < count; n++)
        {
            ct.ThrowIfCancellationRequested();
            if (_clock.GetElapsedTime(began) >= TimeSpan.FromSeconds(2)) { limited = true; break; }
            bool scout = scoutIndex < scouting.Length
                && (activeIndex == active.Length || n == scoutTurn);
            var reader = scout ? scouting[scoutIndex++] : active[activeIndex++];
            reader.LastVisit = reader.LastRead = ++_visit;
            await ObserveAsync(reader, input, ct).ConfigureAwait(false);
        }

        // A chatty client can write more than one per-reader read between its regular turns. Raise the pass budget
        // to one full read per bound reader (at most 1 MiB) and spend what the regular turns left on further reads
        // of logs whose last read stopped on its byte cap, least recently read first, inside the same 2 s deadline.
        // These turns never start a holder query (a reader whose query is due waits for its regular turn) and do not
        // move a reader in the regular rotation, so holder queries per pass stay within ReadsPerPass and every bound
        // reader is still revisited, and its holder lease renewed, exactly as often as before.
        long passBytes = Math.Min(SessionLogCorrelatorOptions.MaximumPassReadBytes,
            Math.Max(_options.SharedReadBytes, (long)active.Length * TurnBytes));
        for (long turn = count; turn < passBytes / TurnBytes; turn++)
        {
            Reader? next = null;
            foreach (var reader in readers)
                if (reader.Log is not null && reader.Batch is not null && reader.UnreadBytes && !QueryDue(reader)
                    && (next is null || reader.LastRead < next.LastRead)) next = reader;
            if (next is null) break;
            ct.ThrowIfCancellationRequested();
            if (_clock.GetElapsedTime(began) >= TimeSpan.FromSeconds(2)) { limited = true; break; }
            next.LastRead = ++_visit;
            await ObserveAsync(next, input, ct).ConfigureAwait(false);
        }

        // Launch-to-log heads go last, so they never take time from the reads above. Only logs written since the earliest
        // offered launch began are read, least recently read first. These reads start no holder query and bind nothing.
        if (input.Launches.Length > 0)
        {
            long notBefore = long.MaxValue;
            foreach (var launch in input.Launches) notBefore = Math.Min(notBefore, launch.NotBeforeUtcTicks);
            for (int turn = 0; turn < HeadReadsPerPass; turn++)
            {
                Reader? next = null;
                foreach (var reader in readers)
                    if (HeadDue(reader, notBefore) && (next is null || reader.LastHead < next.LastHead)) next = reader;
                if (next is null) break;
                ct.ThrowIfCancellationRequested();
                if (_clock.GetElapsedTime(began) >= TimeSpan.FromSeconds(2)) { limited = true; break; }
                next.LastHead = ++_visit; next.HeadAt = _clock.GetTimestamp();
                ReadHead(next, ct);
            }
        }
        limited |= _liveReaders == _options.MaximumReaders && readers.Any(static reader => reader.Process is not null && reader.Log is null);
        if (!input.Processes.SequenceEqual(Volatile.Read(ref _latest).Processes)) return discoveryFailure;
        Publish(input, readers, _candidates.Unavailable ? SessionLogCorrelationStatus.Unavailable : SessionLogCorrelationStatus.Unknown, limited);
        return discoveryFailure;
    }

    private async Task ObserveAsync(Reader reader, Input input, CancellationToken ct)
    {
        try
        {
            var before = IncrementalClientLogReader.ProbeIdentity(reader.Candidate.Path);
            // A replaced file's head is read again before it can link anything.
            if (reader.Head is { } head && head.Identity != before) { reader.Head = null; reader.HeadAt = null; reader.HeadUnchanged = 0; }
            if (reader.Identity is { } identity && before != identity) Reset(reader, SessionLogCorrelationStatus.SourceChanged);
            bool refreshed = QueryDue(reader);
            if (refreshed)
            {
                reader.QueryAt = _clock.GetTimestamp();
                reader.Unheld = false;
                var result = _probe.QueryHolders(reader.Candidate.Path, ct);
                reader.QueryUtc = _clock.GetUtcNow(); reader.LeaseUntil = reader.QueryUtc + HolderLease;
                // Launch-to-log: only a successful query that found no holder at all lets the head settle (HeadSettled).
                reader.Unheld = result.Status == SessionLogCorrelationStatus.ResourceHolderObserved && result.Holders.Count == 0;
                if (!reader.Unheld) reader.HeadUnchanged = 0;
                if (result.Status != SessionLogCorrelationStatus.ResourceHolderObserved)
                { Reset(reader, result.Status, keepQuery: true); return; }
                if (result.Holders.Count != 1)
                {
                    reader.AffectedProcesses = input.Processes.Where(p => result.Holders.Any(h => h.ProcessId == p.ProcessId && h.ProcessStartUtcTicks == p.ProcessStartUtcTicks)).ToArray();
                    Reset(reader, result.Holders.Count > 1 ? SessionLogCorrelationStatus.Ambiguous : SessionLogCorrelationStatus.Unknown, true); return;
                }
                var holder = result.Holders[0];
                var matches = input.Processes.Where(p => p.ProcessId == holder.ProcessId && p.ProcessStartUtcTicks == holder.ProcessStartUtcTicks).ToArray();
                if (matches.Length != 1)
                { Reset(reader, SessionLogCorrelationStatus.Unknown, true); return; }
                if (reader.Process is { } previous && previous != matches[0]) Reset(reader, SessionLogCorrelationStatus.SourceChanged, true);
                reader.Process = matches[0]; reader.AffectedProcesses = []; reader.Identity = before; reader.Status = SessionLogCorrelationStatus.ResourceHolderObserved;
            }
            if (reader.Status != SessionLogCorrelationStatus.ResourceHolderObserved) return;
            var now = _clock.GetUtcNow();
            if (reader.Process is not { } process || reader.QueryAt is not { } queried
                || _clock.GetElapsedTime(queried) >= HolderLease || reader.QueryUtc - now > TimeSpan.FromSeconds(2)
                || reader.LeaseUntil <= now || !input.Processes.Contains(process)
                || !_probe.IsCurrentClient(process, reader.Candidate.Variant))
            { Reset(reader, SessionLogCorrelationStatus.Unknown, keepQuery: refreshed); return; }
            if (reader.Log is null)
            {
                if (_liveReaders == _options.MaximumReaders || _readers.Values.Any(other =>
                    other.Log is not null && other.Process == process && other.LeaseUntil > now))
                {
                    if (IncrementalClientLogReader.ProbeIdentity(reader.Candidate.Path) != before)
                        Reset(reader, SessionLogCorrelationStatus.SourceChanged);
                    return;
                }

                reader.Log = new(reader.Candidate.Path, new(MaximumBytesPerRead: ReaderByteBudget,
                    MaximumObservationsPerRead: _options.ReplayFactsPerReader, Deadline: TimeSpan.FromMilliseconds(250)), _clock);
                reader.Facts = [];
                _liveReaders++;
            }
            var batch = await reader.Log.ReadAsync(ct).ConfigureAwait(false);
            var after = IncrementalClientLogReader.ProbeIdentity(reader.Candidate.Path);
            if (!_probe.IsCurrentClient(process, reader.Candidate.Variant))
            { Reset(reader, SessionLogCorrelationStatus.Unknown); return; }
            if (before != after || reader.Log.SourceIdentity != before)
            { Reset(reader, SessionLogCorrelationStatus.SourceChanged); return; }
            if (batch.Status != ClientLogReadStatus.Ready)
            {
                Reset(reader, batch.Status == ClientLogReadStatus.AccessDenied ? SessionLogCorrelationStatus.AccessDenied
                    : batch.Status == ClientLogReadStatus.Missing ? SessionLogCorrelationStatus.Missing
                    : batch.Status == ClientLogReadStatus.DeadlineExceeded ? SessionLogCorrelationStatus.BudgetExceeded : SessionLogCorrelationStatus.Unavailable);
                return;
            }
            if (reader.Batch is { } previousBatch && previousBatch.Stream != batch.Stream)
            { Reset(reader, SessionLogCorrelationStatus.SourceChanged); return; }
            var facts = reader.Facts!;
            foreach (var observation in batch.Observations)
            {
                facts.Add(observation with { FormatSupport = ClientLogFormatSupport.Unverified });
                if (facts.Count > _options.ReplayFactsPerReader)
                {
                    reader.ReplayFloorOffset = facts[0].EndOffset;
                    facts.RemoveAt(0);
                }
            }
            reader.Batch = batch with { Observations = Array.AsReadOnly(facts.ToArray()), ReplayFloorOffset = reader.ReplayFloorOffset };
            reader.Identity = after;
            // A read stopped by its fact cap is not read again in the same pass: another read would push facts the
            // consumer has not seen out of the bounded replay window. It continues on its next regular turn.
            reader.UnreadBytes = batch.HasMore && batch.Observations.Count < _options.ReplayFactsPerReader;
        }
        catch (FileNotFoundException) { Reset(reader, SessionLogCorrelationStatus.Missing); }
        catch (DirectoryNotFoundException) { Reset(reader, SessionLogCorrelationStatus.Missing); }
        catch (UnauthorizedAccessException) { Reset(reader, SessionLogCorrelationStatus.AccessDenied); }
        catch (System.ComponentModel.Win32Exception) { Reset(reader, SessionLogCorrelationStatus.Unavailable); }
        catch (IOException) { Reset(reader, SessionLogCorrelationStatus.Unavailable); }
    }

    private bool QueryDue(Reader reader) => reader.QueryAt is not { } queried
        || _clock.GetElapsedTime(queried) >= TimeSpan.FromSeconds(5);

    private bool HeadDue(Reader reader, long notBefore) => reader.Candidate.ModifiedUtc.UtcTicks >= notBefore
        && reader.Head is not { Complete: true } && !HeadSettled(reader)
        && (reader.HeadAt is not { } at || _clock.GetElapsedTime(at) >= HeadRecheck);

    // Nothing held the log at the last holder query, and at least SettledHeadChecks checks in a row since then, HeadRecheck
    // apart, each found the same file at the same length as the check before, so it is not opened for its head again.
    private static bool HeadSettled(Reader reader) => reader.Head is not null && reader.Unheld && reader.HeadUnchanged >= SettledHeadChecks;

    private static void ReadHead(Reader reader, CancellationToken ct)
    {
        var previous = reader.Head;
        try { reader.Head = IncrementalClientLogReader.ReadHead(reader.Candidate.Path, previous, ct); }
        // A head that cannot be read names nothing and is tried again later; like a failed read above, it is not a pass failure.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { reader.Head = null; }
        // ReadHead returns the previous head itself when the file's identity and length are unchanged.
        reader.HeadUnchanged = reader.Unheld && previous is not null && ReferenceEquals(reader.Head, previous) ? reader.HeadUnchanged + 1 : 0;
    }

    /// <summary>
    /// One row per offered launch. A launch links to a log only when, among the logs written since it began, every one's
    /// head has been read and its last read succeeded, and exactly one names its tracker and is this launch's (OwnerOf: created since it
    /// began, and before any later offered launch with that tracker began); no other launch with that tracker began at the
    /// same instant; and that log names no other offered launch's tracker, counting only launches that had begun when the
    /// log was created, since a launch that began later cannot own it. A relaunch with the same tracker therefore links each
    /// launch to its own log; while an earlier launch with that tracker has no log, the relaunch's row says its log may be
    /// that launch's (EarlierLaunchMayOwnLog). Process is that log's holder only while the holder binding is current and
    /// unique, exactly as the per-process rows report it.
    /// </summary>
    private static LaunchLogLink[] LinkLaunches(Input input, Reader[] readers, Dictionary<SessionProcessKey, Reader?> matches, bool unavailable)
    {
        var launches = input.Launches;
        if (launches.Length == 0) return [];
        var links = new LaunchLogLink[launches.Length];
        var linked = new Reader?[launches.Length];
        for (int index = 0; index < launches.Length; index++)
        {
            var launch = launches[index];
            bool tied = false, pending = false; int named = 0; Reader? found = null;
            foreach (var reader in readers)
            {
                if (reader.Candidate.ModifiedUtc.UtcTicks < launch.NotBeforeUtcTicks) continue;
                // No head yet, or the last head read failed (HeadAt is set either way): this log could name the tracker.
                if (reader.Head is not { } head) pending = true;
                else if (head.CreatedUtcTicks >= launch.NotBeforeUtcTicks
                    && Array.IndexOf(head.Trackers, launch.BrowserTrackerId) >= 0
                    && OwnerOf(launches, index, head.CreatedUtcTicks) is var owner and not LogOwner.Later)
                { named++; found = reader; tied |= owner == LogOwner.Tied; }
            }
            if (tied || named > 1) links[index] = new(launch.OperationId, LaunchLogLinkStatus.Ambiguous);
            else if (named == 1 && !pending) linked[index] = found;
            else links[index] = new(launch.OperationId, unavailable ? LaunchLogLinkStatus.Unavailable : LaunchLogLinkStatus.NotLinked);
        }
        for (int index = 0; index < launches.Length; index++)
        {
            if (linked[index] is not { Head: { } head } reader) continue;
            // Exclusive: the log names no other offered launch's tracker, whether or not that launch could link itself
            // (Ambiguous or pending), and no other launch links to it. Same-tracker launches are settled by OwnerOf, and a
            // launch that began after the log was created cannot own it (the same time rule the loop above applies), so
            // its tracker in the head takes nothing from this launch. Bounded: at most MaximumLaunches x 4 tracker
            // comparisons per linked launch, and no allocation.
            bool exclusive = true;
            for (int other = 0; other < launches.Length; other++)
                exclusive &= other == index || !ReferenceEquals(linked[other], reader)
                    && (launches[other].BrowserTrackerId == launches[index].BrowserTrackerId
                        || launches[other].NotBeforeUtcTicks > head.CreatedUtcTicks
                        || Array.IndexOf(head.Trackers, launches[other].BrowserTrackerId) < 0);
            links[index] = !exclusive ? new(launches[index].OperationId, LaunchLogLinkStatus.Ambiguous)
                : new(launches[index].OperationId, LaunchLogLinkStatus.Linked,
                    reader.Process is { } process && matches.TryGetValue(process, out var holder) && ReferenceEquals(holder, reader) ? process : null,
                    EarlierLaunchMayOwnLog(launches, links, linked, index));
        }
        return links;
    }

    // The OwnerOf tradeoff, told to the user: an earlier offered launch with this launch's tracker has no log of its own
    // (NotLinked, from the first loop; a launch that links or is Ambiguous has one), so the log credited to this launch
    // may be that earlier client's, created only after this launch began. A later launch with no log says nothing: a log
    // created before it began cannot be its own. Bounded: at most MaximumLaunches comparisons per linked launch, and no
    // allocation.
    private static bool EarlierLaunchMayOwnLog(LaunchLogExpectation[] launches, LaunchLogLink[] links, Reader?[] linked, int index)
    {
        var launch = launches[index];
        for (int other = 0; other < launches.Length; other++)
            if (other != index && launches[other].BrowserTrackerId == launch.BrowserTrackerId
                && launches[other].NotBeforeUtcTicks < launch.NotBeforeUtcTicks
                && linked[other] is null && links[other] is { Status: LaunchLogLinkStatus.NotLinked }) return true;
        return false;
    }

    private enum LogOwner { This, Tied, Later }

    // A log naming a tracker belongs to the offered launch with that tracker that began last at or before the log was
    // created, so a relaunch with the same tracker leaves the earlier launch's own log with it. Later: another launch with
    // the tracker began after this one and no later than the log, so the log is that one's. Tied: another began at the same
    // instant, so the log could be either's. Tradeoff (recorded in HANDOFF.md, launch-to-log, for the owner to keep or
    // reverse): an earlier launch whose client created its log only after the relaunch began has that log taken for the
    // relaunch's, whose row then names the earlier client's PID while the earlier launch shows no log; the row says so
    // (EarlierLaunchMayOwnLog) for as long as the earlier launch has none, and the link stays corroboration only.
    // Bounded: at most MaximumLaunches comparisons per log, and no allocation.
    private static LogOwner OwnerOf(LaunchLogExpectation[] launches, int index, long createdUtcTicks)
    {
        var launch = launches[index];
        var owner = LogOwner.This;
        for (int other = 0; other < launches.Length; other++)
        {
            if (other == index || launches[other].BrowserTrackerId != launch.BrowserTrackerId) continue;
            long began = launches[other].NotBeforeUtcTicks;
            if (began > launch.NotBeforeUtcTicks && began <= createdUtcTicks) return LogOwner.Later;
            if (began == launch.NotBeforeUtcTicks) owner = LogOwner.Tied;
        }
        return owner;
    }

    private void Reset(Reader reader, SessionLogCorrelationStatus status, bool keepQuery = false)
    {
        if (reader.Process is { } previous && !reader.AffectedProcesses.Contains(previous)) reader.AffectedProcesses = [previous];
        reader.Status = status; reader.Batch = null; reader.Process = null; reader.Identity = null;
        if (!keepQuery) reader.QueryAt = null;
        // Never join bytes/facts from before a failed correlation to a later binding.
        CloseReader(reader);
    }

    private void Publish(Input input, Reader[] readers, SessionLogCorrelationStatus fallback = SessionLogCorrelationStatus.Unknown, bool limited = false)
    {
        var now = _clock.GetUtcNow();
        var matches = new Dictionary<SessionProcessKey, Reader?>();
        var failures = new Dictionary<SessionProcessKey, SessionLogCorrelationStatus>();
        foreach (var reader in readers)
        {
            if (reader.Process is { } process && reader.Status == SessionLogCorrelationStatus.ResourceHolderObserved
                && reader.LeaseUntil > now && !matches.TryAdd(process, reader))
                matches[process] = null;

            foreach (var affected in reader.AffectedProcesses)
            {
                if (reader.Status == SessionLogCorrelationStatus.Ambiguous) failures[affected] = reader.Status;
                else failures.TryAdd(affected, reader.Status);
            }
        }

        var result = new List<ProcessLogDiagnostic>(input.Processes.Length);
        foreach (var process in input.Processes)
        {
            bool found = matches.TryGetValue(process, out var reader);
            var failure = failures.GetValueOrDefault(process, fallback);
            if (failure != SessionLogCorrelationStatus.Ambiguous && reader?.Batch is { } batch)
            {
                result.Add(new(process, SessionLogCorrelationStatus.ResourceHolderObserved, batch.Stream, batch,
                    reader.QueryUtc, reader.LeaseUntil));
                continue;
            }
            if (found && failure != SessionLogCorrelationStatus.Ambiguous)
                failure = reader is null ? SessionLogCorrelationStatus.Ambiguous : SessionLogCorrelationStatus.BudgetExceeded;
            result.Add(new(process, failure, null, null, now));
        }
        var launches = LinkLaunches(input, readers, matches, fallback == SessionLogCorrelationStatus.Unavailable);

        foreach (var reader in readers)
            if (reader.Process is { } process && reader.LeaseUntil > now
                && matches.TryGetValue(process, out var match) && match is null)
                Reset(reader, SessionLogCorrelationStatus.Ambiguous, keepQuery: true);

        Volatile.Write(ref _snapshot, new(Interlocked.Increment(ref _revision), now, result.AsReadOnly(), _candidates.Files.Count,
            limited || input.BudgetLimited) { Launches = Array.AsReadOnly(launches) });
    }

    private void CloseReader(Reader reader)
    {
        if (reader.Log is not { } log) return;
        log.Dispose();
        reader.Log = null;
        reader.Facts = null;
        reader.ReplayFloorOffset = 0;
        reader.UnreadBytes = false;
        _liveReaders--;
    }

    private void Remove(Reader reader) { CloseReader(reader); _readers.Remove(reader.Candidate.Path); }
    private void ClearReaders() { foreach (var reader in _readers.Values) CloseReader(reader); _readers.Clear(); Volatile.Write(ref _watched, []); }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _worker.ConfigureAwait(false); return; }
        _stop.Cancel(); _inputs.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); } finally { _stop.Dispose(); }
    }
}
