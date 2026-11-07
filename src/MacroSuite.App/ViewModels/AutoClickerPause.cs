namespace MacroSuite.App.ViewModels;

public sealed partial class AutoClickerViewModel
{
    public Func<string, bool>? TryApplyStartHotkey { get; set; }
    public Func<bool>? EmergencyStopIsReady { get; set; }
    private readonly object _clickGate = new();
    private readonly ManualResetEventSlim _pauseWake = new(true);
    private readonly AutoResetEvent _scheduleChanged = new(false);
    private int _pauseSignalsDisposed;
    private Task _pauseAcknowledgement = Task.CompletedTask;
    private bool _clickerPaused;
    public bool IsPaused
    {
        get => Volatile.Read(ref _clickerPaused);
        private set { if (SetProperty(ref _clickerPaused,value)) { OnPropertyChanged(nameof(StartStopButtonText));RunningStateChanged?.Invoke(); } }
    }
    public Task PauseForEditorFocusAsync()
    {
        var owner = _runOwner;
        if (owner is null || _disposed) return Task.CompletedTask;
        TaskCompletionSource published;
        Task pause;
        lock (owner.Transitions)
        {
            if (owner.Finalizing || !IsRunning || IsStopping || !owner.Lease.CanContinue) return Task.CompletedTask;
            owner.PauseGeneration++;
            if (IsPaused) return owner.Pause;
            published = new TaskCompletionSource();
            pause = owner.Pause = _pauseAcknowledgement = AcknowledgePauseAsync(owner, published.Task);
            _pauseWake.Reset();
            _scheduleChanged.Set();
            // Store the pause flag before opening the task gate, then notify only after ownership exists.
            _clickerPaused = true;
        }
        published.SetResult();
        OnPropertyChanged(nameof(IsPaused)); OnPropertyChanged(nameof(StartStopButtonText)); RunningStateChanged?.Invoke();
        return pause;
    }
    private async Task AcknowledgePauseAsync(InputOwner owner, Task published)
    {
        await published;
        try { await Task.Run(() => { lock (_clickGate) ReleaseInput(owner.Lease); }); }
        catch (Exception ex)
        {
            owner.CleanupFailed = true;
            owner.Lease.SignalStop();
            WakeClicker();
            LogFailure("Pausing Auto Clicker", ex);
        }
    }
    public Task TogglePauseAsync()
    {
        var owner = _runOwner;
        if (owner is null || !IsRunning || IsStopping) return Task.CompletedTask;
        if (!IsPaused) return PauseForEditorFocusAsync();
        TaskCompletionSource published;
        Task resume;
        lock (owner.Transitions)
        {
            if (owner.Finalizing || !owner.Lease.CanContinue) return Task.CompletedTask;
            if (!owner.Resume.IsCompleted) return owner.Resume;
            published = new TaskCompletionSource();
            resume = owner.Resume = ResumeClickerAsync(owner, owner.Pause, owner.PauseGeneration, published.Task);
        }
        published.SetResult();
        return resume;
    }
    private async Task ResumeClickerAsync(InputOwner owner, Task acknowledgement, long pauseGeneration, Task published)
    {
        await published;
        await acknowledgement;
        lock (owner.Transitions)
        {
            if (owner.PauseGeneration != pauseGeneration || !ReferenceEquals(owner.Pause, acknowledgement) || owner.Finalizing || !ReferenceEquals(_runOwner, owner)
                || !owner.Lease.CanContinue || !IsRunning || IsStopping || owner.CleanupFailed || !IsPaused) return;
            _clickerPaused = false;
        }
        WakeClicker();
        OnPropertyChanged(nameof(IsPaused)); OnPropertyChanged(nameof(StartStopButtonText)); RunningStateChanged?.Invoke();
    }
    /// <summary>Safe from the emergency listener; no UI collections or dispatcher waits.</summary>
    public void SignalEmergencyStop()
    {
        if (Volatile.Read(ref _pauseSignalsDisposed) != 0) return;
        _runOwner?.Lease.SignalStop();
        _pickerOwner?.Lease.SignalStop();
        WakeClicker();
        // No native input locks, logging or UI notifications on the hook thread. Each click
        // is an atomic down/up, and the tracked run's finally owns unconditional release.
    }
    private void WakeClicker()
    {
        if (Volatile.Read(ref _pauseSignalsDisposed) != 0) return;
        try { _pauseWake.Set(); _scheduleChanged.Set(); } catch (ObjectDisposedException) { }
    }
}
