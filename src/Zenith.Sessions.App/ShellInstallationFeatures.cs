using System.Collections.ObjectModel;
using System.Globalization;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed class InstallationCandidateRow(ClientInstallationCandidate candidate, ClientReleaseMatch release = ClientReleaseMatch.NotChecked) : Observable
{
    private ClientReleaseMatch _release = release;
    public ClientInstallationCandidate Candidate { get; } = candidate;
    // Compared with Roblox's current public release on the last explicit refresh. The row is listed before Roblox answers
    // and filled in place when it does, so a selected row (and a verification of it) keeps its identity. Leftover older
    // version folders are "Not current" on their own row; whether an update is pending is said once, for the installation.
    public ClientReleaseMatch Release { get => _release; internal set { if (Set(ref _release, value)) Raise(nameof(ReleaseStatus)); } }
    public string ReleaseStatus => Release switch
    {
        ClientReleaseMatch.Current => "Current release",
        ClientReleaseMatch.NotCurrent => "Not current",
        ClientReleaseMatch.NotCompared => "Not compared",
        _ => "Not checked"
    };
    // A Roblox player found under a bootstrapper's folder carries that bootstrapper as its
    // evidence product. Native trials reject it by design, so the row names its owner.
    public ClientInstallationProduct? ManagedBy => Candidate.Product == ClientInstallationProduct.Roblox
        ? Candidate.Evidence.Where(item => item.Product != ClientInstallationProduct.Roblox)
            .Select(item => (ClientInstallationProduct?)item.Product).FirstOrDefault()
        : null;
    public string Product => ManagedBy is { } owner ? $"Roblox · {owner}-managed" : Candidate.Product.ToString();
    // The product name in the file's own version resource. Product above is inferred from the path or
    // registration, so this is the one column that exposes a renamed or non-Roblox file. It is the file's
    // unverified claim; hidden direction, line-break or control characters are never rendered.
    public string ReportedProductName => Candidate.ProductName switch
    {
        null or "" => "Not reported",
        var name when name.Length <= 80 && !name.Any(c => char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) => name,
        _ => "Not shown: unusual text"
    };
    public string Version => Candidate.FileVersion ?? "Unknown";
    public string FileState => Candidate.FileState switch
    {
        ClientInstallationFileState.PresentUnverified => "File found · unverified",
        ClientInstallationFileState.Missing => "Registered file missing",
        ClientInstallationFileState.AccessDenied => "Access denied",
        ClientInstallationFileState.SkippedUnsafe => "Path skipped",
        ClientInstallationFileState.TooLarge => $"Too large to inspect (over {WindowsClientInstallationInventorySource.MaximumExecutableBytes / (1024 * 1024)} MB)",
        _ => "Metadata unavailable"
    };
    public string Evidence => string.Join(", ", Candidate.Evidence.Select(item => (item.Product != Candidate.Product ? item.Product + " " : "") + SourceLabel(item.Source)).Distinct());
    public string ExecutablePath => Candidate.ExecutablePath;
    internal static string SourceLabel(ClientInstallationHintSource source) => source switch
    {
        ClientInstallationHintSource.RobloxProtocol => "roblox registration",
        ClientInstallationHintSource.RobloxPlayerProtocol => "roblox-player registration",
        ClientInstallationHintSource.UninstallRegistry => "installation registration",
        ClientInstallationHintSource.VersionDirectory => "version folder",
        _ => "standard location"
    };
    // Discovery keeps one issue per (code, product, source); the line names the product and where the hint came from
    // so two issues with the same code never read as the same unexplained sentence.
    internal static string IssueLine(ClientInstallationIssue issue)
    {
        string origin = issue.Source is { } source ? SourceLabel(source) : "";
        if (issue.Product is { } product) origin = origin.Length == 0 ? product.ToString() : product + " · " + origin;
        else if (origin.Length > 0) origin = char.ToUpperInvariant(origin[0]) + origin[1..];
        return origin.Length == 0 ? issue.Summary : origin + ": " + issue.Summary;
    }
}

