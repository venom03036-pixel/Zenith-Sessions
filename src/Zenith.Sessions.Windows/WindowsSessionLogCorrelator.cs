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
    IReadOnlyList<ProcessLogDiagnostic> Sessions, int CandidateCount, bool BudgetLimited);

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
/// </summary>
public sealed class WindowsSessionLogCorrelator : IAsyncDisposable
{
    private sealed record Input(SessionProcessKey[] Processes, bool BudgetLimited);
    private sealed class Reader(ClientLogCandidate candidate)
    {
        public readonly ClientLogCandidate Candidate = candidate;
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
    private readonly SessionLogCorrelatorOptions _options;
    private readonly TimeProvider _clock;
    private readonly IClientLogProbe _probe;
    private readonly Channel<Input> _inputs = Channel.CreateBounded<Input>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Reader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _worker;
    private Input _latest = new([], false);
    private SessionLogDiscoverySnapshot _snapshot = new(0, DateTimeOffset.MinValue, Array.Empty<ProcessLogDiagnostic>(), 0, false);
    private ClientLogCandidates _candidates = new(Array.Empty<ClientLogCandidate>(), false);
    private long? _lastPass, _lastDiscovery;
    private long _revision, _visit;
    private int _liveReaders, _disposed, _consecutiveFailures;
    private bool _scoutFirst;
    // One turn reserves one full per-reader read, which includes the reader's bounded rewrite anchor.
    private int TurnBytes => Math.Min(_options.BytesPerReader, _options.SharedReadBytes);
    private int ReaderByteBudget => Math.Max(1, TurnBytes - 64);

    public WindowsSessionLogCorrelator(SessionLogCorrelatorOptions? options = null, TimeProvider? timeProvider = null)
        : this(new WindowsClientLogProbe(), options, timeProvider) { }

    internal WindowsSessionLogCorrelator(IClientLogProbe probe, SessionLogCorrelatorOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = (options ?? new()).Validated(); _clock = timeProvider ?? TimeProvider.System;
        _probe = probe; _worker = Task.Run(RunAsync);
    }

    public SessionLogDiscoverySnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Offer(DesktopSnapshot snapshot, string? hostId = null)
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
        var input = new Input(keys.ToArray(), limited);
        Volatile.Write(ref _latest, input); _inputs.Writer.TryWrite(input);
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
                try
                {
                    var discoveryFailure = await PassAsync(input, _stop.Token).ConfigureAwait(false);
                    if (discoveryFailure is null) _consecutiveFailures = 0;
                    // The pass already kept every reader and published. A failed discovery still counts as a failed
                    // pass, so a persistent probe fault ends this owner on the fifth in a row and surfaces at DisposeAsync.
                    else if (++_consecutiveFailures == MaximumConsecutivePassFailures) discoveryFailure.Throw();
                }
                catch (Exception) when (!_stop.IsCancellationRequested && _consecutiveFailures < MaximumConsecutivePassFailures - 1)
                {
                    // A pass that failed after touching a reader is not terminal: drop every cursor, mark all rows
                    // Unavailable, and rediscover on the next offer, which the 1 s pass gate already rate-limits. A failed
                    // discovery never reaches here (PassAsync keeps the readers). Repeated failures still end this owner.
                    _consecutiveFailures++;
                    ClearReaders(); _candidates = new(Array.Empty<ClientLogCandidate>(), false); _lastDiscovery = null;
                    Publish(Volatile.Read(ref _latest), [], SessionLogCorrelationStatus.Unavailable);
                }
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
            if (reader.Identity is { } identity && before != identity) Reset(reader, SessionLogCorrelationStatus.SourceChanged);
            bool refreshed = QueryDue(reader);
            if (refreshed)
            {
                reader.QueryAt = _clock.GetTimestamp();
                var result = _probe.QueryHolders(reader.Candidate.Path, ct);
                reader.QueryUtc = _clock.GetUtcNow(); reader.LeaseUntil = reader.QueryUtc + HolderLease;
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

        foreach (var reader in readers)
            if (reader.Process is { } process && reader.LeaseUntil > now
                && matches.TryGetValue(process, out var match) && match is null)
                Reset(reader, SessionLogCorrelationStatus.Ambiguous, keepQuery: true);

        Volatile.Write(ref _snapshot, new(Interlocked.Increment(ref _revision), now, result.AsReadOnly(), _candidates.Files.Count,
            limited || input.BudgetLimited));
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
    private void ClearReaders() { foreach (var reader in _readers.Values) CloseReader(reader); _readers.Clear(); }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _worker.ConfigureAwait(false); return; }
        _stop.Cancel(); _inputs.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); } finally { _stop.Dispose(); }
    }
}
