using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

// Discord webhook alerts (R-api-12, also R-shell-22): off by default; a saved schema-1 choice applied after Save settings, never
// from a draft. The webhook URL is a secret: typed into a masked field, kept in the DPAPI-protected DiscordWebhookStore, read from
// it for each send only, shown as host plus the token's last four, never logged or exported (Rule 10). Events come only from Zenith's
// own evidence: the presence history's hang, disconnect and fault-record facts (ClientAlertTracker, fed from the same presentation
// step that shows them) and the launch queue's own snapshots (LaunchBatchAlertTracker). Messages are fixed templates with account
// labels, places and counts. The outbox (Core) coalesces, bounds and paces sends; a failed send is recorded by type and shown once
// until a send succeeds again, never retried in a storm, and a webhook Discord no longer knows stops alerts until a new URL is saved
// (Rule 16). Panic stops the outbox for this run (Rule 11), until Resume alerts or the choice is saved on after being off; Lock leaves
// it running, because monitoring continues behind the lock and the idle lock would otherwise silence alerts exactly when the user is
// away. Exit cancels the send in flight and disposes the transport and the store (Rules 12, 13). Sample mode never sends.
// Rule 18: CPU bounded by the sample cadence and one message per 2 s; memory by 50 queued alerts, 16 tracked batches and the
// history's own client limits; no timer or thread of its own (one awaited worker while on); every path takes the lifetime token.
public sealed partial class ShellViewModel
{
    private const string DiscordAlertsOff = "Off. Nothing is sent to Discord.";
    private const string DiscordAlertsSample = "Not available in sample mode: Zenith does not send alerts while it shows sample profiles.";
    private const string NoDiscordWebhook = "No webhook saved. Paste the webhook URL Discord shows for a channel (Edit channel → Integrations → Webhooks) and choose Save webhook.";
    private enum AlertStop { None, Panic, RefusedWebhook }
    private IDiscordWebhookStore _webhookStore = null!;
    private IDiscordWebhookTransport _webhookTransport = null!;
    private DiscordAlertOutbox _alertOutbox = null!;
    private readonly ClientAlertTracker _clientAlerts = new();
    private readonly LaunchBatchAlertTracker _batchAlerts = new();
    private Task _alertLabelWork = Task.CompletedTask;
    private bool _alertsPermitted, _discordAlerts, _webhookBusy, _alertFailureShown;
    private AlertStop _alertStop;
    private DiscordWebhookSummary? _webhookSummary;
    private string _discordWebhookStatus = NoDiscordWebhook, _discordAlertStatus = DiscordAlertsOff, _lastAlertProblem = "";
    private long _alertsSent;
    private DateTimeOffset _lastAlertAt;

    /// <summary>Settings draft of the saved "Send Discord alerts" choice; only the saved value is applied.</summary>
    public bool DiscordAlerts { get => _discordAlerts; set { if (Set(ref _discordAlerts, value)) PreferencesEdited(); } }
    public bool HasDiscordWebhook => _webhookSummary is not null;
    /// <summary>The outbox is running: the saved choice is on and neither Panic nor a refused webhook stopped it.</summary>
    public bool IsDiscordAlertsActive => _alertOutbox.IsRunning;
    /// <summary>Panic, or a webhook Discord refused, stopped alerts for this run while the saved choice is still on.</summary>
    public bool IsDiscordAlertsStopped => _alertStop != AlertStop.None && _savedSettings.DiscordAlerts;
    public bool CanSaveDiscordWebhook => _alertsPermitted && _ready && !IsLocked && !_shuttingDown && !_webhookBusy;
    public bool CanClearDiscordWebhook => CanSaveDiscordWebhook && HasDiscordWebhook;
    public bool CanSendTestAlert => IsDiscordAlertsActive && HasDiscordWebhook && _ready && !IsLocked && !_shuttingDown;
    public bool CanResumeDiscordAlerts => IsDiscordAlertsStopped && _alertsPermitted && _ready && !IsLocked && !_shuttingDown;
    public string DiscordWebhookStatus { get => _discordWebhookStatus; private set => Set(ref _discordWebhookStatus, value); }
    public string DiscordAlertStatus { get => _discordAlertStatus; private set => Set(ref _discordAlertStatus, value); }
    /// <summary>Parameter: the URL from the masked field, as text. It is validated, saved protected and forgotten.</summary>
    public AsyncCommand SaveDiscordWebhook { get; private set; } = null!;
    public AsyncCommand ClearDiscordWebhook { get; private set; } = null!;
    public AsyncCommand SendTestAlert { get; private set; } = null!;
    public Command ResumeDiscordAlerts { get; private set; } = null!;
    /// <summary>Tests: the account-label lookups queued for client alerts; completed when there are none.</summary>
    internal Task DiscordAlertWork => _alertLabelWork;

