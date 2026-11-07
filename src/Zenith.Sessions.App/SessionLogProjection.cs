using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>Presentation reducer only. Observations never become account authorization or launch targets.</summary>
public sealed class SessionLogProjection
{
    private readonly SessionEvidenceRegistry _registry = new(maximumSessions: 128);
    private readonly HashSet<SessionProcessKey> _processes = [];
    private readonly Dictionary<SessionProcessKey, ProcessLogDiagnostic> _diagnostics = [];
    private long _revision = -1;

    public void Update(IReadOnlyList<SessionObservation> processes, SessionLogDiscoverySnapshot? logs, DateTimeOffset now)
    {
        var current = new HashSet<SessionProcessKey>();
        foreach (var process in processes.Take(128))
        {
            var key = new SessionProcessKey(null, process.ProcessId, process.ProcessStartUtcTicks);
            if (!key.IsValid || process.Problem is not null || process.ObservedUtc.UtcTicks < key.ProcessStartUtcTicks) continue;
            current.Add(key);
            if (!_processes.Contains(key) && _registry.RegisterProcess(key, process.ObservedUtc, process.Window)) _processes.Add(key);
        }
        foreach (var key in _processes.Where(key => !current.Contains(key)).ToArray())
        { _registry.Remove(key); _processes.Remove(key); _diagnostics.Remove(key); }
        if (logs is null || logs.Revision <= _revision) return;
        _revision = logs.Revision;
        foreach (var diagnostic in logs.Sessions.Take(128))
        {
            if (!_processes.Contains(diagnostic.Process) || diagnostic.ObservedUtc > now.AddSeconds(2)) continue;
            if (_diagnostics.TryGetValue(diagnostic.Process, out var prior) && prior.SourceStamp is { } previous
                && diagnostic.SourceStamp is { } replacement && previous != replacement)
                _registry.InvalidateLog(diagnostic.Process, previous, diagnostic.ObservedUtc);
            _diagnostics[diagnostic.Process] = diagnostic;
            if (diagnostic.Correlation == SessionLogCorrelationStatus.ResourceHolderObserved
                && diagnostic.SourceStamp is { } stream && diagnostic.Batch is { Status: ClientLogReadStatus.Ready } batch
                && batch.Stream == stream && diagnostic.LeaseExpiresUtc is { } expiry && expiry > now)
            {
                if (_registry.CorroborateLog(new(diagnostic.Process, stream, SessionLogCorrelationKind.ResourceHolderDiagnostic,
                    stream.ReaderId, diagnostic.ObservedUtc, expiry), now)) _registry.ApplyBatch(batch, now);
                else _registry.InvalidateLog(diagnostic.Process, null, diagnostic.ObservedUtc, ambiguous: true);
            }
            else
                _registry.InvalidateLog(diagnostic.Process, null, diagnostic.ObservedUtc,
                    diagnostic.Correlation == SessionLogCorrelationStatus.Ambiguous);
        }
    }

    public IReadOnlyList<SessionEvidenceSnapshot> EvidenceSnapshot(DateTimeOffset now) => _registry.Snapshot(now);

    /// <summary>Evidence-only views. They never offer the reported destination for Join same or Copy.</summary>
    public IReadOnlyDictionary<SessionProcessKey, SessionLogView> Snapshot(DateTimeOffset now) => Snapshot(now, null, null);

    /// <summary>
    /// Views for the visible rows. Join same game/server and Copy are offered only while the click-time evidence holds:
    /// a complete, fresh process scan still lists this exact process and the latest log diagnostic is the applied one.
    /// A failed, partial or stale scan keeps last-known rows on screen, so their reported actions turn off. The click
    /// still re-checks; a retained row is never the authority.
    /// </summary>
    public IReadOnlyDictionary<SessionProcessKey, SessionLogView> Snapshot(DateTimeOffset now, DesktopSnapshot? latestDesktop,
        SessionLogDiscoverySnapshot? latestLogs)
    {
        var result = new Dictionary<SessionProcessKey, SessionLogView>();
        foreach (var evidence in _registry.Snapshot(now))
        {
            _diagnostics.TryGetValue(evidence.Process, out var diagnostic);
            var view = SessionLogView.Create(evidence, diagnostic, now);
            if (view.HasReportedDestination && diagnostic is not null && HasCurrentProcess(evidence.Process, latestDesktop, now)
                && CurrentDiagnostic(evidence.Process, latestLogs, now) is { } current && current == diagnostic)
                view = view with { ReportedEvidenceCurrent = true };
            result.Add(evidence.Process, view);
        }
        return result;
    }
    /// <summary>Click-time link for a client's current reported destination; with includeServer false only the place is linked.</summary>
    public string? BuildCurrentLink(SessionProcessKey process, DesktopSnapshot? latestDesktop,
        SessionLogDiscoverySnapshot? latestLogs, DateTimeOffset now, bool includeServer = true)
    {
        // A cached visible row cannot establish that this process is still in a complete scan.
        // Validate the full click-time inventory before changing any usable registry state.
        if (!HasCurrentProcess(process, latestDesktop, now)
            || CurrentDiagnostic(process, latestLogs, now) is not { } diagnostic) return null;

        Update(latestDesktop!.Sessions, latestLogs, now);
        if (!_diagnostics.TryGetValue(process, out var applied) || applied != diagnostic) return null;
        if (!Snapshot(now).TryGetValue(process, out var view) || view.ReportedDestination is not { } destination) return null;
        return (includeServer ? destination : new RobloxRequestedDestination(destination.PlaceId)).ToWebStartUri().AbsoluteUri;
    }

