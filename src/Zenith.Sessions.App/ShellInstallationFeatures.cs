using System.Collections.ObjectModel;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed record InstallationCandidateRow(ClientInstallationCandidate Candidate)
{
    public string Product => Candidate.Product.ToString();
    public string Version => Candidate.FileVersion ?? "Unknown";
    public string FileState => Candidate.FileState switch
    {
        ClientInstallationFileState.PresentUnverified => "File found · unverified",
        ClientInstallationFileState.Missing => "Registered file missing",
        ClientInstallationFileState.AccessDenied => "Access denied",
        ClientInstallationFileState.SkippedUnsafe => "Path skipped",
        _ => "Metadata unavailable"
    };
    public string Evidence => string.Join(", ", Candidate.Evidence.Select(item => item.Source switch
    {
        ClientInstallationHintSource.RobloxProtocol => "roblox registration",
        ClientInstallationHintSource.RobloxPlayerProtocol => "roblox-player registration",
        ClientInstallationHintSource.UninstallRegistry => "installation registration",
        ClientInstallationHintSource.VersionDirectory => "version folder",
        _ => "standard location"
    }).Distinct());
    public string ExecutablePath => Candidate.ExecutablePath;
    public string Explanation => Candidate.EvidenceLabel;
}

public sealed partial class ShellViewModel
{
    private IClientInstallationDiscovery _installationDiscovery = null!;
    private CancellationTokenSource? _installationStop;
    private long _installationGeneration;
    private bool _installationBusy;
    private string _installationStatus = "Refresh to inspect installed Roblox and compatible bootstrapper hints. No program will be opened or changed.";
    private string _installationObserved = "Not checked";
    private InstallationCandidateRow? _selectedInstallation;
    public ObservableCollection<InstallationCandidateRow> InstallationCandidates { get; } = [];
    public ObservableCollection<string> InstallationIssues { get; } = [];
    public InstallationCandidateRow? SelectedInstallation { get => _selectedInstallation; set { if (Set(ref _selectedInstallation, value)) { CancelPublisherVerificationWork(); ConfigureNativeTrialInstallation(); Raise(nameof(CanVerifyInstallationPublisher)); } } }
    public string InstallationStatus { get => _installationStatus; private set => Set(ref _installationStatus, value); }
    public string InstallationObserved { get => _installationObserved; private set => Set(ref _installationObserved, value); }
    public bool IsInstallationDiscoveryBusy { get => _installationBusy; private set { if (Set(ref _installationBusy, value)) { Raise(nameof(CanRefreshInstallations)); Raise(nameof(CanVerifyInstallationPublisher)); } } }
    public bool CanRefreshInstallations => _ready && !IsLocked && !_shuttingDown && !IsSample && !IsInstallationDiscoveryBusy && !IsPublisherVerificationBusy;
    public AsyncCommand RefreshInstallations { get; private set; } = null!;
    public Command CancelInstallationDiscovery { get; private set; } = null!;

    private void InitializeInstallationFeatures(IClientInstallationDiscovery? discovery,IClientExecutableTrustVerifier? verifier)
    {
        _installationDiscovery = discovery ?? new WindowsClientInstallationDiscovery();
        InitializePublisherVerification(verifier);
        RefreshInstallations = new(_ => RefreshInstallationsAsync(), ReportInstallationFailure);
        CancelInstallationDiscovery = new(_ => CancelInstallationWork());
    }
    private async Task RefreshInstallationsAsync()
    {
        if (!CanRefreshInstallations) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _installationStop = stop; long generation = _installationGeneration;
        IsInstallationDiscoveryBusy = true;
        InstallationCandidates.Clear(); InstallationIssues.Clear(); SelectedInstallation = null;
        InstallationObserved = "Checking…"; InstallationStatus = "Reading bounded installation metadata. Native account launch remains unavailable.";
        try
        {
            var snapshot = await _installationDiscovery.DiscoverAsync(stop.Token);
            if (stop.IsCancellationRequested || generation != _installationGeneration || IsLocked || _shuttingDown) return;
            if (!Enum.IsDefined(snapshot.Status) || snapshot.Candidates.Count > 64 || snapshot.Issues.Count > 32
                || snapshot.ObservedUtc > DateTimeOffset.UtcNow.AddSeconds(2)
                || snapshot.Candidates.Any(item => !Enum.IsDefined(item.Product) || !Enum.IsDefined(item.FileState)
                    || string.IsNullOrWhiteSpace(item.ExecutablePath) || item.ExecutablePath.Length > 1024
                    || item.Evidence.Count > 16 || item.Evidence.Any(evidence => !Enum.IsDefined(evidence.Source) || !Enum.IsDefined(evidence.Product)))
                || snapshot.Issues.Any(issue => !Enum.IsDefined(issue.Code))
                || snapshot.Candidates.Select(item => item.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Candidates.Count)
                throw new InvalidOperationException("The installation inventory returned inconsistent metadata.");
            foreach (var candidate in snapshot.Candidates)
                InstallationCandidates.Add(new(candidate with { Evidence = Array.AsReadOnly(candidate.Evidence.ToArray()) }));
            foreach (var issue in snapshot.Issues) InstallationIssues.Add(issue.Summary);
            InstallationObserved = snapshot.ObservedUtc.ToUniversalTime().ToString("u");
            InstallationStatus = snapshot.Status + " · " + snapshot.Summary;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _installationGeneration && !IsLocked && !_shuttingDown) ReportInstallationFailure(error);
            else _diagnostics.RecordFailure(ManagerSubsystem.Installation, error.GetType());
        }
        finally
        {
            if (ReferenceEquals(_installationStop, stop)) _installationStop = null;
            IsInstallationDiscoveryBusy = false;
        }
    }
    private void ReportInstallationFailure(Exception error)
    {
        InstallationObserved = "Check failed";
        InstallationStatus = "Installation metadata could not be checked. No executable or provider was selected; retry when the files are available.";
        _diagnostics.RecordFailure(ManagerSubsystem.Installation, error.GetType());
    }
    private void CancelInstallationWork()
    {
        CancelPublisherVerificationWork();
        Interlocked.Increment(ref _installationGeneration); _installationStop?.Cancel();
        InstallationCandidates.Clear(); InstallationIssues.Clear(); SelectedInstallation = null;
        InstallationObserved = "Not checked";
        InstallationStatus = "Installation check stopped. Refresh explicitly to check again; no program was opened or changed.";
    }
}