    private void InitializeDiscordAlertFeatures(IDiscordWebhookStore? store, IDiscordWebhookTransport? transport)
    {
        // Sample mode shows the controls but sends nothing unless a test injects its own transport.
        _alertsPermitted = !IsSample || transport is not null;
        _webhookStore = store ?? new DiscordWebhookStore(DataRoot);
        _webhookTransport = transport ?? new DiscordWebhookClient();
        _alertOutbox = new DiscordAlertOutbox(_webhookTransport, ReadWebhookAddressAsync);
        _alertOutbox.Reported += AlertReported;
        if (!_alertsPermitted) _discordAlertStatus = DiscordAlertsSample;
        SaveDiscordWebhook = new(SaveDiscordWebhookAsync, error => ReportWebhookStoreFailure(error, "The webhook could not be saved. Nothing was changed."));
        ClearDiscordWebhook = new(_ => ClearDiscordWebhookAsync(), error => ReportWebhookStoreFailure(error, "The saved webhook could not be removed."));
        SendTestAlert = new(_ => { QueueTestAlert(); return Task.CompletedTask; }, error => Fail(error, ManagerSubsystem.DiscordAlerts, "The test alert could not be queued."));
        ResumeDiscordAlerts = new(_ => ResumeAlerts());
        PropertyChanged += DiscordAlertOwnerChanged;
    }

