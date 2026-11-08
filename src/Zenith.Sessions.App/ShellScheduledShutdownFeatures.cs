using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

// Scheduled Windows shutdown (R-shell-21): an explicit action on Settings, never a saved preference, so nothing is scheduled
// unless the user asks in this run. Zenith asks Windows' own shutdown tool (shutdown.exe /s /t and /a) and shows the countdown
// on Settings and in the footer, redrawn once a second only while a shutdown is scheduled: a countdown is periodic by nature,
// and nothing ticks otherwise (Rule 14). Panic calls a shutdown Zenith scheduled off, because Panic promises that clients remain
// open, and it never starts one. Lock leaves the schedule in place, because the idle lock would otherwise cancel the very
// shutdown the user walked away from. Tray Exit cancels it, so nothing Zenith started outlives its countdown and Cancel
// (Rule 13); a Windows session end never cancels, since that end may be this shutdown arriving.
public sealed partial class ShellViewModel
{
    private const string NoShutdownScheduled = "No shutdown is scheduled.";
    private const string ShutdownNow = "Windows is shutting down this PC now.";
    private const int ShutdownAccessDenied = 5;
    private enum ShutdownCancelOrigin { User, Panic, Exit }
    private IShutdownScheduler _shutdownScheduler = null!;
    private DispatcherTimer? _shutdownCountdownTimer;
    private Task _shutdownWork = Task.CompletedTask;
    private bool _shutdownPermitted, _shutdownBusy, _shutdownScheduled, _shutdownMayBeHeldElsewhere, _windowsSessionEnding;
    // An unconfirmed request may have installed our schedule. Keep that cleanup obligation separate from a confirmed
    // preexisting schedule, which only the user's explicit Cancel may abort.
    private bool _shutdownAttemptUnconfirmed;
    private long _shutdownGeneration, _shutdownScheduledAt;
    private TimeSpan _shutdownDelay;
    private DateTimeOffset _shutdownDueAt;
    private int _shutdownMinutes = ScheduledShutdownPolicy.DefaultMinutes;
    private string _shutdownStatus = NoShutdownScheduled, _shutdownCountdown = "";

    public IReadOnlyList<ShutdownDelayChoice> ShutdownDelayChoices => ScheduledShutdownPolicy.Choices;
    /// <summary>The delay the next Schedule shutdown uses. Not saved: a shutdown is never scheduled from a settings file.</summary>
    public int ShutdownMinutes { get => _shutdownMinutes; set { if (Set(ref _shutdownMinutes, value)) Raise(nameof(CanScheduleShutdown)); } }
    public bool IsShutdownScheduled => _shutdownScheduled;
    public bool CanScheduleShutdown => _shutdownPermitted && _ready && !IsLocked && !_shuttingDown && !_shutdownBusy && !_shutdownScheduled
        && ScheduledShutdownPolicy.IsValidMinutes(_shutdownMinutes);
    /// <summary>Offered for Zenith's own schedule and for one Windows reported holding: another program's, or one Zenith could not confirm.</summary>
    public bool CanCancelShutdown => _shutdownPermitted && !IsLocked && !_shuttingDown && !_shutdownBusy && (_shutdownScheduled || _shutdownMayBeHeldElsewhere);
    public string ShutdownStatus { get => _shutdownStatus; private set => Set(ref _shutdownStatus, value); }
    /// <summary>The footer line while a shutdown is scheduled: the time and the countdown.</summary>
    public string ShutdownCountdown { get => _shutdownCountdown; private set => Set(ref _shutdownCountdown, value); }
    public AsyncCommand ScheduleShutdown { get; private set; } = null!;
    public AsyncCommand CancelShutdown { get; private set; } = null!;
    internal TimeSpan? ShutdownRemaining => _shutdownScheduled ? Remaining() : null;
    internal bool ShutdownCountdownArmed => _shutdownCountdownTimer is { IsEnabled: true };
    /// <summary>Tests: the cancel Panic started, or the one a schedule accepted after Panic started; completed when there is none.</summary>
    internal Task ShutdownWork => _shutdownWork;