public sealed partial class ShellViewModel
{
    private IClientInstallationDiscovery _installationDiscovery = null!;
    private IPublicRobloxClientReleaseDirectory? _clientReleases;
    private CancellationTokenSource? _installationStop;
    // Set only while the local list is shown and Roblox's answer is still awaited: Cancel check then stops that request alone.
    private CancellationTokenSource? _installationReleaseStop;
    private long _installationGeneration;
    private bool _installationBusy;
    // True until a refresh has shown the rows it read from this PC; the release request that follows does not hold them.
    private bool _installationListPending;
    private const string InstallationReleaseStopped = "Release check stopped. The installations already found stay listed as not checked; refresh to ask Roblox's public version service again.";
    private const string InstallationNotChecked = "Refresh to inspect installed Roblox and compatible bootstrapper hints and to ask Roblox's public version service for its current release. No program will be downloaded, opened or changed.";
    private string _installationStatus = InstallationNotChecked;
    private string _installationObserved = "Not checked";
    private string _installationReleaseStatus = "";
    private InstallationCandidateRow? _selectedInstallation;
    public ObservableCollection<InstallationCandidateRow> InstallationCandidates { get; } = [];
    public ObservableCollection<string> InstallationIssues { get; } = [];
    public InstallationCandidateRow? SelectedInstallation { get => _selectedInstallation; set { if (Set(ref _selectedInstallation, value)) { CancelPublisherVerificationWork(); ConfigureNativeTrialInstallation(); Raise(nameof(CanVerifyInstallationPublisher)); } } }
    public string InstallationStatus { get => _installationStatus; private set => Set(ref _installationStatus, value); }
    public string InstallationObserved { get => _installationObserved; private set => Set(ref _installationObserved, value); }
    public string InstallationReleaseStatus { get => _installationReleaseStatus; private set => Set(ref _installationReleaseStatus, value); }
    public bool IsInstallationDiscoveryBusy { get => _installationBusy; private set { if (Set(ref _installationBusy, value)) { Raise(nameof(CanRefreshInstallations)); Raise(nameof(CanVerifyInstallationPublisher)); } } }
    // Refresh clears the selected installation, which would stop an owned native preparation; wait for it to finish.
    public bool CanRefreshInstallations => _ready && !IsLocked && !_shuttingDown && !IsSample && !IsInstallationDiscoveryBusy && !IsPublisherVerificationBusy && !_nativeLaunch.IsBusy;
    public AsyncCommand RefreshInstallations { get; private set; } = null!;
    public Command CancelInstallationDiscovery { get; private set; } = null!;

