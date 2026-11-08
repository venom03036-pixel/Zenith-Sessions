namespace Zenith.Sessions.Core;

public readonly record struct SessionProcessKey(string? HostId, int ProcessId, long ProcessStartUtcTicks)
{
    public bool IsValid => ProcessId > 0 && ProcessStartUtcTicks > 0 && ProcessStartUtcTicks <= DateTimeOffset.MaxValue.UtcTicks
        && (HostId is null || HostId.Length is > 0 and <= 100 && !HostId.Any(char.IsControl));
}

public readonly record struct ClientLogStreamId(Guid ReaderId, long Generation)
{
    public bool IsValid => ReaderId != Guid.Empty && Generation > 0;
}

public enum ClientLogFact { Joining, Connected, Teleporting, Disconnected, UserIdentityObserved, HumanCheck }
public enum ClientLogFormatSupport { Unverified, SanitizedRealFixtureVerified }
public enum ClientLogReadStatus { Ready, Missing, AccessDenied, IoFailure, DeadlineExceeded, Busy }
public enum ClientLogDiscontinuity { None, Initial, Replaced, TruncatedOrRewritten, Missing }

/// <summary>Allowlisted diagnostic values only. No raw line, path, URL, ticket or command line escapes a reader.</summary>
public sealed record ClientLogObservation(ClientLogStreamId Stream, long StartOffset, long EndOffset,
    DateTimeOffset? LoggedUtc, DateTimeOffset ReadUtc, ClientLogFact Fact, long? UserId = null,
    long? PlaceId = null, Guid? JobId = null, long? UniverseId = null,
    ClientLogFormatSupport FormatSupport = ClientLogFormatSupport.Unverified, int? DisconnectReason = null)
{
    /// <summary>Largest disconnect reason code kept. A longer or non-numeric reason is never stored.</summary>
    public const int MaximumDisconnectReason = 9999;
    /// <summary>The reason Roblox logs when the same account launched in another client and this one was ended.</summary>
    public const int AccountLaunchedElsewhereReason = 273;
    /// <summary>
    /// Reason 285 is the player's own leave; 273 is the same account launched elsewhere. Neither is a dropped connection,
    /// but only 285 is this player leaving: name 273 with <see cref="AccountLaunchedElsewhereReason"/> before using this.
    /// </summary>
    public static bool IsPlayerInitiated(int? reason) => reason is 285 or AccountLaunchedElsewhereReason;
    public bool PlayerInitiated => IsPlayerInitiated(DisconnectReason);
}

public sealed record ClientLogBatch(ClientLogStreamId Stream, ClientLogReadStatus Status,
    ClientLogDiscontinuity Discontinuity, long ReadOffset, long CompleteLineOffset, bool HasMore,
    int RejectedLines, IReadOnlyList<ClientLogObservation> Observations)
{
    public long ReadSequence { get; init; }
    /// <summary>End of the latest observation dropped by the producer's bounded replay window.</summary>
    public long ReplayFloorOffset { get; init; }
    public bool HasUnprocessedTail => HasMore || CompleteLineOffset < ReadOffset;
}

public enum ObservedSessionLifecycle { Unknown, Joining, Connected, Teleporting, Disconnected, Exited }
public enum SessionEvidenceConfidence { Unknown, UnverifiedDiagnostic, CorroboratedDiagnostic }
public enum SessionLogCorrelationKind { TimingOnly, ManualLabel, IndependentlyCorroborated, ResourceHolderDiagnostic }

/// <summary>The caller must obtain independent process-instance/log ownership evidence. This type does not create it.</summary>
public sealed record SessionLogCorroboration(SessionProcessKey Process, ClientLogStreamId Stream,
    SessionLogCorrelationKind Kind, Guid EvidenceId, DateTimeOffset CorroboratedUtc, DateTimeOffset? LeaseExpiresUtc = null);
