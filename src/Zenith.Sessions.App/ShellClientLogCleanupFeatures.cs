using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>
/// Diagnostics > Roblox client logs: an on-demand cleanup of old Roblox Player logs in two explicit steps. Check old logs counts
/// the Player logs older than the days the user chose and shows their total size; Delete these logs is offered only after a check
/// that found something, the view asks the question in <see cref="ClientLogCleanupConfirmation"/> first, and the delete touches
/// exactly the checked files (each re-checked just before it goes). Logs newer than the cutoff, logs a running client holds and
/// logs the correlator is watching are always kept. Nothing runs on its own; Panic, Lock, Cancel and exit stop a check or delete in
/// progress and drop the checked list, so a stale count can never be deleted. In sample mode without an injected cleanup the tool
/// stays off, so the sample profile never touches the real Roblox folders.
/// </summary>
public sealed partial class ShellViewModel
{
    private const string ClientLogCleanupIdle = "Check old logs counts the Roblox Player logs older than the days you choose and shows their total size. Nothing is deleted until you confirm.";
    private const string ClientLogCheckStopped = "Check stopped. Nothing was deleted.";
    private IClientLogCleanup _clientLogCleanup = null!;
    private bool _clientLogCleanupPermitted, _cleaningClientLogs;
    private CancellationTokenSource? _clientLogCleanupStop;
    private Task _clientLogCleanupWork = Task.CompletedTask;
    private long _clientLogCleanupGeneration;
    private ClientLogCleanupSurvey? _clientLogSurvey;
    private string _clientLogCleanupDays = "7";
    private string _clientLogCleanupStatus = ClientLogCleanupIdle;

    /// <summary>The cutoff in whole days (1 to 365) as typed; changing it drops the checked list, so the shown numbers are never stale when deleting.</summary>
    public string ClientLogCleanupDays
    {
        get => _clientLogCleanupDays;
        set { value ??= ""; if (value.Length > 4) value = value[..4]; if (Set(ref _clientLogCleanupDays, value)) DropClientLogSurvey("The days changed. Check old logs again before deleting."); }
    }
    public string ClientLogCleanupStatus { get => _clientLogCleanupStatus; private set => Set(ref _clientLogCleanupStatus, value); }
    public bool IsCleaningClientLogs => _cleaningClientLogs;
    public bool CanSurveyClientLogs => _clientLogCleanupPermitted && _ready && !IsLocked && !_shuttingDown && !_cleaningClientLogs;
    public bool CanDeleteClientLogs => CanSurveyClientLogs && _clientLogSurvey is { RemovableCount: > 0 };
    /// <summary>The question the view asks before Delete these logs; empty while there is nothing checked to delete.</summary>
    public string ClientLogCleanupConfirmation => _clientLogSurvey is { RemovableCount: > 0 } survey
        ? $"Delete {Logs(survey.RemovableCount)} older than {Days(survey.Days)} ({SizeText(survey.RemovableBytes)})? They are removed permanently, not sent to the Recycle Bin. "
            + $"Logs newer than {Days(survey.Days)}, logs a running client still holds and logs Zenith is watching for session evidence are kept, and each file is checked again just before it is deleted. "
            + "No account, setting or running client is changed."
        : "";
    public AsyncCommand SurveyClientLogs { get; private set; } = null!;
    public AsyncCommand DeleteClientLogs { get; private set; } = null!;
    public Command CancelClientLogCleanup { get; private set; } = null!;

    private void InitializeClientLogCleanupFeatures(IClientLogCleanup? cleanup)
    {
        _clientLogCleanupPermitted = !IsSample || cleanup is not null;
        // The correlator exists only after startup; its watched list is read at the start of every check and delete.
        _clientLogCleanup = cleanup ?? new RobloxClientLogCleanup(() => _logCorrelator?.WatchedLogPaths ?? Array.Empty<string>());
        SurveyClientLogs = new(SurveyClientLogsAsync, error => ReportClientLogCleanupFailure(error, check: true));
        DeleteClientLogs = new(DeleteClientLogsAsync, error => ReportClientLogCleanupFailure(error, check: false));
        CancelClientLogCleanup = new(_ => StopClientLogCleanup());
        PropertyChanged += ClientLogCleanupOwnerChanged;
    }