    private void InitializeInstallationFeatures(IClientInstallationDiscovery? discovery,IClientExecutableTrustVerifier? verifier,IPublicRobloxClientReleaseDirectory? releases)
    {
        // The release check reaches the network. The app's own public directory answers it only beside the real discovery,
        // so an injected discovery (an isolated test) never reaches Roblox unless a release source is injected too.
        _clientReleases = releases ?? (discovery is null ? _publicClientReleases : null);
        _installationDiscovery = discovery ?? new WindowsClientInstallationDiscovery();
        InitializePublisherVerification(verifier);
        RefreshInstallations = new(_ => RefreshInstallationsAsync(), ReportInstallationFailure);
        CancelInstallationDiscovery = new(_ => CancelInstallationCheck());
    }
    private async Task RefreshInstallationsAsync()
    {
        if (!CanRefreshInstallations) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        // Panic, Lock and shutdown stop the whole refresh through `stop`; Cancel check after the list is shown stops only this.
        using var releaseStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        _installationStop = stop; long generation = _installationGeneration;
        bool Current() => !stop.IsCancellationRequested && generation == _installationGeneration && !IsLocked && !_shuttingDown;
        _installationListPending = true; IsInstallationDiscoveryBusy = true;
        InstallationCandidates.Clear(); InstallationIssues.Clear(); SelectedInstallation = null;
        InstallationObserved = "Checking…"; InstallationStatus = "Reading bounded installation metadata. Native account launch remains unavailable.";
        InstallationReleaseStatus = _clientReleases is null ? "" : "Asking Roblox's public version service for its current release…";
        try
        {
            var snapshot = await _installationDiscovery.DiscoverAsync(stop.Token);
            if (!Current()) return;
            if (!Enum.IsDefined(snapshot.Status) || snapshot.Candidates.Count > 64 || snapshot.Issues.Count > 32
                || snapshot.ObservedUtc > DateTimeOffset.UtcNow.AddSeconds(2)
                || snapshot.Candidates.Any(item => !Enum.IsDefined(item.Product) || !Enum.IsDefined(item.FileState)
                    || string.IsNullOrWhiteSpace(item.ExecutablePath) || item.ExecutablePath.Length > WindowsClientInstallationDiscovery.MaximumHintCharacters
                    || item.Evidence.Count > 16 || item.Evidence.Any(evidence => !Enum.IsDefined(evidence.Source) || !Enum.IsDefined(evidence.Product)))
                || snapshot.Issues.Any(issue => !Enum.IsDefined(issue.Code) || issue.Product is { } product && !Enum.IsDefined(product)
                    || issue.Source is { } source && !Enum.IsDefined(source))
                || snapshot.Candidates.Select(item => item.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Candidates.Count)
                throw new InvalidOperationException("The installation inventory returned inconsistent metadata.");
            // The local rows never wait for the network: an unreachable service can hold the request for the client's whole
            // timeout. They are shown now, "Not checked", and Roblox's answer fills their Release column in place.
            var rows = new InstallationCandidateRow[snapshot.Candidates.Count];
            for (int index = 0; index < rows.Length; index++)
            {
                var candidate = snapshot.Candidates[index];
                InstallationCandidates.Add(rows[index] = new(candidate with { Evidence = Array.AsReadOnly(candidate.Evidence.ToArray()) }));
            }
            foreach (var issue in snapshot.Issues)
                if (InstallationCandidateRow.IssueLine(issue) is var line && !InstallationIssues.Contains(line)) InstallationIssues.Add(line);
            InstallationObserved = snapshot.ObservedUtc.ToUniversalTime().ToString("u");
            InstallationStatus = snapshot.Status + " · " + snapshot.Summary;
            _installationListPending = false; Raise(nameof(CanVerifyInstallationPublisher));
            if (_clientReleases is null) return;
            _installationReleaseStop = releaseStop;
            var release = await ReadCurrentReleaseAsync(releaseStop.Token);
            // Cancel check already said the release check stopped; an answer that still arrives is not applied.
            if (!Current() || releaseStop.IsCancellationRequested) return;
            var latest = release is { State: PublicDirectoryState.Success } && RobloxClientRelease.IsValid(release.Value) ? release.Value : null;
            foreach (var row in rows) row.Release = RobloxClientRelease.Compare(row.Candidate, latest);
            InstallationReleaseStatus = ReleaseSummary(release, latest, rows);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || releaseStop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _installationGeneration && !IsLocked && !_shuttingDown) ReportInstallationFailure(error);
            else _diagnostics.RecordFailure(ManagerSubsystem.Installation, error.GetType());
        }
        finally
        {
            if (ReferenceEquals(_installationStop, stop)) _installationStop = null;
            if (ReferenceEquals(_installationReleaseStop, releaseStop)) _installationReleaseStop = null;
            _installationListPending = false; IsInstallationDiscoveryBusy = false;
        }
    }
    // One anonymous request per explicit refresh, owned by that refresh's cancellation and awaited by it, so Dispose (which
    // awaits the refresh) never releases the HTTP client under it. An unreachable or malformed answer never fails or delays
    // the local list; its exception text is never shown or recorded, only its type.
    private async Task<PublicDirectoryResult<PublicRobloxClientRelease>?> ReadCurrentReleaseAsync(CancellationToken ct)
    {
        if (_clientReleases is null) return null;
        try { return await _clientReleases.GetCurrentWindowsPlayerReleaseAsync(ct); }
        catch (Exception error) when (!(error is OperationCanceledException && ct.IsCancellationRequested))
        {
            _diagnostics.RecordFailure(ManagerSubsystem.Installation,error.GetType());
            return new(PublicDirectoryState.Failure, null, "The request to the public version service failed.");
        }
    }
    private static string ReleaseSummary(PublicDirectoryResult<PublicRobloxClientRelease>? result, PublicRobloxClientRelease? latest, IReadOnlyList<InstallationCandidateRow> rows)
    {
        if (result is null) return "";
        if (latest is null)
            return "Could not check Roblox's current release. " + (result.State == PublicDirectoryState.Success ? "The public version service returned an unusable release." : result.Message);
        string named = latest.Version + " (" + latest.ClientVersionUpload + ")";
        if (rows.Any(row => row.Release == ClientReleaseMatch.Current))
            return "Up to date: an installed Roblox player matches Roblox's current release " + named + ".";
        if (rows.Any(row => row.Release == ClientReleaseMatch.NotCurrent))
            return "Update pending: no installed Roblox player matches Roblox's current release " + named
                + ". Roblox normally updates when it next starts; Zenith never downloads or installs updates.";
        return "Roblox's current release is " + named + ". No installed Roblox player could be compared with it.";
    }
    private void ReportInstallationFailure(Exception error)
    {
        InstallationReleaseStatus = "";
        InstallationObserved = "Check failed";
        InstallationStatus = "Installation metadata could not be checked. No executable or provider was selected; retry when the files are available.";
        _diagnostics.RecordFailure(ManagerSubsystem.Installation, error.GetType());
    }
    // Cancel check: once the local list is shown only Roblox's answer is awaited, so only that request stops and the rows
    // already read stay. Before then, and always for Panic, Lock and shutdown, CancelInstallationWork clears everything.
    private void CancelInstallationCheck()
    {
        if (_installationReleaseStop is { } release) { release.Cancel(); InstallationReleaseStatus = InstallationReleaseStopped; return; }
        CancelInstallationWork();
    }
    private void CancelInstallationWork()
    {
        CancelPublisherVerificationWork();
        // Only an unfinished check is "stopped"; otherwise the cleared list is simply unchecked again (Panic, Lock).
        bool checking = _installationStop is not null;
        Interlocked.Increment(ref _installationGeneration); _installationStop?.Cancel();
        InstallationCandidates.Clear(); InstallationIssues.Clear(); SelectedInstallation = null;
        InstallationObserved = "Not checked"; InstallationReleaseStatus = "";
        InstallationStatus = checking ? "Installation check stopped. Refresh explicitly to check again; no program was opened or changed." : InstallationNotChecked;
    }
}