public sealed record ObservedSessionDestination(long PlaceId, Guid JobId, long? UniverseId, DateTimeOffset ObservedUtc);
public sealed record SessionEvidenceSnapshot(Guid SessionId, SessionProcessKey Process, nint Window,
    ObservedSessionLifecycle Lifecycle, long? ObservedUserId, ObservedSessionDestination? CurrentDestination,
    ObservedSessionDestination? LastKnownDestination, ClientLogStreamId? Stream,
    SessionEvidenceConfidence Confidence, DateTimeOffset ObservedUtc, bool Ambiguous,DateTimeOffset? UserIdentityObservedUtc=null,
    SessionLogCorrelationKind? CorrelationKind=null, DateTimeOffset? CorrelationExpiresUtc=null,
    long? LastKnownUserId=null, DateTimeOffset? LastKnownUserIdentityUtc=null)
{
    // A diagnostic log, even with corroborated ownership, is never authorization or an input/launch permit.
    public bool CanAuthorizeAccountAction => false;
    public bool AwaitingLogTail { get; init; }
    /// <summary>The first numeric reason logged for the current disconnect; null outside Disconnected.</summary>
    public int? DisconnectReason { get; init; }
    public bool PlayerInitiatedDisconnect => ClientLogObservation.IsPlayerInitiated(DisconnectReason);
    /// <summary>When the client last logged a human-check request since its latest join; display only.</summary>
    public DateTimeOffset? HumanCheckObservedUtc { get; init; }
    /// <summary>
    /// The bound log still says Connected, but its destination fact is older than the registry's evidence age, so it
    /// moved to <see cref="LastKnownDestination"/> and <see cref="CurrentDestination"/> is null (owner decision
    /// 2026-11-08, R-instances-2). Display may show that destination with how long ago it was last confirmed; it is
    /// not a current claim, so nothing that needs fresh evidence (Join same server, Copy) may use it. It ends with
    /// the next join, teleport, disconnect or exit, a lost or lapsed binding, or an unread log tail.
    /// </summary>
    public bool ConnectedToLastKnownDestination => Lifecycle == ObservedSessionLifecycle.Connected && !Ambiguous && !AwaitingLogTail
        && CurrentDestination is null && LastKnownDestination is not null;
}

/// <summary>
/// Bounded, synchronous evidence reducer; no discovery, timers, credentials or platform calls. Requested launch
/// targets belong to the launch queue, never these observed fields. Every mutation is serialized by one lock.
/// </summary>
public sealed class SessionEvidenceRegistry
{
    private readonly int _maximum;
    private readonly TimeSpan _maximumAge;
    private readonly object _gate = new();
    private readonly Dictionary<SessionProcessKey, Entry> _entries = [];
    private readonly Dictionary<ClientLogStreamId, SessionProcessKey?> _owners = [];
    private readonly Dictionary<Guid, long> _generations = [];
    private sealed class Entry(SessionEvidenceSnapshot snapshot)
    {
        public SessionEvidenceSnapshot Snapshot = snapshot;
        public ObservedSessionDestination? Pending;
        public long? StreamUserId;
        public long LastOffset = -1;
        public long ReadOffset, CompleteLineOffset, ReadSequence, ReplayFloorOffset;
        public bool AwaitingLogTail;
        public DateTimeOffset LastEventUtc = DateTimeOffset.MinValue;
        public DateTimeOffset InvalidatedReadUtc = DateTimeOffset.MinValue;
    }

    public SessionEvidenceRegistry(int maximumSessions = 256, TimeSpan? maximumEvidenceAge = null)
    {
        _maximum = maximumSessions is > 0 and <= 4096 ? maximumSessions : throw new ArgumentOutOfRangeException(nameof(maximumSessions));
        _maximumAge = maximumEvidenceAge ?? TimeSpan.FromMinutes(5);
        if (_maximumAge <= TimeSpan.Zero || _maximumAge > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(maximumEvidenceAge));
    }

