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

public sealed record SessionLogCorrelatorOptions(int MaximumReaders = 16, int MaximumCandidates = 64,
    int MaximumProcesses = 128, int ReadsPerPass = 4, int SharedReadBytes = 64 * 1024,
    int BytesPerReader = 16 * 1024, int ReplayFactsPerReader = 128)
{
    internal SessionLogCorrelatorOptions Validated()
    {
        if (MaximumReaders is < 1 or > 64 || MaximumCandidates < MaximumReaders || MaximumCandidates > 256
            || MaximumProcesses is < 1 or > 1024 || ReadsPerPass is < 1 or > 16
            || SharedReadBytes is < 1024 or > 1024 * 1024 || BytesPerReader is < 1024 or > 65536
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
        public long LastVisit;
        public long ReplayFloorOffset;
    }

    // A holder observation is trusted for this long. Holders are still re-queried on every visit made
    // 5 s or more after the last query (QueryDue), so the lease never extends without a fresh observation.
    // With 16 bound readers, one scout slot per pass leaves 3 visits per pass, so each bound reader is
    // revisited every 5-6 passes: 25 s covers passes of up to about 4 s (10 s lapsed at 2 s passes).
    // It stays under the 30 s lease cap that SessionEvidenceRegistry and SessionLogProjection enforce;
    // the 15 s display freshness check there is separate and does not discard facts.
    private static readonly TimeSpan HolderLease = TimeSpan.FromSeconds(25);
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
    private int _liveReaders, _disposed;
    private bool _scoutFirst;
    // Include the reader's bounded rewrite anchor in the shared content-read budget.
    private int ReaderByteBudget => Math.Max(1, Math.Min(_options.BytesPerReader, _options.SharedReadBytes) - 64);

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
                await PassAsync(input, _stop.Token).ConfigureAwait(false);
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

    private async Task PassAsync(Input input, CancellationToken ct)
    {
        if (_lastDiscovery is null || _clock.GetElapsedTime(_lastDiscovery.Value) >= TimeSpan.FromSeconds(5))
        {
            _lastDiscovery = _clock.GetTimestamp();
            _candidates = _probe.Discover(_options.MaximumCandidates, ct);
            var candidates = _candidates.Files.Take(_options.MaximumCandidates).ToArray();
            var keep = candidates.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var reader in _readers.Values.Where(reader => !keep.Contains(reader.Candidate.Path)
                && (reader.Log is null || reader.Process is not { } process || !input.Processes.Contains(process))).ToArray())
                Remove(reader);
            foreach (var candidate in candidates)
                if (!_readers.ContainsKey(candidate.Path))
                    _readers.Add(candidate.Path, new(candidate));
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
            Math.Min(_options.ReadsPerPass, _options.SharedReadBytes / Math.Min(_options.BytesPerReader, _options.SharedReadBytes)));
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
            reader.LastVisit = ++_visit;
            await ObserveAsync(reader, input, ct).ConfigureAwait(false);
        }
        limited |= _liveReaders == _options.MaximumReaders && readers.Any(static reader => reader.Process is not null && reader.Log is null);
        if (!input.Processes.SequenceEqual(Volatile.Read(ref _latest).Processes)) return;
        Publish(input, readers, _candidates.Unavailable ? SessionLogCorrelationStatus.Unavailable : SessionLogCorrelationStatus.Unknown, limited);
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
