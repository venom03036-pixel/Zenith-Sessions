using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private IPerformanceSettingsTransferStore _performanceTransferStore = null!;
    private PortablePerformanceSettings? _performanceReview;
    private CancellationTokenSource? _performanceTransferStop;
    private long _performanceTransferGeneration;
    private bool _performanceTransferBusy;
    private string _performanceShareInput = "";
    private string _performanceTransferStatus = "Share manager timing only. Account selection, sign-in, capacity guards and client settings stay local.";

    public string PerformanceShareInput { get => _performanceShareInput; set => Set(ref _performanceShareInput, value); }
    public string PerformanceTransferStatus { get => _performanceTransferStatus; private set => Set(ref _performanceTransferStatus, value); }
    public bool IsPerformanceTransferBusy { get => _performanceTransferBusy; private set { if (Set(ref _performanceTransferBusy, value)) NotifyPerformanceTransfer(); } }
    public bool CanTransferPerformance => _ready && !IsLocked && !_shuttingDown && !IsPerformanceTransferBusy;
    public bool HasPerformanceReview => _performanceReview is not null;
    public string PerformanceReviewSummary
    {
        get
        {
            if (_performanceReview is not { } candidate) return "";
            var policy = candidate.ToReviewSettings(new(MainAccountProfileId: _draftMainAccount)).Resolve();
            return policy.DisplayName + ". " + policy.Explanation + Environment.NewLine
                + FormattableString.Invariant($"Custom timing values: process details {candidate.CustomProcessSampleMs} ms; dashboard {candidate.CustomPresentationMs} ms; launch spacing {candidate.CustomStaggerMs} ms; stabilization {candidate.CustomStabilizationMs} ms.")
                + (candidate.Profile == ManagerPerformanceProfile.Custom ? " These values are active in Custom." : " These values stay dormant until Custom is selected.")
                + " Your current local main-account selection is retained.";
        }
    }
    public AsyncCommand ImportPerformanceSettings { get; private set; } = null!;
    public AsyncCommand ExportPerformanceSettings { get; private set; } = null!;
    public Command PreviewPerformanceCode { get; private set; } = null!;
    public Command UsePerformanceReview { get; private set; } = null!;
    public Command DismissPerformanceReview { get; private set; } = null!;
    public Command CancelPerformanceTransfer { get; private set; } = null!;

    private void InitializePerformanceTransfer(IPerformanceSettingsTransferStore? store)
    {
        _performanceTransferStore = store ?? new PerformanceSettingsTransferStore(DataRoot, AppContext.BaseDirectory);
        ImportPerformanceSettings = new(parameter => TransferPerformanceFileAsync(parameter, export: false), ReportPerformanceTransferFailure);
        ExportPerformanceSettings = new(parameter => TransferPerformanceFileAsync(parameter, export: true), ReportPerformanceTransferFailure);
        PreviewPerformanceCode = new(_ =>
        {
            if (!CanTransferPerformance) return;
            try
            {
                var candidate = PerformanceSettingsTransfer.DecodeShareCode(PerformanceShareInput, _lifetime.Token);
                _performanceReview = candidate; NotifyPerformanceTransfer();
                PerformanceTransferStatus = "Code checked. Review the settings below before copying them into your draft.";
            }
            catch (Exception error) when (error is ArgumentException or System.IO.InvalidDataException or System.Text.Json.JsonException or PerformanceSettingsTransferException)
            { ReportPerformanceTransferFailure(error); }
        });
        UsePerformanceReview = new(_ =>
        {
            if (!CanTransferPerformance || _performanceReview is not { } candidate) return;
            // The destination's current main account is read now, never from transferred data.
            LoadPerformanceDraft(candidate.ToReviewSettings(new(MainAccountProfileId: _draftMainAccount)));
            _performanceReview = null; NotifyPerformanceTransfer(); PreferencesEdited();
            PerformanceTransferStatus = "Copied into the performance draft. Save all settings to apply it; other drafts and your main account were retained.";
        });
        DismissPerformanceReview = new(_ =>
        {
            if (!CanTransferPerformance) return;
            _performanceReview = null; NotifyPerformanceTransfer();
            PerformanceTransferStatus = "Review dismissed. Your settings were not changed.";
        });
        CancelPerformanceTransfer = new(_ => CancelPerformanceTransfers());
    }

    public string? BuildPerformanceShareCode()
    {
        if (!CanTransferPerformance) return null;
        return PerformanceSettingsTransfer.EncodeShareCode(PortablePerformanceSettings.FromSettings(ReadPerformanceDraft()), _lifetime.Token);
    }

    private async Task TransferPerformanceFileAsync(object? parameter, bool export)
    {
        if (!CanTransferPerformance || parameter is not string path || string.IsNullOrWhiteSpace(path)) return;
        var outgoing = export ? PortablePerformanceSettings.FromSettings(ReadPerformanceDraft()) : null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _performanceTransferStop = stop; long generation = _performanceTransferGeneration;
        IsPerformanceTransferBusy = true;
        PerformanceTransferStatus = export ? "Saving the manager performance file…" : "Checking the performance file…";
        try
        {
            PortablePerformanceSettings? candidate = null;
            if (export) await _performanceTransferStore.ExportAsync(path, outgoing!, stop.Token);
            else candidate = await _performanceTransferStore.ImportAsync(path, stop.Token);
            if (stop.IsCancellationRequested || generation != _performanceTransferGeneration || IsLocked || _shuttingDown) return;
            if (!export) { _performanceReview = candidate!.Validated(); NotifyPerformanceTransfer(); }
            PerformanceTransferStatus = export
                ? "Manager performance file saved. Accounts, authorization, main-account selection and capacity guards were excluded."
                : "File checked. Review below, then copy it into your draft. No active setting has changed.";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _performanceTransferGeneration && !IsLocked && !_shuttingDown) ReportPerformanceTransferFailure(error);
            else _diagnostics.RecordFailure(ManagerSubsystem.Settings, error.GetType());
        }
        finally
        {
            if (ReferenceEquals(_performanceTransferStop, stop)) _performanceTransferStop = null;
            IsPerformanceTransferBusy = false;
        }
    }

    private void ReportPerformanceTransferFailure(Exception error)
    {
        PerformanceTransferStatus = "The performance transfer failed. Use a supported Zenith performance file or code and check the selected folder. Existing settings and the prior review were preserved.";
        _diagnostics.RecordFailure(ManagerSubsystem.Settings, error.GetType());
    }
    private void NotifyPerformanceTransfer()
    { Raise(nameof(CanTransferPerformance)); Raise(nameof(HasPerformanceReview)); Raise(nameof(PerformanceReviewSummary)); }
    private void CancelPerformanceTransfers()
    {
        Interlocked.Increment(ref _performanceTransferGeneration); _performanceTransferStop?.Cancel();
        _performanceReview = null; PerformanceShareInput = ""; NotifyPerformanceTransfer();
        PerformanceTransferStatus = "Performance transfer stopped. Settings remain unchanged; a file write already committed may remain on disk.";
    }
}