    private void InitializeScheduledShutdownFeatures(IShutdownScheduler? scheduler)
    {
        // Sample mode shows the controls but never asks Windows for anything unless a test injects its own scheduler.
        _shutdownPermitted = !IsSample || scheduler is not null;
        _shutdownScheduler = scheduler ?? new WindowsShutdownScheduler();
        if (!_shutdownPermitted) _shutdownStatus = "Not available in sample mode: Zenith does not ask Windows to shut down while it shows sample profiles.";
        ScheduleShutdown = new(ScheduleShutdownAsync, error => ReportShutdownToolFailure(error, "The shutdown tool could not be run, so nothing was scheduled."));
        CancelShutdown = new(_ => CancelScheduledShutdownAsync(ShutdownCancelOrigin.User, _lifetime.Token),
            error => ReportShutdownToolFailure(error, "The shutdown tool could not be run, so the shutdown may still be scheduled. Try Cancel shutdown again, or run shutdown /a yourself."));
        PropertyChanged += ScheduledShutdownOwnerChanged;
    }

    private void ScheduledShutdownOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IsReady) or nameof(IsLocked)) RaiseShutdownAvailability();
    }
    private void RaiseShutdownAvailability() { Raise(nameof(IsShutdownScheduled)); Raise(nameof(CanScheduleShutdown)); Raise(nameof(CanCancelShutdown)); }
    private void SetShutdownBusy(bool busy) { _shutdownBusy = busy; RaiseShutdownAvailability(); }
    private static string Clock(DateTimeOffset at) => at.ToString("t", CultureInfo.CurrentCulture);

    private async Task ScheduleShutdownAsync(object? parameter)
    {
        if (!CanScheduleShutdown) return;
        int minutes = _shutdownMinutes;
        var delay = TimeSpan.FromMinutes(minutes);
        long generation = _shutdownGeneration;
        SetShutdownBusy(true);
        ShutdownStatus = "Asking Windows to schedule the shutdown…";
        ShutdownScheduleResult result;
        try { result = await _shutdownScheduler.ScheduleAsync(delay, _lifetime.Token); }
        finally { SetShutdownBusy(false); }
        switch (result.Status)
        {
            case ShutdownScheduleStatus.Scheduled:
                _shutdownScheduled = true; _shutdownMayBeHeldElsewhere = false; _shutdownAttemptUnconfirmed = false;
                _shutdownScheduledAt = _clock.GetTimestamp(); _shutdownDelay = delay; _shutdownDueAt = _clock.GetLocalNow() + delay;
                _diagnostics.Record(ManagerEventKind.ShutdownScheduled);
                AddEvent($"Windows shutdown scheduled for {Clock(_shutdownDueAt)}, after {ScheduledShutdownPolicy.Duration(minutes)}.");
                Inform($"Shutdown scheduled: Windows will shut down this PC at {Clock(_shutdownDueAt)}. Cancel shutdown calls it off.");
                // Exit already stopped the timers; it cancels this schedule itself once the command's task is awaited.
                if (!_shuttingDown) StartShutdownCountdown();
                UpdateShutdownCountdown();
                break;
            case ShutdownScheduleStatus.AlreadyScheduled:
                _shutdownMayBeHeldElsewhere = true;
                ShutdownStatus = result.Explanation + " Cancel shutdown calls that one off; then schedule again.";
                break;
            case ShutdownScheduleStatus.AccessDenied:
                ShutdownStatus = NoShutdownScheduled;
                Fail(new Win32Exception(ShutdownAccessDenied), ManagerSubsystem.ScheduledShutdown, "Windows refused to schedule the shutdown (access denied). Nothing was scheduled.");
                break;
            case ShutdownScheduleStatus.NotConfirmed:
                _shutdownAttemptUnconfirmed = true;
                _shutdownMayBeHeldElsewhere = true;
                ShutdownStatus = "Windows could not confirm whether the shutdown was scheduled.";
                Fail(UnconfirmedShutdownFault(result), ManagerSubsystem.ScheduledShutdown, result.Explanation + " A shutdown may or may not be scheduled; Cancel shutdown clears any that Windows holds.");
                break;
            default: // Busy or Closed: the controls were disabled, so only a race can reach this; the status says what happened.
                ShutdownStatus = result.Explanation;
                break;
        }
        // Panic also owns a late, unconfirmed submission. Exit uses its independent cleanup budget below; a Windows
        // session end may be this shutdown arriving and must never abort itself.
        if ((_shutdownScheduled || _shutdownAttemptUnconfirmed) && generation != _shutdownGeneration && !_windowsSessionEnding)
        { _shutdownWork = CancelScheduledShutdownAsync(ShutdownCancelOrigin.Panic, _lifetime.Token); await _shutdownWork; }
        RaiseShutdownAvailability();
    }

    private async Task CancelScheduledShutdownAsync(ShutdownCancelOrigin origin, CancellationToken ct)
    {
        if (origin == ShutdownCancelOrigin.User && !CanCancelShutdown) return;
        SetShutdownBusy(true);
        ShutdownScheduleResult result;
        try { result = await _shutdownScheduler.CancelAsync(ct); }
        // Exit took over: it cancels again on its own budget, so the schedule stays marked until Windows confirms.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception error) when (origin != ShutdownCancelOrigin.User)
        {
            // Nobody awaits a Panic or exit cancel for its result, so it reports here; a user's Cancel reports through its command.
            ReportShutdownToolFailure(error, "The shutdown tool could not be run, so the shutdown may still be scheduled. Use Cancel shutdown, or run shutdown /a yourself.");
            return;
        }
        finally { SetShutdownBusy(false); }
        switch (result.Status)
        {
            case ShutdownScheduleStatus.Cancelled:
            case ShutdownScheduleStatus.NothingScheduled:
                ClearScheduledShutdown();
                if (result.Status == ShutdownScheduleStatus.Cancelled) _diagnostics.Record(ManagerEventKind.ShutdownScheduleCancelled);
                string what = result.Status == ShutdownScheduleStatus.NothingScheduled ? result.Explanation : origin switch
                {
                    ShutdownCancelOrigin.Panic => "Panic cancelled the scheduled shutdown.",
                    ShutdownCancelOrigin.Exit => "Exit cancelled the scheduled shutdown.",
                    _ => "The scheduled shutdown was cancelled."
                };
                ShutdownStatus = NoShutdownScheduled + " " + what;
                AddEvent(what);
                if (origin == ShutdownCancelOrigin.User && result.Status == ShutdownScheduleStatus.Cancelled) Inform("Shutdown cancelled. Windows will not shut down this PC.");
                break;
            case ShutdownScheduleStatus.AccessDenied:
                Fail(new Win32Exception(ShutdownAccessDenied), ManagerSubsystem.ScheduledShutdown, "Windows refused to cancel the shutdown (access denied). It is still scheduled; run shutdown /a yourself or try again.");
                break;
            case ShutdownScheduleStatus.NotConfirmed:
                Fail(UnconfirmedShutdownFault(result), ManagerSubsystem.ScheduledShutdown, result.Explanation + " The shutdown may still be scheduled; try Cancel shutdown again, or run shutdown /a yourself.");
                break;
            default: // Busy or Closed.
                ShutdownStatus = result.Explanation;
                break;
        }
        RaiseShutdownAvailability();
    }

    // Exit codes are Windows error codes; an unconfirmed started request can be a timeout, cancellation or wait failure.
    // Keep a fixed diagnostic type/message rather than any tool output (Rule 10).
    private static Exception UnconfirmedShutdownFault(ShutdownScheduleResult result)
        => result.ErrorCode is { } code ? new Win32Exception(code) : new InvalidOperationException("Windows' shutdown tool did not return a confirmed result.");

    private void ReportShutdownToolFailure(Exception error, string message)
    {
        if (!_shutdownScheduled && !_shutdownAttemptUnconfirmed) ShutdownStatus = NoShutdownScheduled;
        Fail(error, ManagerSubsystem.ScheduledShutdown, message);
    }

    private void ClearScheduledShutdown()
    {
        _shutdownScheduled = false; _shutdownMayBeHeldElsewhere = false; _shutdownAttemptUnconfirmed = false;
        StopShutdownCountdown();
        ShutdownCountdown = "";
    }

    private TimeSpan Remaining() => _shutdownDelay - _clock.GetElapsedTime(_shutdownScheduledAt);

    private void StartShutdownCountdown()
    {
        if (_shutdownCountdownTimer is null)
        {
            _shutdownCountdownTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = ScheduledShutdownPolicy.CountdownTick };
            _shutdownCountdownTimer.Tick += ShutdownCountdownTick;
        }
        _shutdownCountdownTimer.Start();
    }
    private void ShutdownCountdownTick(object? sender, EventArgs e) => UpdateShutdownCountdown();
    /// <summary>UI thread: redraws the countdown from the clock. Once the time is up it stops ticking; Windows is doing the rest.</summary>
    internal void TickShutdownCountdown() => UpdateShutdownCountdown();
    private void UpdateShutdownCountdown()
    {
        if (!_shutdownScheduled) return;
        var remaining = Remaining();
        if (remaining <= TimeSpan.Zero)
        {
            _shutdownCountdownTimer?.Stop();
            ShutdownStatus = ShutdownNow; ShutdownCountdown = ShutdownNow;
            return;
        }
        string countdown = ScheduledShutdownPolicy.Countdown(remaining), at = Clock(_shutdownDueAt);
        ShutdownStatus = $"Windows will shut down this PC at {at}, in {countdown}. It will close every program, Roblox clients included, without saving their work. Cancel shutdown calls it off.";
        ShutdownCountdown = $"Shutdown at {at}, in {countdown}";
    }
    private void StopShutdownCountdown()
    {
        if (_shutdownCountdownTimer is not { } timer) return;
        timer.Stop(); timer.Tick -= ShutdownCountdownTick; _shutdownCountdownTimer = null;
    }

    // Panic and Lock step. Lock keeps the schedule (see the file comment). Panic calls a shutdown Zenith scheduled off, tracked in
    // ShutdownWork and reported on its own, and advances the generation so a schedule Windows accepts after this moment is cancelled
    // too. A user's Cancel already under way finishes the job itself. Panic never starts a shutdown.
    private void StopScheduledShutdownOperations(bool locking)
    {
        if (locking) return;
        _shutdownGeneration++;
        if ((!_shutdownScheduled && !_shutdownAttemptUnconfirmed) || _shutdownBusy) return;
        _shutdownWork = CancelScheduledShutdownAsync(ShutdownCancelOrigin.Panic, _lifetime.Token);
    }

    private async Task DisposeScheduledShutdownFeaturesAsync()
    {
        StopShutdownCountdown();
        PropertyChanged -= ScheduledShutdownOwnerChanged;
        // A request still with Windows finishes first (the tool is bounded), so the state read below is settled.
        try { await Task.WhenAll(ScheduleShutdown.Execution, CancelShutdown.Execution, _shutdownWork); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.ScheduledShutdown, error.GetType()); }
        StopShutdownCountdown();
        // Tray Exit only: Zenith cancels the shutdown it scheduled, so nothing it started outlives its countdown and Cancel. A Windows
        // session end leaves it alone: that end may be this very shutdown, and aborting it would undo what the user asked for.
        if ((_shutdownScheduled || _shutdownAttemptUnconfirmed) && !_windowsSessionEnding)
            await CancelScheduledShutdownAsync(ShutdownCancelOrigin.Exit, CancellationToken.None);
        try { await _shutdownScheduler.DisposeAsync(); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.ScheduledShutdown, error.GetType()); }
    }
}
