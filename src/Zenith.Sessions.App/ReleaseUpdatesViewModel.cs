using System.Windows.Threading;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>Bindable commands for the existing Settings UI; no layout changes or automatic installer execution.</summary>
public sealed class ReleaseUpdatesViewModel : Observable, IAsyncDisposable
{
    private readonly ReleaseUpdateSession _session;
    private readonly CancellationTokenSource _stop = new();
    private string _feed = "", _publisher = "", _problem = "";
    private bool _daily;
    public string FeedUri { get => _feed; set => Set(ref _feed, value); }
    public string PublisherCertificateSha256 { get => _publisher; set => Set(ref _publisher, value); }
    public bool CheckDaily { get => _daily; set => Set(ref _daily, value); }
    public string Status => _problem.Length > 0 ? _problem : _session.Status;
    public bool IsChecking => _session.IsChecking;
    public string? PendingVersion => _session.State.Pending?.Version;
    public bool InstallOnNextStart => _session.State.InstallOnNextStart;
    public AsyncCommand Save { get; }
    public AsyncCommand CheckNow { get; }
    public AsyncCommand ApproveNextStart { get; }
    public AsyncCommand CancelApproval { get; }
    public AsyncCommand DiscardPending { get; }
    public Command Stop { get; }
    public ReleaseUpdatesViewModel(string root, Dispatcher dispatcher, Func<bool> queueIdle, IReleaseUpdateBackend? backend = null, Func<bool>? canOperate = null)
    {
        _session = new(root, backend ?? new ReleaseToolUpdateBackend(Path.Combine(AppContext.BaseDirectory, "Zenith.Sessions.ReleaseTool.exe"), Path.Combine(root, "Updates"), typeof(App).Assembly.GetName().Version ?? new(0, 0)),
            ct => dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background, ct).Task);
        Save = new(async _ => { if(canOperate?.Invoke()==false)return; _problem = ""; await _session.ConfigureAsync(new(FeedUri.Trim(), PublisherCertificateSha256.Trim(), CheckDaily), _stop.Token); }, Failed);
        CheckNow = new(async _ => { if(canOperate?.Invoke()==false)return; _problem = ""; await _session.CheckNowAsync(_stop.Token); }, Failed);
        ApproveNextStart = new(_ => canOperate?.Invoke()==false?Task.CompletedTask:_session.ApproveNextStartAsync(queueIdle(), _stop.Token), Failed);
        CancelApproval = new(_ => canOperate?.Invoke()==false?Task.CompletedTask:_session.CancelApprovalAsync(_stop.Token), Failed);
        DiscardPending = new(_ => canOperate?.Invoke()==false?Task.CompletedTask:_session.DiscardPendingAsync(_stop.Token), Failed);
        Stop = new(_ => _session.CancelWork());
    }
    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            await _session.InitializeAsync(ct);
            FeedUri = _session.State.Settings.FeedUri ?? ""; PublisherCertificateSha256 = _session.State.Settings.PublisherCertificateSha256 ?? ""; CheckDaily = _session.State.Settings.CheckDaily;
            Refresh();
        }
        catch (Exception error) when (error is not OperationCanceledException) { Failed(error); }
    }
    private void Failed(Exception error) { _problem = $"Update operation failed ({error.GetType().Name}). Existing files were preserved."; Refresh(); }
    private void Refresh() { Raise(nameof(Status)); Raise(nameof(IsChecking)); Raise(nameof(PendingVersion)); Raise(nameof(InstallOnNextStart)); }
    public void CancelWork() => _session.CancelWork();
    public void BeginShutdown() { _stop.Cancel(); _session.CancelWork(); }
    public async ValueTask DisposeAsync()
    {
        BeginShutdown();
        await Task.WhenAll(Save.Execution, CheckNow.Execution, ApproveNextStart.Execution, CancelApproval.Execution, DiscardPending.Execution);
        await _session.DisposeAsync(); _stop.Dispose();
    }
}
