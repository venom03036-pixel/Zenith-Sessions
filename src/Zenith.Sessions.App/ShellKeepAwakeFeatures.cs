using System.Windows.Threading;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

// Keep clients awake (R-instances-17, owner decision 2026-11-08), off by default. Every saved gap (5 to 19 minutes; Roblox disconnects
// an idle client after 20) one tick brings each client in the latest fresh scan forward, sends it one F13 press through the one
// targeted input owner (KeepAwakeRunner over TargetedInputCoordinator; never a second input path, Rule 4) and puts the earlier
// foreground window back. The project's own gate (CapabilityGate) refuses every tick until the live input isolation trial marks
// background input Supported, exactly like Launch and run; until then the saved choice only shows why nothing runs. A tick waits
// while the user touched the keyboard or mouse in the last ten seconds, so the foreground is never taken from them. One
// DispatcherTimer, re-armed after each tick (a scheduled action, so a timer is the right tool; nothing polls between ticks). Panic and
// Lock cancel the tick in flight and stop the feature for this run until Resume keeping clients awake, or until the choice is saved on
// after being off (Rule 11); exit cancels and joins it (Rule 12). Failures are kept by type only (Rule 10).
public sealed partial class ShellViewModel
{
    internal const string KeepAwakeOffStatus = "Off. No client is brought forward or sent a key.";
    private CapabilityGate _capabilityGate = null!;
    private KeepAwakeRunner? _keepAwakeRunner;
    private IUserInputIdleSource _userIdle = null!;
    private DispatcherTimer? _keepAwakeTimer;
    private CancellationTokenSource? _keepAwakeStop;
    private Task _keepAwakeTick = Task.CompletedTask;
    private int _keepAwakeMinutes;
    private bool _keepAwakeStopped;
    private long _keepAwakeGeneration;
    private string _keepAwakeStatus = KeepAwakeOffStatus;
    private IReadOnlyList<KeepAwakeChoice> _keepAwakeChoices = KeepAwakePolicy.ChoicesFor(0);
    private Command? _resumeKeepAwake;

    public IReadOnlyList<KeepAwakeChoice> KeepAwakeChoices { get => _keepAwakeChoices; private set => Set(ref _keepAwakeChoices, value); }
    /// <summary>Settings draft: send one F13 press to each client every this many minutes; 0 is Off. Applies after Save settings.</summary>
    public int KeepAwakeMinutes { get => _keepAwakeMinutes; set { if (Set(ref _keepAwakeMinutes, value)) PreferencesEdited(); } }
    /// <summary>What the feature is doing now, in plain words: off, refused by the gate, waiting, the last tick's outcome, or stopped.</summary>
    public string KeepAwakeStatus { get => _keepAwakeStatus; private set => Set(ref _keepAwakeStatus, value); }
    /// <summary>Tests: how long the armed keep-awake timer waits, or null when none is armed.</summary>
    internal TimeSpan? KeepAwakeDue => _keepAwakeTimer is { IsEnabled: true } timer ? timer.Interval : null;
    /// <summary>Tests and exit: the tick in flight, or a completed task.</summary>
    internal Task KeepAwakeWork => _keepAwakeTick;
    private int SavedKeepAwakeMinutes => _savedSettings.KeepAwakeMinutes;
    /// <summary>Panic or Lock stopped the feature, the saved choice is on, and the dashboard is unlocked.</summary>
    public bool CanResumeKeepAwake => _keepAwakeStopped && SavedKeepAwakeMinutes > 0 && !IsLocked && !_shuttingDown && _ready
        && !IsSample && _keepAwakeRunner is not null && _capabilityGate.Check(GatedFeature.KeepClientsAwake).Permitted;
    /// <summary>The explicit restart after Panic or Lock. The first press follows after the saved gap.</summary>
    public Command ResumeKeepAwake => _resumeKeepAwake ??= new(_ =>
    {
        if (!CanResumeKeepAwake) return;
        _keepAwakeStopped = false; Raise(nameof(CanResumeKeepAwake));
        ScheduleKeepAwake();
    });