    private static bool HasCurrentProcess(SessionProcessKey process, DesktopSnapshot? desktop, DateTimeOffset now)
    {
        if (!process.IsValid || process.HostId is not null || now == default
            || desktop is not { Problem: null, ProcessInventoryComplete: true, ProcessRevision: > 0 }
            || desktop.ProcessObservedUtc is not { } observed
            || !Fresh(observed, now, NativeTrialEvidence.MaximumProcessAge)
            || desktop.Sessions is null || desktop.Sessions.Count > NativeTrialEvidence.MaximumObservations) return false;

        var pids = new HashSet<int>();
        bool found = false;
        foreach (var row in desktop.Sessions)
        {
            if (row is null || row.Problem is not null || !pids.Add(row.ProcessId)
                || !new SessionProcessKey(null, row.ProcessId, row.ProcessStartUtcTicks).IsValid
                || row.ProcessStartUtcTicks > row.ObservedUtc.UtcTicks || row.ObservedUtc > observed
                || !Fresh(row.ObservedUtc, now, NativeTrialEvidence.MaximumProcessAge)) return false;
            if (row.ProcessId == process.ProcessId)
            {
                if (row.ProcessStartUtcTicks != process.ProcessStartUtcTicks) return false;
                found = true;
            }
        }
        return found;
    }

    private ProcessLogDiagnostic? CurrentDiagnostic(SessionProcessKey process, SessionLogDiscoverySnapshot? logs, DateTimeOffset now)
    {
        if (logs is null || logs.Revision <= 0 || logs.Revision < _revision || logs.Sessions is null
            || logs.Sessions.Count > NativeTrialEvidence.MaximumLogViews
            || !Fresh(logs.ObservedUtc, now, NativeTrialEvidence.MaximumLogAge)) return null;

        ProcessLogDiagnostic? current = null;
        foreach (var diagnostic in logs.Sessions)
        {
            if (diagnostic is null) return null;
            if (diagnostic.Process != process) continue;
            if (current is not null) return null;
            current = diagnostic;
        }
        if (current is not { Correlation: SessionLogCorrelationStatus.ResourceHolderObserved,
                SourceStamp: { IsValid: true } stream, Batch: { Status: ClientLogReadStatus.Ready } batch,
                LeaseExpiresUtc: { } expiry }
            || batch.Stream != stream || batch.Observations is null || batch.Observations.Count > 1024
            || !Fresh(current.ObservedUtc, now, NativeTrialEvidence.MaximumLogAge)
            || current.ObservedUtc > logs.ObservedUtc || current.ObservedUtc.UtcTicks < process.ProcessStartUtcTicks
            || expiry <= now || expiry - current.ObservedUtc > TimeSpan.FromSeconds(30)) return null;

        foreach (var other in logs.Sessions)
            if (other.SourceStamp == stream && other.Process != process) return null;
        // A revision cannot describe two different diagnostic payloads at the click boundary.
        if (logs.Revision == _revision && (!_diagnostics.TryGetValue(process, out var prior) || prior != current)) return null;
        return current;
    }

    private static bool Fresh(DateTimeOffset observed, DateTimeOffset now, TimeSpan maximumAge)
        => observed != default && observed <= now && now - observed <= maximumAge;
}

