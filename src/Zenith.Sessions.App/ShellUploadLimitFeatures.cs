using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Status = Zenith.Sessions.Core.UploadLimitStatus;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private IUploadLimitControl _uploadLimit = null!;
    private bool _uploadLimitPermitted, _changingUploadLimit, _uploadLimitStoppedByPanic;
    private CancellationTokenSource? _uploadLimitStop;
    private Task _uploadLimitWork = Task.CompletedTask;
    private long _uploadLimitGeneration;
    private UploadLimitReconciliation? _uploadLimitReconciliation;
    private string _uploadLimitRate = "1000";
    private string _uploadLimitStatus = "Choose a rate and Apply upload limit, or Remove. Windows asks for administrator approval first.";
    private sealed record UploadLimitRequest(long Generation, int? KilobitsPerSecond);

    /// <summary>The typed rate in kilobits per second; read at the click. Prefilled with the recorded rate when there is one.</summary>
    public string UploadLimitRate { get => _uploadLimitRate; set => Set(ref _uploadLimitRate, value ?? ""); }
    public string UploadLimitStatus { get => _uploadLimitStatus; private set => Set(ref _uploadLimitStatus, value); }
    public bool CanChangeUploadLimit => _uploadLimitPermitted && _ready && !IsLocked && !_shuttingDown && !_changingUploadLimit;
    public bool IsChangingUploadLimit => _changingUploadLimit;
    public AsyncCommand ApplyUploadLimit { get; private set; } = null!;
    public AsyncCommand RemoveUploadLimit { get; private set; } = null!;
    public Command CancelUploadLimit { get; private set; } = null!;

    /// <summary>
    /// What Zenith knows about its upload limit in this Windows session: its own record of the last change it asked for, never a
    /// readback, because reading the active policy store needs administrator rights. Says so when the record is uncertain or unreadable.
    /// </summary>
    public string CurrentUploadLimitText
    {
        get
        {
            if (_uploadLimit.RecordUnreadable)
                return "Zenith could not read its record of the upload limit (" + WindowsUploadLimitControl.RecordFileName
                    + " in the data folder), so it cannot say whether one is in force. Apply upload limit or Remove replaces the record.";
            return _uploadLimit.Current switch
            {
                null => "No upload limit applied by Zenith in this Windows session. Reading Windows' policy list needs administrator rights, so Zenith shows only its own record.",
                { Confirmed: true } record => "Upload limit applied by Zenith in this Windows session: " + UploadLimitPolicy.FormatRate(record.KilobitsPerSecond)
                    + " for all Roblox clients together, confirmed by Windows at " + LocalTime(record.AtUtc)
                    + ". Windows drops it when it restarts; Remove lifts it now. This is Zenith's own record, not a readback: another administrator may have changed it since.",
                { KilobitsPerSecond: > 0 } record => "Zenith does not know whether its upload limit of " + UploadLimitPolicy.FormatRate(record.KilobitsPerSecond)
                    + " is in force: the last " + record.LastAction + " ended without Windows' confirmation at " + LocalTime(record.AtUtc)
                    + ". Choose Apply upload limit or Remove again to be sure.",
                var record => "Zenith does not know whether an upload limit is in force: the last " + record.LastAction + " ended without Windows' confirmation at "
                    + LocalTime(record.AtUtc) + ". Choose Apply upload limit or Remove again to be sure."
            };
        }
    }

    private static string LocalTime(DateTimeOffset utc) => utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    private void InitializeUploadLimitFeatures(IUploadLimitControl? control)
    {
        _uploadLimitPermitted = !IsSample || control is not null;
        _uploadLimit = control ?? new WindowsUploadLimitControl(Path.Combine(DataRoot, WindowsUploadLimitControl.RecordFileName));
        _uploadLimit.Reconciled += UploadLimitReconciled;
        ApplyUploadLimit = new(p => ChangeUploadLimitAsync(UploadLimitAction.Apply, p), ReportUploadLimitFailure, CaptureUploadLimitRequest);
        RemoveUploadLimit = new(p => ChangeUploadLimitAsync(UploadLimitAction.Remove, p), ReportUploadLimitFailure, CaptureUploadLimitRequest);
        CancelUploadLimit = new(_ => StopUploadLimitOperations(panic: false));
        PropertyChanged += UploadLimitOwnerChanged;
    }

    private void UploadLimitReconciled(UploadLimitReconciliation reconciliation)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_shuttingDown || reconciliation.OperationRevision != _uploadLimit.OperationRevision) return;
            _uploadLimitReconciliation = reconciliation;
            Raise(nameof(CurrentUploadLimitText));
            if (!_changingUploadLimit) ReportUploadLimit(reconciliation.Result);
        }));
    }

    private void UploadLimitOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IsReady) or nameof(IsLocked)) Raise(nameof(CanChangeUploadLimit));
    }

    // The rate is read at the click, so a later edit of the box cannot reach a waiting operation.
    private object? CaptureUploadLimitRequest(object? parameter)
        => CanChangeUploadLimit ? new UploadLimitRequest(_uploadLimitGeneration, UploadLimitPolicy.TryParseRate(UploadLimitRate, out int rate) ? rate : null) : null;

    private Task ChangeUploadLimitAsync(UploadLimitAction action, object? parameter)
    {
        if (!CanChangeUploadLimit || parameter is not UploadLimitRequest request || request.Generation != _uploadLimitGeneration) return Task.CompletedTask;
        if (action == UploadLimitAction.Apply && request.KilobitsPerSecond is null)
        {
            UploadLimitStatus = UploadLimitPolicy.RateRequirement;
            return Task.CompletedTask;
        }
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _uploadLimitStop = stop;
        _uploadLimitStoppedByPanic = false;
        _uploadLimitReconciliation = null;
        _changingUploadLimit = true;
        UploadLimitStatus = "Waiting for the administrator prompt and Windows PowerShell…";
        Raise(nameof(IsChangingUploadLimit)); Raise(nameof(CanChangeUploadLimit));
        _uploadLimitWork = RunUploadLimitAsync(action, request.KilobitsPerSecond ?? 0, stop);
        return _uploadLimitWork;
    }

    private async Task RunUploadLimitAsync(UploadLimitAction action, int kilobitsPerSecond, CancellationTokenSource stop)
    {
        try
        {
            var result = action == UploadLimitAction.Apply
                ? await _uploadLimit.ApplyAsync(kilobitsPerSecond, stop.Token)
                : await _uploadLimit.RemoveAsync(stop.Token);
            if (result is null || result.Action != action || !Enum.IsDefined(result.Status))
                throw new InvalidOperationException("The upload limit report did not match the request.");
            ReportUploadLimit(result);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { ReportUploadLimit(new(action, Status.Cancelled, kilobitsPerSecond, 0, _uploadLimit.Current)); }
        finally
        {
            if (ReferenceEquals(_uploadLimitStop, stop)) _uploadLimitStop = null;
            _changingUploadLimit = false;
            stop.Dispose();
            if (_uploadLimitReconciliation is { } late && late.OperationRevision == _uploadLimit.OperationRevision)
                ReportUploadLimit(late.Result);
            Raise(nameof(IsChangingUploadLimit)); Raise(nameof(CanChangeUploadLimit)); Raise(nameof(CurrentUploadLimitText));
        }
    }

    // A declined prompt and a stop are the user's own choices and only a status line; a run Windows refused or did not confirm is an error.
    private void ReportUploadLimit(UploadLimitResult result)
    {
        string text = result.Status == Status.Cancelled && _uploadLimitStoppedByPanic
            ? "Upload limit work stopped by Panic or Lock. If the administrator prompt was already approved, that PowerShell may still finish on its own; choose Apply upload limit or Remove again to be sure."
            : result.Explanation;
        if (!result.RecordSaved) _diagnostics.RecordFailure(ManagerSubsystem.UploadLimit, typeof(IOException));
        switch (result.Status)
        {
            case Status.Confirmed:
                AddEvent(result.Action == UploadLimitAction.Apply
                    ? "Upload limit of " + UploadLimitPolicy.FormatRate(result.KilobitsPerSecond) + " applied for all Roblox clients together."
                    : "Upload limit removed; Windows confirmed Zenith's policies are gone.");
                break;
            case Status.Refused or Status.NotConfirmed or Status.Unavailable or Status.TimedOut:
                ShowError(text);
                AddEvent("Upload limit change failed (" + result.Status + ").");
                _diagnostics.RecordFailure(ManagerSubsystem.UploadLimit);
                break;
        }
        UploadLimitStatus = text;
    }

    /// <summary>Cancel, Panic, Lock and exit stop the wait. A run the user already approved finishes on its own; the status says so.</summary>
    private void StopUploadLimitOperations(bool panic)
    {
        _uploadLimitGeneration++;
        if (panic && _changingUploadLimit) _uploadLimitStoppedByPanic = true;
        _uploadLimitStop?.Cancel();
        _uploadLimit.CancelOperations();
    }

    private void ReportUploadLimitFailure(Exception error)
    {
        UploadLimitStatus = "The upload limit operation could not be confirmed. Choose Apply upload limit or Remove again to be sure.";
        Fail(error, ManagerSubsystem.UploadLimit);
    }

    /// <summary>Startup only: reads Zenith's record for this Windows session so the section shows it. Nothing is changed. Sample mode without an injected control never looks.</summary>
    private async Task LoadUploadLimitRecordAsync()
    {
        if (!_uploadLimitPermitted) return;
        try
        {
            var record = await _uploadLimit.LoadRecordAsync(_lifetime.Token);
            if (record is not null && UploadLimitPolicy.IsValidRate(record.KilobitsPerSecond))
                UploadLimitRate = record.KilobitsPerSecond.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Fail(error, ManagerSubsystem.UploadLimit, "Zenith could not read its record of the upload limit, so it cannot say whether one is in force. Apply upload limit or Remove replaces the record.");
        }
        Raise(nameof(CurrentUploadLimitText));
    }

    private async Task DisposeUploadLimitFeaturesAsync()
    {
        _uploadLimit.Reconciled -= UploadLimitReconciled;
        StopUploadLimitOperations(panic: false);
        try { await _uploadLimitWork; }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.UploadLimit, error.GetType()); }
        PropertyChanged -= UploadLimitOwnerChanged;
        try { await _uploadLimit.DisposeAsync(); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.UploadLimit, error.GetType()); }
    }
}