    public bool RegisterProcess(SessionProcessKey key, DateTimeOffset observedUtc, nint window = 0)
    {
        if (!key.IsValid || observedUtc.UtcTicks < key.ProcessStartUtcTicks) return false;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var known))
            {
                if (known.Snapshot.Lifecycle == ObservedSessionLifecycle.Exited || observedUtc < known.Snapshot.ObservedUtc) return false;
                known.Snapshot = known.Snapshot with { Window = window, ObservedUtc = observedUtc };
                return true;
            }
            if (_entries.Count >= _maximum) return false;
            if (_entries.Keys.Any(current => current.HostId == key.HostId && current.ProcessId == key.ProcessId
                && current.ProcessStartUtcTicks > key.ProcessStartUtcTicks)) return false;
            foreach (var older in _entries.Values)
                if (older.Snapshot.Process.HostId == key.HostId && older.Snapshot.Process.ProcessId == key.ProcessId)
                    Invalidate(older, observedUtc, ObservedSessionLifecycle.Exited, false);
            _entries.Add(key, new(new(Guid.NewGuid(), key, window, ObservedSessionLifecycle.Unknown, null, null, null,
                null, SessionEvidenceConfidence.Unknown, observedUtc, false)));
            return true;
        }
    }

    public bool CorroborateLog(SessionLogCorroboration evidence, DateTimeOffset now)
    {
        if (!evidence.Process.IsValid || !evidence.Stream.IsValid || evidence.EvidenceId == Guid.Empty
            || evidence.Kind is not (SessionLogCorrelationKind.IndependentlyCorroborated or SessionLogCorrelationKind.ResourceHolderDiagnostic)
            || !Fresh(evidence.CorroboratedUtc, now)
            || evidence.CorroboratedUtc.UtcTicks < evidence.Process.ProcessStartUtcTicks) return false;
        if (evidence.Kind == SessionLogCorrelationKind.ResourceHolderDiagnostic
            && (evidence.LeaseExpiresUtc is not { } expiry || expiry <= now || expiry <= evidence.CorroboratedUtc
                || expiry - evidence.CorroboratedUtc > TimeSpan.FromSeconds(30))) return false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(evidence.Process, out var entry) || entry.Snapshot.Lifecycle == ObservedSessionLifecycle.Exited
                || now < entry.Snapshot.ObservedUtc) return false;
            bool knownGeneration = _generations.TryGetValue(evidence.Stream.ReaderId, out long generation);
            if (knownGeneration && evidence.Stream.Generation < generation) return false;
            bool advancedGeneration = knownGeneration && evidence.Stream.Generation > generation;
            if (!MakeReaderRoom(evidence.Stream.ReaderId)) return false;
            if (_owners.TryGetValue(evidence.Stream, out var owner))
            {
                if (owner is null) return false;
                if (owner != evidence.Process)
                {
                    Invalidate(entry, now, ObservedSessionLifecycle.Unknown, true);
                    if (_entries.TryGetValue(owner.Value, out var previous)) Invalidate(previous, now, ObservedSessionLifecycle.Unknown, true);
                    _owners[evidence.Stream] = null;
                    return false;
                }
            }
            if (entry.Snapshot.Stream is { } current)
            {
                if (current.ReaderId != evidence.Stream.ReaderId)
                {
                    Invalidate(entry, now, ObservedSessionLifecycle.Unknown, true);
                    _owners[current] = null;
                    if (_owners.Count < _maximum * 2) _owners[evidence.Stream] = null;
                    return false;
                }
                if (evidence.Stream.Generation < current.Generation) return false;
                if (current == evidence.Stream)
                {
                    if (entry.Snapshot.Ambiguous) return false;
                    // A holder query cannot upgrade a stream to independently proven writer ownership.
                    if (entry.Snapshot.CorrelationKind == SessionLogCorrelationKind.ResourceHolderDiagnostic
                        && evidence.Kind != SessionLogCorrelationKind.ResourceHolderDiagnostic) return false;
                    entry.Snapshot = entry.Snapshot with { CorrelationKind = evidence.Kind, CorrelationExpiresUtc = evidence.LeaseExpiresUtc,
                        Confidence = evidence.Kind == SessionLogCorrelationKind.ResourceHolderDiagnostic
                            && entry.Snapshot.Confidence != SessionEvidenceConfidence.Unknown
                            ? SessionEvidenceConfidence.UnverifiedDiagnostic : entry.Snapshot.Confidence };
                    return true;
                }
                _owners.Remove(current);
                Invalidate(entry, now, ObservedSessionLifecycle.Unknown, false);
            }
            if (!MakeOwnerRoom(evidence.Stream)) return false;
            _owners[evidence.Stream] = evidence.Process;
            _generations[evidence.Stream.ReaderId] = evidence.Stream.Generation;
            if (advancedGeneration) entry.InvalidatedReadUtc = DateTimeOffset.MinValue;
            // Correlation freshness and event freshness are separate. Querying a holder after a
            // read must not make its just-read events appear older than the binding itself.
            entry.Snapshot = entry.Snapshot with { Stream = evidence.Stream, Ambiguous = false,
                CorrelationKind = evidence.Kind, CorrelationExpiresUtc = evidence.LeaseExpiresUtc };
            return true;
        }
    }

    /// <summary>Explicit loss of a diagnostic relationship, without pretending the process exited.</summary>
    public bool InvalidateLog(SessionProcessKey process, ClientLogStreamId? expectedStream, DateTimeOffset now, bool ambiguous = false)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(process, out var entry) || now < entry.Snapshot.ObservedUtc
                || expectedStream is { } expected && entry.Snapshot.Stream != expected) return false;
            if (entry.Snapshot.Stream is { } current) _owners.Remove(current);
            Invalidate(entry, now, ObservedSessionLifecycle.Unknown, ambiguous);
            return true;
        }
    }

    public bool Apply(ClientLogObservation observation, DateTimeOffset now)
    {
        // Reading old, undated bytes now does not make them current positive evidence.
        if(observation.LoggedUtc is null && observation.Fact is (ClientLogFact.Joining or ClientLogFact.Connected
            or ClientLogFact.UserIdentityObserved or ClientLogFact.HumanCheck)) return false;
        var time = observation.LoggedUtc ?? observation.ReadUtc;
        if (!observation.Stream.IsValid || observation.StartOffset < 0 || observation.EndOffset <= observation.StartOffset
            || !Fresh(observation.ReadUtc, now) || !Fresh(time, now) || time-observation.ReadUtc>TimeSpan.FromSeconds(2)
            || observation.UserId is <= 0 || observation.PlaceId is <= 0 || observation.UniverseId is <= 0
            || observation.JobId == Guid.Empty || !Enum.IsDefined(observation.Fact) || !Enum.IsDefined(observation.FormatSupport)
            || observation.DisconnectReason is { } code && (observation.Fact != ClientLogFact.Disconnected
                || code is <= 0 or > ClientLogObservation.MaximumDisconnectReason)) return false;
        if (observation.Fact == ClientLogFact.UserIdentityObserved
            ? observation.UserId is null || observation.PlaceId is not null || observation.JobId is not null || observation.UniverseId is not null
            : observation.Fact == ClientLogFact.Joining
                ? observation.UserId is not null || observation.PlaceId is null || observation.JobId is null
                : observation.UserId is not null || observation.PlaceId is not null || observation.JobId is not null || observation.UniverseId is not null) return false;
        lock (_gate)
        {
            if (!_owners.TryGetValue(observation.Stream, out var owner) || owner is null
                || !_entries.TryGetValue(owner.Value, out var entry) || entry.Snapshot.Stream != observation.Stream
                || entry.Snapshot.Ambiguous || entry.Snapshot.Lifecycle == ObservedSessionLifecycle.Exited
                || observation.ReadUtc < entry.InvalidatedReadUtc
                || time.UtcTicks < owner.Value.ProcessStartUtcTicks || time < entry.LastEventUtc
                || observation.StartOffset < entry.LastOffset) return false;
            if (observation.Fact == ClientLogFact.UserIdentityObserved && observation.UserId is null
                || observation.Fact == ClientLogFact.Joining && (observation.PlaceId is null || observation.JobId is null)) return false;
            if (entry.StreamUserId is { } previousUser && observation.UserId is { } nextUser && previousUser != nextUser)
            {
                Invalidate(entry, now, ObservedSessionLifecycle.Unknown, true);
                _owners[observation.Stream] = null;
                return false;
            }
            // Output is script-writable. A second join while connected, without a transition, is conflicting data
            // while the connection's own fact is within the evidence age. After that the state had always been
            // forgotten, so such a join is a new join; a destination kept for display does not change that.
            if (observation.Fact == ClientLogFact.Joining && entry.Snapshot.Lifecycle == ObservedSessionLifecycle.Connected
                && entry.Snapshot.CurrentDestination is { } live && Fresh(live.ObservedUtc, now))
            {
                Invalidate(entry, now, ObservedSessionLifecycle.Unknown, true);
                _owners[observation.Stream] = null;
                return false;
            }
            if (entry.Snapshot.CorrelationExpiresUtc is { } expires && now >= expires)
            {
                _owners.Remove(observation.Stream);
                Invalidate(entry, now, ObservedSessionLifecycle.Unknown, false);
                return false;
            }
            entry.LastOffset = observation.EndOffset;
            entry.LastEventUtc = time;
            entry.StreamUserId ??= observation.UserId;
            var state = entry.Snapshot;
            var confidence = state.CorrelationKind != SessionLogCorrelationKind.ResourceHolderDiagnostic
                && observation.FormatSupport == ClientLogFormatSupport.SanitizedRealFixtureVerified
                ? SessionEvidenceConfidence.CorroboratedDiagnostic : SessionEvidenceConfidence.UnverifiedDiagnostic;
            if (observation.Fact == ClientLogFact.Joining)
            {
                RememberCurrent(entry);
                entry.Pending = new(observation.PlaceId!.Value, observation.JobId!.Value, observation.UniverseId, time);
                // A new join supersedes the previous disconnect reason and any human check that preceded it.
                state = entry.Snapshot with { Lifecycle = ObservedSessionLifecycle.Joining, CurrentDestination = null,
                    DisconnectReason = null, HumanCheckObservedUtc = null };
            }
            else if (observation.Fact == ClientLogFact.Connected && entry.Pending is { } pending)
            {
                state = state with { Lifecycle = ObservedSessionLifecycle.Connected,
                    CurrentDestination = pending with { ObservedUtc = time }, DisconnectReason = null };
                entry.Pending = null;
            }
            else if (observation.Fact is ClientLogFact.Teleporting or ClientLogFact.Disconnected)
            {
                // One disconnect logs several lines; the first that names a reason code describes it.
                int? reason = observation.Fact == ClientLogFact.Teleporting ? null
                    : state.Lifecycle == ObservedSessionLifecycle.Disconnected ? state.DisconnectReason ?? observation.DisconnectReason
                    : observation.DisconnectReason;
                RememberCurrent(entry);
                entry.Pending = null;
                state = entry.Snapshot with { CurrentDestination = null, DisconnectReason = reason,
                    Lifecycle = observation.Fact == ClientLogFact.Teleporting
                    ? ObservedSessionLifecycle.Teleporting : ObservedSessionLifecycle.Disconnected };
            }
            else if (observation.Fact == ClientLogFact.HumanCheck)
                state = state with { HumanCheckObservedUtc = time };
            entry.Snapshot = state with { ObservedUserId = observation.UserId ?? state.ObservedUserId,
                UserIdentityObservedUtc=observation.UserId is not null?time:state.UserIdentityObservedUtc,
                LastKnownUserId=observation.UserId??state.LastKnownUserId,
                LastKnownUserIdentityUtc=observation.UserId is not null?time:state.LastKnownUserIdentityUtc,
                Confidence = confidence, ObservedUtc = observation.ReadUtc };
            return true;
        }
    }

    /// <summary>Discontinuity invalidates ownership; a new generation must be independently corroborated again.</summary>
    public int ApplyBatch(ClientLogBatch batch, DateTimeOffset now)
    {
        if (!batch.Stream.IsValid || batch.Status == ClientLogReadStatus.Busy) return 0;
        lock (_gate)
        {
            bool knownGeneration = _generations.TryGetValue(batch.Stream.ReaderId, out long highest);
            if (knownGeneration && batch.Stream.Generation < highest) return 0;
            bool ready = batch.Status == ClientLogReadStatus.Ready;
            Entry? target = null;
            if (ready)
            {
                if (_owners.TryGetValue(batch.Stream, out var owner) && owner is { } key
                    && _entries.TryGetValue(key, out var entry) && entry.Snapshot.Stream == batch.Stream)
                {
                    target = entry;
                    if (batch.ReadOffset < target.ReadOffset || batch.ReadOffset < target.LastOffset
                        || batch.CompleteLineOffset < target.CompleteLineOffset || batch.ReplayFloorOffset < target.ReplayFloorOffset
                        || batch.ReadSequence < target.ReadSequence
                        || batch.ReadSequence > 0 && batch.ReadSequence == target.ReadSequence) return 0;
                }
                if (!ValidCoverage(batch))
                {
                    // A newer batch for an owned stream that contradicts its own coverage may hide a disconnect or an
                    // identity change in the bytes it claims. Nothing is applied or advanced, but current claims are
                    // withheld (as for an unread tail) until a consistent read advances past this point.
                    if (target is not null) target.AwaitingLogTail = true;
                    return 0;
                }
            }
            bool advancedGeneration = knownGeneration && batch.Stream.Generation > highest;
            if (advancedGeneration)
                _generations[batch.Stream.ReaderId] = batch.Stream.Generation;
            if (advancedGeneration || !ready)
                foreach (var entry in _entries.Values)
                    if (entry.Snapshot.Stream is { } current && current.ReaderId == batch.Stream.ReaderId
                        && batch.Stream.Generation >= current.Generation && (!ready || batch.Stream != current))
                    {
                        _owners.Remove(current);
                        Invalidate(entry, now, ObservedSessionLifecycle.Unknown, false);
                    }
            if (target is null) return 0;

            // A missed replay window may contain a disconnect or identity conflict. Discard the
            // accumulated current facts and rebuild only from the remaining ordered observations.
            if (batch.ReplayFloorOffset > target.CompleteLineOffset)
                ClearCurrentFacts(target, ObservedSessionLifecycle.Unknown);

            bool advanced = batch.ReadOffset > target.ReadOffset || batch.ReadSequence > target.ReadSequence;
            target.AwaitingLogTail = batch.HasUnprocessedTail || target.AwaitingLogTail && !advanced;
            target.ReadOffset = batch.ReadOffset;
            target.CompleteLineOffset = batch.CompleteLineOffset;
            target.ReadSequence = batch.ReadSequence;
            target.ReplayFloorOffset = batch.ReplayFloorOffset;
            int applied = 0;
            foreach (var observation in batch.Observations)
                if (Apply(observation, now)) applied++;
            return applied;
        }
    }

    private static bool ValidCoverage(ClientLogBatch batch)
    {
        if (batch.ReadSequence < 0 || batch.ReplayFloorOffset < 0 || batch.CompleteLineOffset < batch.ReplayFloorOffset
            || batch.ReadOffset < batch.CompleteLineOffset || batch.RejectedLines < 0
            || batch.Observations is null || batch.Observations.Count > 1024) return false;
        long offset = batch.ReplayFloorOffset;
        foreach (var observation in batch.Observations)
        {
            if (observation is null || observation.Stream != batch.Stream || observation.StartOffset < offset
                || observation.EndOffset <= observation.StartOffset || observation.EndOffset > batch.CompleteLineOffset) return false;
            offset = observation.EndOffset;
        }
        return true;
    }

    public bool MarkExited(SessionProcessKey key, DateTimeOffset observedUtc)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || observedUtc < entry.Snapshot.ObservedUtc) return false;
            Invalidate(entry, observedUtc, ObservedSessionLifecycle.Exited, false);
            return true;
        }
    }

    public bool Remove(SessionProcessKey key)
    {
        lock (_gate)
        {
            if (!_entries.Remove(key)) return false;
            foreach (var stream in _owners.Where(p => p.Value == key).Select(p => p.Key).ToArray()) _owners.Remove(stream);
            return true;
        }
    }

    public IReadOnlyList<SessionEvidenceSnapshot> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach(var entry in _entries.Values)
            {
                if (entry.Snapshot.CorrelationExpiresUtc is { } expires && now >= expires)
                {
                    if (entry.Snapshot.Stream is { } stream) _owners.Remove(stream);
                    Invalidate(entry, now, ObservedSessionLifecycle.Unknown, false);
                    continue;
                }
                // Process liveness, a new username line or unrelated log activity must not
                // renew an old server claim. Each kind of evidence expires independently.
                if(entry.Pending is { } pending && !Fresh(pending.ObservedUtc,now))
                    Invalidate(entry,now,ObservedSessionLifecycle.Unknown,false);
                else
                {
                    // An aged Connected destination becomes last-known while the same bound log still says Connected
                    // (ConnectedToLastKnownDestination). The binding, lifecycle and read position are kept, so a later
                    // transition is still read in order; the claim itself is not renewed. Owner decision 2026-11-08.
                    if(entry.Snapshot.CurrentDestination is { } destination && !Fresh(destination.ObservedUtc,now))
                    { RememberCurrent(entry); entry.Snapshot=entry.Snapshot with {CurrentDestination=null}; }
                    if(entry.Snapshot.ObservedUserId is not null && (entry.Snapshot.UserIdentityObservedUtc is not { } identityTime || !Fresh(identityTime,now)))
                        entry.Snapshot=entry.Snapshot with {ObservedUserId=null,UserIdentityObservedUtc=null,Confidence=SessionEvidenceConfidence.Unknown};
                }
            }
            return Array.AsReadOnly(_entries.Values.Select(static entry => entry.AwaitingLogTail
                ? entry.Snapshot with
                {
                    AwaitingLogTail = true, Lifecycle = ObservedSessionLifecycle.Unknown,
                    LastKnownDestination = entry.Snapshot.CurrentDestination ?? entry.Snapshot.LastKnownDestination,
                    CurrentDestination = null, ObservedUserId = null, UserIdentityObservedUtc = null,
                    DisconnectReason = null, HumanCheckObservedUtc = null
                }
                : entry.Snapshot).ToArray());
        }
    }

    private bool Fresh(DateTimeOffset time, DateTimeOffset now) => time-now<=TimeSpan.FromSeconds(2) && now-time<=_maximumAge;
    private bool MakeReaderRoom(Guid reader)
    {
        if (_generations.ContainsKey(reader) || _generations.Count < _maximum * 2) return true;
        // Reader IDs rotate when files are replaced. Retired fences must not exhaust a long-lived
        // registry; active stream generations remain pinned. A late batch still has no owner.
        foreach (var retired in _generations.Keys)
        {
            if (_entries.Values.Any(entry => entry.Snapshot.Stream?.ReaderId == retired)) continue;
            _generations.Remove(retired);
            foreach (var old in _owners.Keys.Where(stream => stream.ReaderId == retired).ToArray()) _owners.Remove(old);
            return true;
        }
        return false;
    }
    private bool MakeOwnerRoom(ClientLogStreamId stream)
    {
        if (_owners.ContainsKey(stream) || _owners.Count < _maximum * 2) return true;
        foreach (var retired in _owners.Keys)
        {
            if (_entries.Values.Any(entry => entry.Snapshot.Stream == retired)) continue;
            _owners.Remove(retired);
            return true;
        }
        return false;
    }
    private static void RememberCurrent(Entry entry)
    {
        if (entry.Snapshot.CurrentDestination is { } current) entry.Snapshot = entry.Snapshot with { LastKnownDestination = current };
    }
    private static void ClearCurrentFacts(Entry entry, ObservedSessionLifecycle lifecycle)
    {
        RememberCurrent(entry);
        entry.Pending = null;
        entry.Snapshot = entry.Snapshot with { Lifecycle = lifecycle, CurrentDestination = null, ObservedUserId = null,
            UserIdentityObservedUtc = null, Confidence = SessionEvidenceConfidence.Unknown,
            DisconnectReason = null, HumanCheckObservedUtc = null };
    }

    private static void Invalidate(Entry entry, DateTimeOffset time, ObservedSessionLifecycle lifecycle, bool ambiguous)
    {
        ClearCurrentFacts(entry, lifecycle);
        entry.StreamUserId = null;
        entry.LastOffset = -1; entry.LastEventUtc = DateTimeOffset.MinValue;
        entry.ReadOffset = entry.CompleteLineOffset = entry.ReadSequence = entry.ReplayFloorOffset = 0;
        entry.AwaitingLogTail = false;
        entry.InvalidatedReadUtc = time;
        entry.Snapshot = entry.Snapshot with { Stream = null, ObservedUtc = time, Ambiguous = ambiguous,
            CorrelationKind=null,CorrelationExpiresUtc=null };
    }
}
