using System.Windows.Threading;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

// Optional idle lock, off by default. The window reports keyboard and mouse input, which only stores a timestamp. One
// one-shot DispatcherTimer wakes when the saved idle time could first have passed and re-arms for the time still left,
// so nothing polls while the user is active (Rule 14). The timer is idle while locked, and shutdown stops it (Rules 12-13).
public sealed partial class ShellViewModel
{
    private readonly IdleLockPolicy _idleLock;
    private DispatcherTimer? _idleLockTimer;
    private int _idleLockMinutes;
    private IReadOnlyList<IdleLockChoice> _idleLockChoices = IdleLockPolicy.ChoicesFor(0);
    public IReadOnlyList<IdleLockChoice> IdleLockChoices { get => _idleLockChoices; private set => Set(ref _idleLockChoices, value); }
    /// <summary>Settings draft: lock after this many minutes without keyboard or mouse input; 0 is Off. Applies after Save settings.</summary>
    public int IdleLockMinutes { get => _idleLockMinutes; set { if (Set(ref _idleLockMinutes, value)) PreferencesEdited(); } }
    /// <summary>Tests: how long the armed idle-lock timer waits, or null when none is armed.</summary>
    internal TimeSpan? IdleLockDue => _idleLockTimer is { IsEnabled: true } timer ? timer.Interval : null;
    /// <summary>UI thread: a key, mouse button or wheel reached a manager window, or the user reopened it.</summary>
    internal void NoteUserInput() => _idleLock.RecordActivity();
    /// <summary>UI thread: the pointer moved over a manager window; a repeated position is not input.</summary>
    internal void NotePointer(double x, double y) => _idleLock.RecordPointer(x, y);
    // Sign-in and native trials continue in pages Zenith opened outside its own windows. They count as activity, so the
    // idle lock never cancels them the way an explicit Lock does.
    private bool IdleLockBusy => IsAuthorizing || _nativeLaunch.IsBusy || !StartNativeTrial.Execution.IsCompleted;

    // UI thread, after settings load or a save that changed the choice: count from now with the saved time.
    private void ApplySavedIdleLock()
    {
        _idleLock.Configure(_savedSettings.IdleLockMinutes);
        IdleLockChoices = IdleLockPolicy.ChoicesFor(_savedSettings.IdleLockMinutes);
        CheckIdleLock();
    }
    private void RestartIdleLock() { _idleLock.RecordActivity(); CheckIdleLock(); }
    /// <summary>UI thread: what the timer runs when it wakes. Locks once the saved idle time has passed; otherwise re-arms.</summary>
    internal void CheckIdleLock()
    {
        _idleLockTimer?.Stop();
        // Unlock starts a fresh count, so nothing waits while locked.
        if (_shuttingDown || IsLocked) return;
        switch (_idleLock.Check(IdleLockBusy, out var wait))
        {
            case IdleLockStep.Lock:
                LockControls($"Controls locked after {IdleLockPolicy.Duration(_idleLock.Minutes)} without keyboard or mouse input. Monitoring continues; automation requires explicit restart.");
                break;
            case IdleLockStep.Wait:
                if (_idleLockTimer is null) { _idleLockTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher); _idleLockTimer.Tick += IdleLockTick; }
                _idleLockTimer.Interval = wait; _idleLockTimer.Start();
                break;
        }
    }
    private void IdleLockTick(object? sender, EventArgs e) => CheckIdleLock();
    private void StopIdleLock()
    {
        if (_idleLockTimer is not { } timer) return;
        timer.Stop(); timer.Tick -= IdleLockTick; _idleLockTimer = null;
    }
}