    private void InitializeKeepAwakeFeatures(IKeepAwakeInputExecutor? input, IForegroundWindowSwitch? foreground, IUserInputIdleSource? userIdle, CapabilityGate? gate)
    {
        // Production has no verified input transport yet (Z1702), so no runner: the gate refuses first, and this says so after it.
        _keepAwakeRunner = input is null ? null : new(input, foreground ?? new WindowsForegroundWindowSwitch());
        _userIdle = userIdle ?? new WindowsUserInputIdleSource();
        _capabilityGate = gate ?? new();
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsLocked) or nameof(IsReady)) Raise(nameof(CanResumeKeepAwake));
            if (args.PropertyName == nameof(IsReady) && _ready) ScheduleKeepAwake();
        };
    }

    // UI thread, after settings load or a save that changed the choice. Saving it on after it was off also clears a Panic or Lock stop.
    private void ApplySavedKeepAwake(int previousMinutes)
    {
        if (previousMinutes == 0 && SavedKeepAwakeMinutes > 0) _keepAwakeStopped = false;
        KeepAwakeChoices = KeepAwakePolicy.ChoicesFor(SavedKeepAwakeMinutes);
        Raise(nameof(CanResumeKeepAwake));
        ScheduleKeepAwake();
    }

    /// <summary>Says what the saved choice means now and arms the one timer for the first press when something will run.</summary>
    private void ScheduleKeepAwake()
    {
        _keepAwakeGeneration++;
        _keepAwakeStop?.Cancel();
        _keepAwakeTimer?.Stop();
        if (_shuttingDown) return;
        int minutes = SavedKeepAwakeMinutes;
        if (minutes == 0) { KeepAwakeStatus = KeepAwakeOffStatus; return; }
        if (_keepAwakeStopped || IsLocked) { KeepAwakeStatus = KeepAwakeStoppedStatus; return; }
        var gate = _capabilityGate.Check(GatedFeature.KeepClientsAwake);
        if (!gate.Permitted) { KeepAwakeStatus = gate.Explanation; return; }
        if (IsSample) { KeepAwakeStatus = "Sample mode: Keep clients awake is saved but sends nothing, because sample mode has no real clients."; return; }
        if (_keepAwakeRunner is null) { KeepAwakeStatus = "Keep clients awake is saved but not running: this build has no verified input transport for Roblox clients yet. Nothing was sent to any client."; return; }
        if (!_ready) { KeepAwakeStatus = "Waiting for the manager to finish opening the library. No client is brought forward or sent a key."; return; }
        ArmKeepAwake(TimeSpan.FromMinutes(minutes));
        KeepAwakeStatus = $"On. Every {KeepAwakePolicy.Duration(minutes)} each client is brought forward, sent one F13 press through the input owner, and the window that was in front is put back. First press in {KeepAwakePolicy.Duration(minutes)}.";
    }
    private const string KeepAwakeStoppedStatus = "Stopped by Panic or Lock. No client is brought forward or sent a key until you choose Resume keeping clients awake.";

    /// <summary>
    /// UI thread: what the timer runs when it wakes. One tick at a time, kept as <see cref="KeepAwakeWork"/> so exit joins it
    /// (Rule 12); the next is armed only after this one has ended. It catches its own failures, so the handle never faults.
    /// </summary>
    internal Task RunKeepAwakeTickAsync() => _keepAwakeTick.IsCompleted ? _keepAwakeTick = RunKeepAwakeTickCoreAsync() : _keepAwakeTick;
    private async Task RunKeepAwakeTickCoreAsync()
    {
        _keepAwakeTimer?.Stop();
        if (_shuttingDown || IsLocked || _keepAwakeStopped || !_ready) return;
        int minutes = SavedKeepAwakeMinutes;
        var gate = _capabilityGate.Check(GatedFeature.KeepClientsAwake);
        TimeSpan? idle;
        try { idle = _userIdle.IdleFor; }
        catch (Exception ex) { _diagnostics.RecordFailure(ManagerSubsystem.KeepAwake, ex.GetType()); idle = null; }
        switch (KeepAwakePolicy.Decide(minutes, gate.Permitted, idle, out var wait))
        {
            case KeepAwakeStep.Off: KeepAwakeStatus = KeepAwakeOffStatus; return;
            case KeepAwakeStep.Refused: KeepAwakeStatus = gate.Explanation; return;
            case KeepAwakeStep.Defer:
                KeepAwakeStatus = "Waiting until you have left the keyboard and mouse alone for 10 seconds, so the press never takes a window from you.";
                ArmKeepAwake(wait); return;
        }
        if (IsSample || _keepAwakeRunner is not { } runner) { ScheduleKeepAwake(); return; }
        var clients = KeepAwakeClients();
        if (clients.Length == 0)
        {
            KeepAwakeStatus = $"No client with a window to keep awake in the latest scan. Next check in {KeepAwakePolicy.Duration(minutes)}.";
            ArmKeepAwake(wait); return;
        }
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        long generation = _keepAwakeGeneration;
        _keepAwakeStop = stop;
        try
        {
            var result = await runner.RunAsync(clients, stop.Token);
            if (stop.IsCancellationRequested || generation != _keepAwakeGeneration) return;
            KeepAwakeStatus = result.Summary + $" Next press in {KeepAwakePolicy.Duration(minutes)}.";
            AddEvent($"Keep clients awake: sent F13 to {result.Pressed} of {result.Clients.Count} clients.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        catch (Exception ex) when (!stop.IsCancellationRequested && generation == _keepAwakeGeneration)
        {
            Fail(ex, ManagerSubsystem.KeepAwake, "Keep clients awake could not finish a press. Only the error type was saved to Diagnostics; the next press is still scheduled.");
        }
        catch (Exception) { return; } // A replaced/cancelled owner cannot publish over the current schedule.
        finally
        {
            if (ReferenceEquals(_keepAwakeStop, stop)) _keepAwakeStop = null;
            stop.Dispose();
        }
        if (!_shuttingDown && !_keepAwakeStopped && !IsLocked && generation == _keepAwakeGeneration) ArmKeepAwake(wait);
    }

    // The clients of the latest fresh, complete scan that have a window Windows answers for; a hung or unreadable row gets no key.
    private SessionObservation[] KeepAwakeClients()
    {
        if (FreshClientInventory(Volatile.Read(ref _latestDesktop), DateTimeOffset.UtcNow) is not { } rows) return [];
        return rows.Where(row => row.Problem is null && row.Window != 0 && row.WindowHung != true && !row.NotResponding).ToArray();
    }

    private void ArmKeepAwake(TimeSpan wait)
    {
        if (_keepAwakeTimer is null) { _keepAwakeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher); _keepAwakeTimer.Tick += KeepAwakeTick; }
        _keepAwakeTimer.Interval = wait; _keepAwakeTimer.Start();
    }
    private void KeepAwakeTick(object? sender, EventArgs e) { _ = RunKeepAwakeTickAsync(); } // Tracked as _keepAwakeTick, which exit joins.

    // A StopOperations step for Panic and Lock. Only ResumeKeepAwake, or saving the choice on after off, clears it; Unlock does not.
    private void StopKeepAwake()
    {
        bool active = _keepAwakeTimer is { IsEnabled: true } || !_keepAwakeTick.IsCompleted;
        if (!active) return; // Preserve the gate/transport explanation for a feature that never ran.
        _keepAwakeGeneration++;
        _keepAwakeStopped = true;
        _keepAwakeTimer?.Stop();
        _keepAwakeStop?.Cancel();
        Raise(nameof(CanResumeKeepAwake));
        if (SavedKeepAwakeMinutes > 0) KeepAwakeStatus = KeepAwakeStoppedStatus;
    }
    // Exit: nothing new may start and a tick in flight is cancelled; DisposeOwnersAsync then joins it.
    private void StopKeepAwakeForShutdown()
    {
        _keepAwakeGeneration++;
        if (_keepAwakeTimer is { } timer) { timer.Stop(); timer.Tick -= KeepAwakeTick; _keepAwakeTimer = null; }
        _keepAwakeStop?.Cancel();
    }
}
