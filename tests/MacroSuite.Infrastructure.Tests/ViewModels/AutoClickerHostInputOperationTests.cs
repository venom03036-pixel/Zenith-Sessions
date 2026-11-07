using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using System.IO;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed class AutoClickerHostInputOperationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static HostInputOperationLease Acquire(HostInputOperationArbiter arbiter, HostInputOperationKind kind)
    { Assert.True(arbiter.TryAcquire(kind, out var lease, out _)); return lease!; }

    [Fact]
    public async Task CompetingOwnerRejectsBeforeActivationOrNativeAttachment()
    {
        var arbiter = new HostInputOperationArbiter(); var foreign = Acquire(arbiter, HostInputOperationKind.Playback);
        var input = new Input(); var activation = new Activation();
        using var vm = new AutoClickerViewModel(input, activationService: activation, inputOperations: arbiter);
        try
        {
            vm.ToggleRun(); await vm.RunCompletion.WaitAsync(Deadline);
            Assert.Equal(0, activation.Calls); Assert.Equal(0, input.Attachments); Assert.Equal(0, input.Clicks);
            Assert.Equal(foreign.OperationId, arbiter.Snapshot.OperationId);
        }
        finally { activation.Release.TrySetResult(); await vm.ShutdownAsync().WaitAsync(Deadline); await foreign.CompleteAsync(true); }
    }

    [Fact]
    public async Task ActivationIgnoringStopKeepsReservationUntilActualReturnAndNeverClicks()
    {
        var arbiter = new HostInputOperationArbiter(); var input = new Input(); var activation = new Activation();
        using var vm = new AutoClickerViewModel(input, activationService: activation, inputOperations: arbiter);
        vm.ToggleRun(); await activation.Entered.Task.WaitAsync(Deadline);
        Task run = vm.RunCompletion; vm.StopClicker();
        try
        {
            Assert.False(run.IsCompleted); Assert.True(arbiter.Snapshot.IsBusy);
            Assert.False(arbiter.TryAcquire(HostInputOperationKind.Recording, out _, out _));
        }
        finally { activation.Release.TrySetResult(); await run.WaitAsync(Deadline); await vm.ShutdownAsync(); }
        Assert.Equal(0, input.Clicks); Assert.Equal(0, input.Releases); Assert.False(arbiter.Snapshot.IsBusy);
    }

    [Fact]
    public async Task ReadinessCallbackSeesPublishedOwnerAndReentrantStopPreventsActivation()
    {
        var input = new Input(); var arbiter = new HostInputOperationArbiter();
        using var vm = new AutoClickerViewModel(input, inputOperations: arbiter);
        bool observed = false;
        vm.EmergencyStopIsReady = () =>
        {
            observed = arbiter.Snapshot.Kind == HostInputOperationKind.AutoClicker && !vm.RunCompletion.IsCompleted;
            vm.StopClicker(); return true;
        };
        vm.ToggleRun(); await vm.RunCompletion.WaitAsync(Deadline); await vm.ShutdownAsync();
        Assert.True(observed); Assert.Equal(0, input.Clicks); Assert.Equal(0, input.Releases);
    }

    [Fact]
    public async Task ReentrantRunningNotificationCanStopTheAlreadyPublishedRun()
    {
        var input = new Input(); using var vm = new AutoClickerViewModel(input);
        bool seen = false;
        vm.RunningStateChanged += () =>
        {
            if (!vm.IsRunning || seen) return;
            seen = true; Assert.False(vm.RunCompletion.IsCompleted); vm.StopClicker();
        };
        vm.ToggleRun(); await vm.RunCompletion.WaitAsync(Deadline); await vm.ShutdownAsync();
        Assert.True(seen); Assert.Equal(0, input.Clicks); Assert.False(vm.InputOperations.Snapshot.IsBusy);
    }

    [Fact]
    public async Task ReentrantSecondViewModelCannotOverlapTheSharedOwner()
    {
        var arbiter = new HostInputOperationArbiter(); var firstInput = new Input(); var secondInput = new Input();
        using var first = new AutoClickerViewModel(firstInput, inputOperations: arbiter) { RepeatOnce = true };
        using var second = new AutoClickerViewModel(secondInput, inputOperations: arbiter) { RepeatOnce = true };
        first.RunningStateChanged += () => { if (first.IsRunning) second.ToggleRun(); };
        first.ToggleRun(); await first.RunCompletion.WaitAsync(Deadline);
        Assert.Equal(1, firstInput.Clicks); Assert.Equal(0, secondInput.Clicks); Assert.Equal(0, secondInput.Attachments);
        await first.ShutdownAsync(); await second.ShutdownAsync();
    }

    [Fact]
    public async Task PauseAndStoppedProducerRemainOwnedUntilExactReleaseAcknowledgementJoins()
    {
        using var gate = new ManualResetEventSlim();
        var input = new Input(); using var vm = new AutoClickerViewModel(input) { ClicksPerSecond = .1 };
        vm.ToggleRun(); await input.FirstClick.Task.WaitAsync(Deadline);
        input.ReleaseGate = gate;
        Task pause = vm.PauseForEditorFocusAsync();
        try
        {
            await input.ReleaseEntered.Task.WaitAsync(Deadline);
            Assert.True(vm.IsPaused);
            Assert.False(vm.InputOperations.TryAcquire(HostInputOperationKind.Playback, out _, out _));
            vm.StopClicker(); Assert.False(vm.RunCompletion.IsCompleted);
            Assert.False(vm.InputOperations.TryAcquire(HostInputOperationKind.Playback, out _, out _));
        }
        finally { gate.Set(); await pause.WaitAsync(Deadline); await vm.RunCompletion.WaitAsync(Deadline); await vm.ShutdownAsync(); }
        Assert.Equal(1, input.Clicks); Assert.False(vm.InputOperations.Snapshot.IsBusy);
        Assert.All(input.CleanupOwners, id => Assert.Equal(input.LastLease!.OperationId, id));
    }

    [Fact]
    public async Task ReentrantResumeWaitsForPublishedPauseAcknowledgement()
    {
        using var gate = new ManualResetEventSlim(); var input = new Input();
        using var vm = new AutoClickerViewModel(input) { ClicksPerSecond = .1 };
        vm.ToggleRun(); await input.FirstClick.Task.WaitAsync(Deadline);
        input.ReleaseGate = gate;
        Task? resume = null;
        vm.RunningStateChanged += () => { if (vm.IsPaused && resume is null) resume = vm.TogglePauseAsync(); };
        Task pause = vm.PauseForEditorFocusAsync();
        try
        {
            await input.ReleaseEntered.Task.WaitAsync(Deadline);
            Assert.NotNull(resume); Assert.False(resume!.IsCompleted); Assert.True(vm.IsPaused);
            vm.StopClicker();
        }
        finally { gate.Set(); await pause.WaitAsync(Deadline); if (resume is not null) await resume.WaitAsync(Deadline); await vm.RunCompletion.WaitAsync(Deadline); await vm.ShutdownAsync(); }
        Assert.Equal(1, input.Clicks);
    }

    [Fact]
    public async Task PauseReleaseFailureStillAttemptsFinalOwnedReleaseAndRequiresExplicitRecovery()
    {
        var input = new Input(); using var vm = new AutoClickerViewModel(input) { ClicksPerSecond = .1 };
        vm.ToggleRun(); await input.FirstClick.Task.WaitAsync(Deadline);
        input.FailRelease = true;
        await vm.PauseForEditorFocusAsync().WaitAsync(Deadline);
        await vm.RunCompletion.WaitAsync(Deadline);
        Assert.Equal(2, input.Releases); Assert.True(vm.InputOperations.Snapshot.IsQuarantined);
        int clicks = input.Clicks; vm.ToggleRun(); Assert.Equal(clicks, input.Clicks);
        Assert.False(vm.InputOperations.TryAcquire(HostInputOperationKind.Playback, out _, out _));
        input.FailRelease = false;
        Assert.True(await vm.RecoverInputOwnershipAsync().WaitAsync(Deadline));
        Assert.Equal(3, input.Releases); Assert.False(vm.InputOperations.Snapshot.IsBusy);
        await vm.ShutdownAsync(); Assert.False(input.Disposed);
    }

    [Fact]
    public async Task ANewFocusPauseWinsOverResumeAlreadyWaitingForRelease()
    {
        using var gate = new ManualResetEventSlim(); var input = new Input();
        using var vm = new AutoClickerViewModel(input) { ClicksPerSecond = .1 };
        vm.ToggleRun(); await input.FirstClick.Task.WaitAsync(Deadline); input.ReleaseGate = gate;
        Task pause = vm.PauseForEditorFocusAsync();
        Task resume = Task.CompletedTask;
        try
        {
            await input.ReleaseEntered.Task.WaitAsync(Deadline);
            resume = vm.TogglePauseAsync(); Assert.False(resume.IsCompleted);
            Assert.Same(pause, vm.PauseForEditorFocusAsync());
            gate.Set(); await resume.WaitAsync(Deadline);
            Assert.True(vm.IsPaused); Assert.True(vm.InputOperations.Snapshot.IsBusy); Assert.Equal(1, input.Clicks);
        }
        finally { gate.Set(); vm.StopClicker(); await Task.WhenAll(pause, resume, vm.RunCompletion).WaitAsync(Deadline); await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task ResumeObserverFaultDoesNotSkipIndependentFinalReleaseOrClearQuarantine()
    {
        var input = new Input(); using var vm = new AutoClickerViewModel(input) { ClicksPerSecond = .1 };
        vm.ToggleRun(); await input.FirstClick.Task.WaitAsync(Deadline);
        await vm.PauseForEditorFocusAsync().WaitAsync(Deadline);
        Assert.Equal(1, input.Releases);
        bool failObserver = true;
        vm.RunningStateChanged += () =>
        {
            if (!failObserver || !vm.IsRunning || vm.IsPaused) return;
            failObserver = false;
            vm.SignalEmergencyStop();
            throw new InvalidOperationException("Fixture resume observer failure.");
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.TogglePauseAsync().WaitAsync(Deadline));
            await vm.RunCompletion.WaitAsync(Deadline);
            Assert.Equal(2, input.Releases); // Successful pause release plus independent final release.
            Assert.True(vm.InputOperations.Snapshot.IsQuarantined);
            Assert.False(vm.InputOperations.TryAcquire(HostInputOperationKind.Playback, out _, out _));
            Assert.All(input.CleanupOwners, id => Assert.Equal(input.LastLease!.OperationId, id));
            Assert.True(await vm.RecoverInputOwnershipAsync().WaitAsync(Deadline));
            Assert.Equal(3, input.Releases); Assert.False(vm.InputOperations.Snapshot.IsBusy);
        }
        finally { vm.SignalEmergencyStop(); await vm.RunCompletion.WaitAsync(Deadline); await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task FailedAttachmentCleanupRemainsOwnedUntilTheSameResourceCanBeRecovered()
    {
        var input = new Input { FailDetach = true }; using var vm = new AutoClickerViewModel(input) { RepeatOnce = true };
        vm.ToggleRun(); await vm.RunCompletion.WaitAsync(Deadline);
        Assert.True(vm.InputOperations.Snapshot.IsQuarantined);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RecoverInputOwnershipAsync().WaitAsync(Deadline));
        Assert.True(vm.InputOperations.Snapshot.IsQuarantined);
        input.FailDetach = false;
        Assert.True(await vm.RecoverInputOwnershipAsync().WaitAsync(Deadline));
        Assert.False(vm.InputOperations.Snapshot.IsBusy);
        await vm.ShutdownAsync();
    }

    [Fact]
    public async Task DeliberateRecoveryJoinsTheCapturedFinalPublicationTailInsteadOfReturningEarlyFalse()
    {
        var input = new Input { FailRelease = true }; using var vm = new AutoClickerViewModel(input) { RepeatOnce = true };
        Task<bool>? recovery = null;
        vm.RunningStateChanged += () =>
        {
            if (vm.IsRunning || recovery is not null || !vm.InputOperations.Snapshot.IsQuarantined) return;
            input.FailRelease = false;
            recovery = vm.RecoverInputOwnershipAsync();
            Assert.False(recovery.IsCompleted); // This handler is still part of the owned run's actual tail.
        };
        vm.ToggleRun(); await vm.RunCompletion.WaitAsync(Deadline);
        Assert.NotNull(recovery); Assert.True(await recovery!.WaitAsync(Deadline));
        Assert.Equal(2, input.Releases); Assert.False(vm.InputOperations.Snapshot.IsBusy);
        await vm.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PickerRetiresOrdinaryJoinedSetupFailureButPreservesTypedCleanupUncertainty(bool cleanupUnknown)
    {
        var picker = new Picker(); using var vm = new AutoClickerViewModel(new Input(), picker) { UseFixedPosition = true };
        vm.ShowOwnedPickOverlay = (_, _, _) => Task.FromException<ScreenPoint?>(cleanupUnknown
            ? new HostInputOperationCleanupException() : new IOException("Fixture setup failed after joined cleanup."));
        await vm.PickFixedLocationAsync().WaitAsync(Deadline);
        Assert.False(vm.IsPickingLocation); Assert.Equal(cleanupUnknown, vm.InputOperations.Snapshot.IsQuarantined);
        Assert.Equal(cleanupUnknown, vm.InputOperations.Snapshot.IsBusy);
        Assert.False(await vm.RecoverInputOwnershipAsync()); // Picker uncertainty has no guessed native-only recovery.
        await vm.ShutdownAsync();
        vm.InputOperations.Close();
        if (cleanupUnknown) await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => vm.InputOperations.DrainAsync().WaitAsync(Deadline));
        else await vm.InputOperations.DrainAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task IdleStopAndShutdownCannotCancelForeignPickerOrReleaseForeignInput()
    {
        var arbiter = new HostInputOperationArbiter(); var foreign = Acquire(arbiter, HostInputOperationKind.Picker);
        var input = new Input(); var picker = new Picker();
        using var vm = new AutoClickerViewModel(input, picker, inputOperations: arbiter);
        vm.StopClicker(); vm.SignalEmergencyStop(); await vm.ShutdownAsync();
        Assert.Equal(0, picker.Cancels); Assert.Equal(0, input.Releases);
        Assert.True(foreign.CanContinue); Assert.Equal(foreign.OperationId, arbiter.Snapshot.OperationId);
        await foreign.CompleteAsync(true);
    }

    [Fact]
    public async Task OwnedPickerPassesExactLeaseAndRetainsItThroughIgnoredCancellation()
    {
        var picker = new Picker(); var input = new Input();
        using var vm = new AutoClickerViewModel(input, picker) { UseFixedPosition = true, FixedX = 1, FixedY = 2 };
        HostInputOperationLease? received = null;
        vm.ShowOwnedPickOverlay = (actual, parent, ct) =>
        { Assert.Same(picker, actual); received = parent; return picker.ArmAndWaitAsync(parent, ct); };
        Task pick = vm.PickFixedLocationAsync(); await picker.Entered.Task.WaitAsync(Deadline);
        Task shutdown = vm.ShutdownAsync();
        try
        {
            await picker.Cancelled.Task.WaitAsync(Deadline);
            Assert.NotNull(received); Assert.Equal(HostInputOperationKind.Picker, received!.Kind);
            Assert.False(shutdown.IsCompleted); Assert.False(pick.IsCompleted);
            Assert.True(vm.InputOperations.Snapshot.IsBusy); Assert.Equal(0, input.Releases);
        }
        finally { picker.Release.TrySetResult(new(50, 60)); await shutdown.WaitAsync(Deadline); }
        Assert.Equal(1, vm.FixedX); Assert.Equal(2, vm.FixedY); Assert.False(vm.InputOperations.Snapshot.IsBusy);
    }

    [Fact]
    public async Task ReentrantTesterShutdownSeesActualTaskButDoesNotAcquireInput()
    {
        var input = new Input(); using var vm = new AutoClickerViewModel(input);
        Task? shutdown = null;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsTestRunning) && vm.IsTestRunning) shutdown = vm.ShutdownAsync(); };
        vm.StartTestCommand.Execute(null);
        Assert.NotNull(shutdown); await shutdown!.WaitAsync(Deadline);
        Assert.False(vm.IsTestRunning); Assert.False(vm.InputOperations.Snapshot.IsBusy);
        Assert.Equal(0, input.Releases); Assert.Equal(0, input.Attachments);
    }

    [Fact]
    public async Task ShutdownRetainsLeaseUntilActualBlockedClickReturnsWithoutDisposingBorrowedBackend()
    {
        using var gate = new ManualResetEventSlim(); var input = new Input { ClickGate = gate };
        using var vm = new AutoClickerViewModel(input);
        vm.ToggleRun(); await input.FirstClick.Task.WaitAsync(Deadline);
        Task shutdown = vm.ShutdownAsync();
        try
        {
            Assert.Same(shutdown, vm.ShutdownAsync()); Assert.False(shutdown.IsCompleted);
            Assert.Equal(0, input.Releases); Assert.True(vm.InputOperations.Snapshot.IsBusy);
        }
        finally { gate.Set(); await shutdown.WaitAsync(Deadline); }
        Assert.Equal(1, input.Releases); Assert.False(input.Disposed); Assert.False(vm.InputOperations.Snapshot.IsBusy);
    }

    private sealed class Input : IInputPlayer, IHostInputOperationAwarePlayer
    {
        public TaskCompletionSource FirstClick { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim? ClickGate, ReleaseGate;
        public bool FailRelease, FailDetach, Disposed;
        public int Clicks, Releases, Attachments;
        public List<Guid> CleanupOwners { get; } = [];
        public HostInputOperationLease? LastLease;
        public bool IsPlaying => false;
        public IDisposable AttachHostOperation(HostInputOperationLease lease)
        { Assert.True(lease.OwnsCleanup); LastLease = lease; Interlocked.Increment(ref Attachments); return new Attachment(this); }
        public void ReleaseHostOperationInputs(HostInputOperationLease lease)
        {
            Assert.True(lease.OwnsCleanup); Assert.Same(LastLease, lease);
            Interlocked.Increment(ref Releases); CleanupOwners.Add(lease.OperationId); ReleaseEntered.TrySetResult();
            if (ReleaseGate is { } gate && !gate.Wait(Deadline)) throw new TimeoutException("Fixture release gate did not open.");
            if (FailRelease) throw new InvalidOperationException("Fixture release failure.");
        }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false)
        {
            Assert.True(LastLease?.CanContinue); Interlocked.Increment(ref Clicks); FirstClick.TrySetResult();
            if (ClickGate is { } gate && !gate.Wait(Deadline)) throw new TimeoutException("Fixture click gate did not open.");
        }
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default) => Task.CompletedTask;
        public void ReleaseAllHeldKeys() => throw new InvalidOperationException("Unscoped fixture cleanup is forbidden.");
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() => Disposed = true;
        private sealed class Attachment(Input input) : IDisposable
        { public void Dispose() { if (input.FailDetach) throw new InvalidOperationException("Fixture attachment cleanup failure."); } }
    }

    private sealed class Activation : IActivationService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool IsActive => true; public string StatusMessage => "fixture"; public VerifiedLicense? CurrentLicense => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public async Task<ActivationOperationResult> RefreshAsync(CancellationToken ct = default)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Release.Task; return new(true, "fixture"); }
        public Task<ActivationRequestResult> CreateRequestAsync(bool allowSoftwareFallback = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ActivationOperationResult> ImportAsync(string licenseText, CancellationToken ct = default) => throw new NotSupportedException();
        public void RequireActive() { }
    }

    private sealed class Picker : IHostInputOperationBoundPicker
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ScreenPoint?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Cancels; public HostInputOperationLease? Parent;
        public bool IsArmed => !Release.Task.IsCompleted;
        public Task<ScreenPoint?> ArmAndWaitAsync(HostInputOperationLease parent, CancellationToken ct = default)
        { Parent = parent; Entered.TrySetResult(); return Release.Task; }
        public void Cancel(HostInputOperationLease parent) { Assert.Same(Parent, parent); Interlocked.Increment(ref Cancels); Cancelled.TrySetResult(); }
        public Task<ScreenPoint?> ArmAndWaitAsync(CancellationToken ct = default) => throw new InvalidOperationException("Owned fixture requires explicit parent.");
        public void Cancel() => throw new InvalidOperationException("Unscoped fixture cancellation is forbidden.");
        public void Dispose() { }
    }
}