    private void DiscordAlertOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IsReady) or nameof(IsLocked)) RaiseDiscordAlertAvailability();
    }
    private void RaiseDiscordAlertAvailability()
    {
        Raise(nameof(IsDiscordAlertsActive)); Raise(nameof(IsDiscordAlertsStopped)); Raise(nameof(HasDiscordWebhook));
        Raise(nameof(CanSaveDiscordWebhook)); Raise(nameof(CanClearDiscordWebhook)); Raise(nameof(CanSendTestAlert)); Raise(nameof(CanResumeDiscordAlerts));
    }

    // The outbox's address source: read from the protected store for this one send, disposed by the outbox after it (Rule 10).
    private Task<DiscordWebhookAddress?> ReadWebhookAddressAsync(CancellationToken ct) => _webhookStore.ReadAsync(ct);

    // Startup, after settings: what Settings may say about the saved webhook. A damaged file is an error, never a stopped startup.
    private async Task LoadDiscordWebhookSummaryAsync()
    {
        if (!_alertsPermitted) return;
        try { _webhookSummary = await Task.Run(() => _webhookStore.ReadSummaryAsync(_lifetime.Token)); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _webhookSummary = null;
            Fail(error, ManagerSubsystem.DiscordAlerts, "The saved Discord webhook could not be read. Save it again, or choose Clear webhook; no alert is sent until then.");
        }
        ShowWebhookSummary();
    }
    private void ShowWebhookSummary()
    {
        DiscordWebhookStatus = _webhookSummary?.Description ?? NoDiscordWebhook;
        RaiseDiscordAlertAvailability();
    }

    // UI thread, after settings load and after each explicit Save settings: run, or stop, to match the saved choice. Saving the choice
    // on after it was off is an explicit start, so it clears a stop; saving it unchanged never restarts a stopped outbox (Rule 11).
    private void ApplySavedDiscordAlerts(bool wasOn)
    {
        if (!_alertsPermitted || _shuttingDown) return;
        bool on = _savedSettings.DiscordAlerts;
        if (on && !wasOn) { _alertStop = AlertStop.None; _alertFailureShown = false; }
        if (on && _alertStop == AlertStop.None) _alertOutbox.Start();
        else if (!on) { _alertOutbox.Stop(); _alertStop = AlertStop.None; }
        RefreshDiscordAlertStatus();
    }

    private void RefreshDiscordAlertStatus()
    {
        DiscordAlertStatus = !_alertsPermitted ? DiscordAlertsSample
            : !_savedSettings.DiscordAlerts ? DiscordAlertsOff
            : _alertStop == AlertStop.Panic ? "Stopped by Panic. Nothing is sent, and alerts for what happens meanwhile are not kept. Choose Resume alerts to send alerts again."
            : _alertStop == AlertStop.RefusedWebhook ? "Stopped: " + _lastAlertProblem + " Save a new webhook URL, or choose Resume alerts to try the saved one again."
            : !HasDiscordWebhook ? "On, but no webhook URL is saved, so nothing is sent. Save a webhook below."
            : _lastAlertProblem.Length > 0 ? "On. The last alert was not sent: " + _lastAlertProblem + " It was not retried; later alerts are still sent."
            : _alertsSent == 0 ? "On. Waiting for events: a client that crashes, stops responding or is disconnected, or a launch batch that finishes."
            : _alertsSent == 1 ? "On. 1 alert sent, at " + _lastAlertAt.ToString("T", CultureInfo.CurrentCulture) + "."
            : "On. " + _alertsSent.ToString(CultureInfo.CurrentCulture) + " alerts sent, the last at " + _lastAlertAt.ToString("T", CultureInfo.CurrentCulture) + ".";
        RaiseDiscordAlertAvailability();
    }

    private async Task SaveDiscordWebhookAsync(object? parameter)
    {
        if (!CanSaveDiscordWebhook) return;
        if (!DiscordWebhookAddress.TryParse(parameter as string, out var address, out string problem))
        {
            // The problem text never repeats what was typed, so an almost-URL cannot leak through the error line.
            Fail(new ArgumentException(problem), ManagerSubsystem.DiscordAlerts, problem + " Nothing was saved.");
            return;
        }
        using (address)
        {
            SetWebhookBusy(true);
            try { await Task.Run(() => _webhookStore.SaveAsync(address, _lifetime.Token)); }
            finally { SetWebhookBusy(false); }
            if (_shuttingDown) return;
            _webhookSummary = new(address.Host, address.Last4);
        }
        _diagnostics.Record(ManagerEventKind.DiscordWebhookSaved);
        AddEvent("Discord webhook saved for " + _webhookSummary.Host + " (token ends with …" + _webhookSummary.Last4 + ").");
        // A new URL answers a refusal of the old one; a Panic stop is the user's to lift.
        if (_alertStop == AlertStop.RefusedWebhook) { _alertStop = AlertStop.None; _alertFailureShown = false; _lastAlertProblem = ""; }
        ShowWebhookSummary();
        ApplySavedDiscordAlerts(wasOn: _savedSettings.DiscordAlerts);
        Inform(_savedSettings.DiscordAlerts ? "Discord webhook saved. Alerts go to it from now on; Send test alert checks it."
            : "Discord webhook saved. Turn on Send Discord alerts and save settings to use it.");
    }

    private async Task ClearDiscordWebhookAsync()
    {
        if (!CanClearDiscordWebhook) return;
        SetWebhookBusy(true);
        try { await Task.Run(() => _webhookStore.ClearAsync(_lifetime.Token)); }
        finally { SetWebhookBusy(false); }
        if (_shuttingDown) return;
        _webhookSummary = null;
        _diagnostics.Record(ManagerEventKind.DiscordWebhookCleared);
        AddEvent("Discord webhook removed.");
        ShowWebhookSummary(); RefreshDiscordAlertStatus();
        Inform("Discord webhook removed. Nothing is sent until a new one is saved.");
    }

    private void SetWebhookBusy(bool busy) { _webhookBusy = busy; RaiseDiscordAlertAvailability(); }

    private void ReportWebhookStoreFailure(Exception error, string message)
    {
        if (!_shuttingDown) Fail(error, ManagerSubsystem.DiscordAlerts, message);
    }

    private void QueueTestAlert()
    {
        if (!CanSendTestAlert) return;
        _alertOutbox.Enqueue(new AlertEvent(AlertKind.Test, DateTimeOffset.UtcNow));
        Inform("Test alert queued for the saved Discord webhook.");
    }

    /// <summary>The explicit start after Panic or a refused webhook. It acts on nothing by itself; the next event does.</summary>
    private void ResumeAlerts()
    {
        if (!CanResumeDiscordAlerts) return;
        _alertStop = AlertStop.None; _alertFailureShown = false; _lastAlertProblem = "";
        _alertOutbox.Start();
        RefreshDiscordAlertStatus();
        Inform("Discord alerts resumed. Zenith sends the next alert that happens.");
    }

    // Dispatcher, from the presentation step, right after the presence history took the same scan and log evidence: the tracker
    // always observes, so a baseline is taken and a state that began while alerts were off or stopped never alerts later.
    private void ObserveClientAlerts(DateTimeOffset now)
    {
        if (RecentClients.Snapshot() is not { } view) return;
        var alerts = _clientAlerts.Observe(view, now);
        if (alerts.Count == 0 || !_alertOutbox.IsRunning || _shuttingDown) return;
        foreach (var alert in alerts)
        {
            var previous = _alertLabelWork;
            _alertLabelWork = ResolveAndEnqueueAsync(alert, previous);
        }
    }

    // One tracked chain on the dispatcher (Rule 12): each alert finds its account label, then joins the outbox in order.
    private async Task ResolveAndEnqueueAsync(ClientAlert alert, Task previous)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        string? label = null;
        try { label = await ResolveAlertLabelAsync(alert.UserId, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.DiscordAlerts, error.GetType()); }
        if (_shuttingDown) return;
        _alertOutbox.Enqueue(new AlertEvent(alert.Kind, alert.Utc, label, alert.PlaceId, alert.DisconnectReason));
    }

    /// <summary>
    /// UI thread. The label a saved account shows for the client's last reported player ID: the loaded Accounts page first (the same
    /// label the page shows), then the protected store's identity index (a saved sign-in's verified identity, by player ID; no
    /// credential is read). Null when no saved account matches: the message then names no one.
    /// </summary>
    internal async Task<string?> ResolveAlertLabelAsync(long? userId, CancellationToken ct)
    {
        if (userId is not > 0) return null;
        if (Accounts.FirstOrDefault(row => row.KnownUserId == userId || row.Profile.RobloxUserId == userId) is { } row) return row.Label;
        var summary = await Task.Run(() => _vault.FindByRobloxUserIdAsync(userId.Value, ct), CancellationToken.None);
        if (summary is null) return null;
        if (summary.VerifiedUsername.Length > 0) return summary.VerifiedUsername;
        var profile = await Task.Run(() => _accounts.FindAsync(summary.ProfileId, ct), CancellationToken.None);
        return profile?.Label;
    }

    // Dispatcher, from the batch enqueue: the requests the queue recorded for one Queue checked accounts are one batch.
    private void NoteLaunchBatchRecorded(IReadOnlyList<LaunchBatchRow> rows, long placeId)
    {
        var ids = rows.Where(row => row is { Disposition: LaunchBatchDisposition.Recorded, Operation: not null })
            .Select(row => row.Operation!.Intent.OperationId).ToArray();
        if (ids.Length == 0) return;
        _batchAlerts.Track(ids, placeId);
        // The queue's change step may already have shown these rows settled; look once now so such a batch is not missed.
        ObserveLaunchBatchAlerts(Volatile.Read(ref _latestQueue));
    }

    // Dispatcher, from the queue's own change step: every settled request is seen once there.
    private void ObserveLaunchBatchAlerts(LaunchQueueSnapshot snapshot)
    {
        var alerts = _batchAlerts.Observe(snapshot, DateTimeOffset.UtcNow);
        if (alerts.Count == 0 || !_alertOutbox.IsRunning || _shuttingDown) return;
        foreach (var alert in alerts) _alertOutbox.Enqueue(alert);
    }

    // The outbox's worker thread: only a hop to the dispatcher happens here.
    private void AlertReported(AlertDeliveryReport report)
    {
        if (Volatile.Read(ref _shuttingDown) || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() => ApplyAlertReport(report)));
    }

    private void ApplyAlertReport(AlertDeliveryReport report)
    {
        if (_shuttingDown) return;
        var result = report.Result;
        switch (result.Status)
        {
            case AlertDeliveryStatus.Delivered:
                _alertsSent += report.Alerts; _lastAlertAt = DateTimeOffset.Now; _lastAlertProblem = ""; _alertFailureShown = false;
                _diagnostics.Record(ManagerEventKind.DiscordAlertSent);
                break;
            case AlertDeliveryStatus.NoWebhook:
                _lastAlertProblem = result.Explanation; // Not a failure: the status says what to do, no error is raised.
                break;
            case AlertDeliveryStatus.Rejected:
                // Sending more would only repeat the refusal: stop, and say so even if an earlier failure was already shown.
                _alertOutbox.Stop(); _alertStop = AlertStop.RefusedWebhook;
                ReportAlertFailure(typeof(InvalidOperationException), result.Explanation + " Alerts are stopped until you save a new webhook URL or choose Resume alerts.", force: true);
                break;
            default:
                ReportAlertFailure(result.ErrorType ?? (result.StatusCode is null ? typeof(TimeoutException) : typeof(HttpRequestException)),
                    result.Explanation + " The alert was not retried; later alerts are still sent.");
                break;
        }
        RefreshDiscordAlertStatus();
    }

    // Every failure is kept by type (Rule 10); only the first since the last success is shown, so a Discord outage is one error line,
    // not one per alert. A forced report (a refused webhook) is shown even then.
    private void ReportAlertFailure(Type type, string message, bool force = false)
    {
        _lastAlertProblem = message;
        _diagnostics.RecordFailure(ManagerSubsystem.DiscordAlerts, type);
        if (_alertFailureShown && !force) { RefreshDiagnosticsView(); return; }
        _alertFailureShown = true;
        ShowError(message);
        AddEvent("Operation failed (" + type.Name + ").");
        RefreshDiagnosticsView();
    }

    // Panic step. Lock keeps alerts running (see the file comment). Panic drops what is queued, cancels the send in flight and keeps
    // the outbox stopped until Resume alerts or the choice is saved on after being off.
    private void StopDiscordAlertOperations(bool locking)
    {
        if (locking || !_savedSettings.DiscordAlerts || !_alertsPermitted) return;
        _alertOutbox.Stop();
        _alertStop = AlertStop.Panic;
        RefreshDiscordAlertStatus();
    }

    // Exit's synchronous stage: nothing new is queued and the send in flight is cancelled.
    private void StopDiscordAlertOutbox() => _alertOutbox.Stop();

    private async Task DisposeDiscordAlertFeaturesAsync()
    {
        PropertyChanged -= DiscordAlertOwnerChanged;
        _alertOutbox.Reported -= AlertReported;
        StopDiscordAlertOutbox();
        try { await _alertOutbox.DisposeAsync(); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.DiscordAlerts, error.GetType()); }
        await _alertLabelWork.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        try { await Task.WhenAll(SaveDiscordWebhook.Execution, ClearDiscordWebhook.Execution, SendTestAlert.Execution); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.DiscordAlerts, error.GetType()); }
        try { await _webhookTransport.DisposeAsync(); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.DiscordAlerts, error.GetType()); }
        try { (_webhookStore as IDisposable)?.Dispose(); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.DiscordAlerts, error.GetType()); }
        RaiseDiscordAlertAvailability();
    }
}