    private void ClientLogCleanupOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IsReady) or nameof(IsLocked)) { Raise(nameof(CanSurveyClientLogs)); Raise(nameof(CanDeleteClientLogs)); }
    }

    private Task SurveyClientLogsAsync(object? parameter)
    {
        if (!CanSurveyClientLogs) return Task.CompletedTask;
        if (!int.TryParse(ClientLogCleanupDays.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out int days)
            || days < RobloxClientLogCleanup.MinimumDays || days > RobloxClientLogCleanup.MaximumDays)
            throw new ArgumentException("Enter a whole number of days from 1 to 365.");
        DropClientLogSurvey(null);
        var stop = BeginClientLogCleanupWork($"Checking Roblox Player logs older than {Days(days)}…");
        _clientLogCleanupWork = RunClientLogSurveyAsync(days, _clientLogCleanupGeneration, stop);
        return _clientLogCleanupWork;
    }

    private async Task RunClientLogSurveyAsync(int days, long generation, CancellationTokenSource stop)
    {
        try
        {
            var survey = await _clientLogCleanup.SurveyAsync(TimeSpan.FromDays(days), stop.Token);
            // A stop that arrived while the check ran drops its result: nothing from before a Panic or Lock may be deleted later.
            if (stop.IsCancellationRequested || generation != _clientLogCleanupGeneration || IsLocked || _shuttingDown)
            { ClientLogCleanupStatus = ClientLogCheckStopped; return; }
            _clientLogSurvey = survey;
            ClientLogCleanupStatus = DescribeSurvey(survey);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { ClientLogCleanupStatus = ClientLogCheckStopped; }
        finally { FinishClientLogCleanupWork(stop); }
    }

    private Task DeleteClientLogsAsync(object? parameter)
    {
        if (!CanDeleteClientLogs || _clientLogSurvey is not { } survey) return Task.CompletedTask;
        // The checked list is used once: the numbers the user confirmed belong to this delete, and a new check comes before another.
        _clientLogSurvey = null;
        var stop = BeginClientLogCleanupWork($"Deleting {Logs(survey.RemovableCount)} older than {Days(survey.Days)}…");
        _clientLogCleanupWork = RunClientLogDeleteAsync(survey, _clientLogCleanupGeneration, stop);
        return _clientLogCleanupWork;
    }

    private async Task RunClientLogDeleteAsync(ClientLogCleanupSurvey survey, long generation, CancellationTokenSource stop)
    {
        try
        {
            var result = await _clientLogCleanup.DeleteAsync(survey, stop.Token);
            bool stopped = result.Stopped || stop.IsCancellationRequested || generation != _clientLogCleanupGeneration;
            string text = DescribeResult(result, stopped);
            ClientLogCleanupStatus = text;
            AddEvent(text);
            if (!stopped) Notice = text; // A stop keeps its own notice (Panic, Lock or exit).
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { ClientLogCleanupStatus = "Cleanup stopped before any log was deleted."; }
        finally { FinishClientLogCleanupWork(stop); }
    }

    private CancellationTokenSource BeginClientLogCleanupWork(string status)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _clientLogCleanupStop = stop;
        _cleaningClientLogs = true;
        ClientLogCleanupStatus = status;
        Raise(nameof(IsCleaningClientLogs)); Raise(nameof(CanSurveyClientLogs)); Raise(nameof(CanDeleteClientLogs)); Raise(nameof(ClientLogCleanupConfirmation));
        return stop;
    }

    private void FinishClientLogCleanupWork(CancellationTokenSource stop)
    {
        if (ReferenceEquals(_clientLogCleanupStop, stop)) _clientLogCleanupStop = null;
        _cleaningClientLogs = false;
        stop.Dispose();
        Raise(nameof(IsCleaningClientLogs)); Raise(nameof(CanSurveyClientLogs)); Raise(nameof(CanDeleteClientLogs)); Raise(nameof(ClientLogCleanupConfirmation));
    }

    /// <summary>Panic, Lock, Cancel and exit: stops a check or delete in progress and drops the checked list.</summary>
    private void StopClientLogCleanup()
    {
        _clientLogCleanupGeneration++;
        _clientLogCleanupStop?.Cancel();
        DropClientLogSurvey("Stopped. Check old logs again before deleting.");
    }

    private void DropClientLogSurvey(string? statusWhenDropped)
    {
        if (_clientLogSurvey is null) return;
        _clientLogSurvey = null;
        if (statusWhenDropped is not null) ClientLogCleanupStatus = statusWhenDropped;
        Raise(nameof(CanDeleteClientLogs)); Raise(nameof(ClientLogCleanupConfirmation));
    }

    private void ReportClientLogCleanupFailure(Exception error, bool check)
    {
        // A refused days value is the user's own text and is shown as such; anything else gets a fixed message (Rule 10).
        if (error is ArgumentException) { Fail(error, ManagerSubsystem.ClientLogs); return; }
        ClientLogCleanupStatus = check ? "The check could not finish. No log was deleted." : "The cleanup could not finish. Check old logs again to see what remains.";
        Fail(error, ManagerSubsystem.ClientLogs, check
            ? "The Roblox log check could not finish. No log was deleted; check the data folder permissions and try again."
            : "The Roblox log cleanup could not finish. Some logs may already have been deleted; check old logs again to see what remains.");
    }

    private async Task DisposeClientLogCleanupFeaturesAsync()
    {
        StopClientLogCleanup();
        try { await _clientLogCleanupWork; }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.ClientLogs, error.GetType()); }
        PropertyChanged -= ClientLogCleanupOwnerChanged;
    }

    private static string DescribeSurvey(ClientLogCleanupSurvey survey)
    {
        if (survey.FoldersChecked == 0) return "No Roblox logs folder was found for this Windows user, so there is nothing to clean.";
        var kept = new List<string>(3);
        if (survey.KeptRecent > 0) kept.Add($"{survey.KeptRecent} newer");
        if (survey.KeptInUse > 0) kept.Add($"{survey.KeptInUse} in use or watched by Zenith");
        if (survey.KeptUnchecked > 0) kept.Add($"{survey.KeptUnchecked} that could not be checked");
        string head = survey.RemovableCount == 0
            ? $"No Roblox Player logs older than {Days(survey.Days)} were found."
            : $"{Logs(survey.RemovableCount)} older than {Days(survey.Days)} can be removed ({SizeText(survey.RemovableBytes)}).";
        string keptText = kept.Count == 0 ? "" : " Kept: " + string.Join(", ", kept) + ".";
        string tail = survey.RemovableCount == 0 ? "" : " Nothing is deleted until you confirm.";
        string limit = survey.BudgetLimited ? " The check stopped at its limit; after deleting, check again for more." : "";
        return head + keptText + tail + limit;
    }

    private static string DescribeResult(ClientLogCleanupResult result, bool stopped)
    {
        string deleted = result.Deleted == 0 ? "No Roblox Player logs were deleted" : $"{Logs(result.Deleted)} deleted ({SizeText(result.DeletedBytes)} freed)";
        if (!stopped)
        {
            var kept = new List<string>(2);
            if (result.KeptInUse > 0) kept.Add($"{result.KeptInUse} in use or watched by Zenith");
            if (result.KeptChanged > 0) kept.Add($"{result.KeptChanged} changed or already gone since the check");
            string keptText = kept.Count == 0 ? "" : " Kept: " + string.Join(", ", kept) + ".";
            string failed = result.Failed > 0 ? $" {result.Failed} could not be deleted." : "";
            return deleted + "." + keptText + failed;
        }
        var parts = new List<string>(5) { deleted + " before the stop" };
        if (result.KeptInUse > 0) parts.Add($"{result.KeptInUse} kept in use or watched by Zenith");
        if (result.KeptChanged > 0) parts.Add($"{result.KeptChanged} kept because they changed since the check");
        if (result.Failed > 0) parts.Add($"{result.Failed} could not be deleted");
        if (result.NotAttempted > 0) parts.Add($"{result.NotAttempted} were not touched");
        return "Cleanup stopped. " + string.Join("; ", parts) + ".";
    }

    private static string Logs(int count) => count == 1 ? "1 Roblox Player log" : $"{count} Roblox Player logs";
    private static string Days(int days) => days == 1 ? "1 day" : $"{days} days";
    private static string SizeText(long bytes) => bytes >= 1024 * 1024
        ? (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
}