public sealed record SessionLogView(string Correlation, string ReportedUserId, string CurrentPlace,
    string CurrentServer, string LastKnownDestination, string State, string Time, string Explanation, string LastKnownPlayer = "Unknown",
    long? ReportedPlaceId = null, Guid? ReportedJobId = null, long? ReportedUniverseId = null, DateTimeOffset? ReportedObservedUtc = null,
    bool ReportedEvidenceCurrent = false)
{
    public bool CanAuthorizeAccountAction => false;
    /// <summary>The log reports a place and server. Not enough to offer Join same or Copy; see CanUseReportedDestination.</summary>
    public bool HasReportedDestination => ReportedPlaceId is > 0 && ReportedJobId is { } job && job != Guid.Empty;
    /// <summary>Enables Join same game/server and Copy: a reported destination whose click-time evidence held when this view was presented.</summary>
    public bool CanUseReportedDestination => ReportedEvidenceCurrent && HasReportedDestination;
    public RobloxRequestedDestination? ReportedDestination => ReportedPlaceId is > 0 && ReportedJobId is { } job && job != Guid.Empty
        ? new(ReportedPlaceId.Value, job.ToString("D")) : null;
    public static SessionLogView Unknown { get; } = new("Not correlated", "Unknown", "Unknown", "Unknown", "Unknown",
        "Unknown", "Not observed", "Log values are diagnostic only. No account or server has been verified.");

    public static SessionLogView Create(SessionEvidenceSnapshot evidence, ProcessLogDiagnostic? diagnostic, DateTimeOffset now)
    {
        bool fresh = diagnostic is not null && diagnostic.ObservedUtc <= now.AddSeconds(2)
            && now - diagnostic.ObservedUtc <= TimeSpan.FromSeconds(15);
        bool leased = fresh && diagnostic!.Correlation == SessionLogCorrelationStatus.ResourceHolderObserved
            && diagnostic.LeaseExpiresUtc > now && evidence.Stream == diagnostic.SourceStamp;
        string correlation = evidence.Ambiguous ? "Ambiguous · conflicting log evidence" : !fresh ? "Waiting for correlation" : diagnostic!.Correlation switch
        {
            SessionLogCorrelationStatus.ResourceHolderObserved => leased ? "File use observed · unverified" : "File-use observation expired",
            SessionLogCorrelationStatus.Ambiguous => "Ambiguous · no account mapping",
            SessionLogCorrelationStatus.Missing => "Log not found",
            SessionLogCorrelationStatus.AccessDenied => "Log access unavailable",
            SessionLogCorrelationStatus.SourceChanged => "Log changed · checking again",
            SessionLogCorrelationStatus.BudgetExceeded => "Waiting for diagnostic budget",
            SessionLogCorrelationStatus.Unavailable => "Log correlation unavailable",
            _ => "Not correlated"
        };
        static string Destination(ObservedSessionDestination? destination) => destination is null ? "Unknown"
            : $"Place {destination.PlaceId.ToString(CultureInfo.InvariantCulture)} · {destination.JobId:D} · {destination.ObservedUtc.ToLocalTime():HH:mm:ss}";
        return new(correlation,
            leased && evidence.ObservedUserId is { } user ? user.ToString(CultureInfo.InvariantCulture) : "Unknown",
            leased && evidence.CurrentDestination is { } current ? current.PlaceId.ToString(CultureInfo.InvariantCulture) : "Unknown",
            leased && evidence.CurrentDestination is { } server ? server.JobId.ToString("D") : "Unknown",
            Destination(evidence.LastKnownDestination), leased ? LifecycleText(evidence) : "Unknown",
            evidence.Confidence == SessionEvidenceConfidence.Unknown ? "Not observed" : evidence.ObservedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            "A process using a log does not prove who wrote its lines. Parser formats remain unverified. Reported IDs never authorize account actions; expired and last-known destinations are kept separate.",
            evidence.LastKnownUserId is { } known && evidence.LastKnownUserIdentityUtc is { } at
                ? $"User {known.ToString(CultureInfo.InvariantCulture)} · {at.ToLocalTime():HH:mm:ss} · unverified" : "Unknown",
            leased && !evidence.Ambiguous ? evidence.CurrentDestination?.PlaceId : null,
            leased && !evidence.Ambiguous ? evidence.CurrentDestination?.JobId : null,
            leased && !evidence.Ambiguous ? evidence.CurrentDestination?.UniverseId : null,
            leased && !evidence.Ambiguous ? evidence.CurrentDestination?.ObservedUtc : null);
    }

    private static string LifecycleText(SessionEvidenceSnapshot evidence)
    {
        string state = evidence.Lifecycle == ObservedSessionLifecycle.Disconnected && evidence.DisconnectReason is { } reason
            ? $"Disconnected · reason {reason.ToString(CultureInfo.InvariantCulture)} · " + (reason == ClientLogObservation.AccountLaunchedElsewhereReason ? "account launched elsewhere"
                : evidence.PlayerInitiatedDisconnect ? "you left" : "connection lost")
            : evidence.Lifecycle.ToString();
        return evidence.HumanCheckObservedUtc is null ? state : state + " · Roblox asked for a human check";
    }
}
