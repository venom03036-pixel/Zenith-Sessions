using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using System.Windows.Threading;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Usage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    private static readonly TimeSpan OwnershipDeadline = TimeSpan.FromSeconds(8);
    private static TaskCompletionSource OwnershipGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EditorOwnership_StartupIsReservedBeforeUncooperativeActivationAndPanicPreventsLateStart(bool recording)
        => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder();
        var arbiter = new HostInputOperationArbiter(); var player = new OwnershipPlayer(); var recorder = new OwnershipRecorder();
        var activation = new OwnershipActivation();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, player, recorder);
        vm.ActivationService = activation;
        Task work = recording ? vm.StartRecordingAsync() : vm.RunMacroAsync();
        try
        {
            await activation.Entered.Task.WaitAsync(OwnershipDeadline);
            Assert.Equal(recording ? HostInputOperationKind.Recording : HostInputOperationKind.Playback, arbiter.Snapshot.Kind);
            Assert.True(arbiter.Snapshot.IsBusy);
            Assert.False(arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out _, out _));
            if (recording) await vm.RunMacroAsync(); else await vm.StartRecordingAsync();
            Assert.Equal(1, activation.Calls);
            vm.SignalEmergencyStop();
            Assert.False(work.IsCompleted);
            Assert.Equal(0, player.Attachments);
            Assert.Equal(0, recorder.Starts);
        }
        finally { activation.Release.TrySetResult(); await work.WaitAsync(OwnershipDeadline); }
        Assert.False(vm.IsRunning); Assert.False(vm.IsRecording);
        Assert.False(arbiter.Snapshot.IsBusy);
        Assert.Equal(0, player.Plays);
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorOwnership_PausedPlaybackKeepsTheLeaseUntilItsActualRunEnds() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter(); var player = new OwnershipPlayer();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, player, delay: TimeSpan.FromSeconds(30));
        var started = OwnershipGate();
        vm.StatusChanged += message => { if (message.StartsWith("Executing '", StringComparison.Ordinal)) started.TrySetResult(); };
        Task run = vm.RunMacroAsync();
        try
        {
            await started.Task.WaitAsync(OwnershipDeadline);
            var paused = OwnershipGate();
            vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.IsPaused) && vm.IsPaused) paused.TrySetResult(); };
            vm.PauseCommand.Execute(null);
            await paused.Task.WaitAsync(OwnershipDeadline);
            Assert.True(arbiter.Snapshot.IsBusy);
            Assert.False(arbiter.TryAcquire(HostInputOperationKind.Recording, out _, out _));
            Assert.False(run.IsCompleted);
        }
        finally { await vm.StopMacroAsync().WaitAsync(OwnershipDeadline); await run.WaitAsync(OwnershipDeadline); }
        Assert.False(arbiter.Snapshot.IsBusy);
        Assert.False(arbiter.Snapshot.IsQuarantined);
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorOwnership_EditorFocusPauseDuringPlaybackRetirementIsANoOp() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter(); var player = new OwnershipPlayer();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, player, delay: TimeSpan.FromSeconds(30));
        var started = OwnershipGate();
        vm.StatusChanged += message => { if (message.StartsWith("Executing '", StringComparison.Ordinal)) started.TrySetResult(); };
        Task run = vm.RunMacroAsync();
        Exception? probe = null; int probes = 0;
        try
        {
            await started.Task.WaitAsync(OwnershipDeadline);
            // The native detach runs during retirement after the controller is disposed and before the
            // editor retracts it, off the UI thread: the same window an editor restore can land in.
            player.OnDetach = () =>
            {
                Interlocked.Increment(ref probes);
                probe = Record.Exception(() => vm.PauseForEditorFocusAsync().GetAwaiter().GetResult());
            };
        }
        finally { await vm.StopMacroAsync().WaitAsync(OwnershipDeadline); await run.WaitAsync(OwnershipDeadline); }
        Assert.Equal(1, probes);
        Assert.Null(probe);
        Assert.False(vm.IsPaused);
        Assert.False(vm.IsRunning);
        Assert.False(arbiter.Snapshot.IsBusy);
        Assert.False(arbiter.Snapshot.IsQuarantined);
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorOwnership_StopWaitsForActualCapturedBackendCleanupBeforeAReplacement() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter();
        using var player = new OwnershipPlayer { BlockRelease = true };
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, player);
        Task run = vm.RunMacroAsync(); Task? stop = null;
        try
        {
            await player.ReleaseEntered.Task.WaitAsync(OwnershipDeadline);
            stop = vm.StopMacroAsync();
            Assert.False(run.IsCompleted); Assert.False(stop.IsCompleted);
            Assert.True(arbiter.Snapshot.IsBusy);
            Assert.False(arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out _, out _));
            Assert.Same(player.LastLease, GetOwnedPlaybackLease(vm));
        }
        finally
        {
            player.AllowRelease.Set();
            await run.WaitAsync(OwnershipDeadline);
            if (stop is not null) await stop.WaitAsync(OwnershipDeadline);
        }
        Assert.False(arbiter.Snapshot.IsBusy);
        Assert.True(arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out var replacement, out _));
        await replacement!.CompleteAsync(true);
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorOwnership_FailedFinalReleaseQuarantinesUntilExplicitOwnerRecovery() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter();
        using var player = new OwnershipPlayer { FailRelease = true };
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, player);
        await vm.RunMacroAsync().WaitAsync(OwnershipDeadline);
        Assert.True(arbiter.Snapshot.IsQuarantined);
        Assert.False(vm.IsRunning);
        Assert.False(arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out _, out _));
        await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => vm.RecoverInputOwnershipAsync());
        player.FailRelease = false;
        Assert.True(await vm.RecoverInputOwnershipAsync().WaitAsync(OwnershipDeadline));
        Assert.False(arbiter.Snapshot.IsBusy); Assert.False(arbiter.Snapshot.IsQuarantined);
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorOwnership_IdleStopAndDisposeCannotReleaseOrCancelAnotherOwner() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter(); var player = new OwnershipPlayer();
        var vm = CreateOwnershipEditor(folder.Library, arbiter, player);
        Assert.True(arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out var foreign, out _));
        using var attached = player.AttachHostOperation(foreign!);
        await vm.StopMacroAsync(); vm.Dispose();
        Assert.True(foreign!.CanContinue);
        Assert.Equal(0, player.Releases);
        Assert.True(arbiter.Owns(foreign));
        await foreign.CompleteAsync(true);
    });

    [Fact]
    public Task EditorOwnership_BusyRefusalDoesNotInvokeDraftLicenseOrOverlayCallbacks() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter(); var player = new OwnershipPlayer();
        var recorder = new OwnershipRecorder(); using var vm = CreateOwnershipEditor(folder.Library, arbiter, player, recorder);
        var activation = new OwnershipActivation(); vm.ActivationService = activation;
        int drafts = 0, overlays = 0;
        vm.CommitVisibleRecordingFields = () => { drafts++; return true; };
        vm.EnterCompactOverlay = () => overlays++;
        Assert.True(arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out var foreign, out _));
        try
        {
            await vm.StartRecordingAsync(); await vm.RunMacroAsync();
            Assert.Equal(0, drafts); Assert.Equal(0, overlays); Assert.Equal(0, activation.Calls);
            Assert.Equal(0, recorder.Starts); Assert.Equal(0, player.Attachments);
        }
        finally { activation.Release.TrySetResult(); await foreign!.CompleteAsync(true); }
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorOwnership_RecordingRetainsItsLeaseThroughActualProducerStopAndAcceptedTail() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter(); var player = new OwnershipPlayer();
        var recorder = new OwnershipRecorder { BlockStop = true }; var documents = new OwnershipDocuments(folder.Library, TimeSpan.Zero);
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, player, recorder, documents: documents);
        await vm.StartRecordingAsync().WaitAsync(OwnershipDeadline);
        Assert.True(vm.IsRecording);
        long stamp = Stopwatch.GetTimestamp();
        Assert.True(recorder.Writer!.TryWrite(new(InputEventType.KeyDown, stamp, 0, 0, 65)));
        Assert.True(recorder.Writer.TryWrite(new(InputEventType.KeyUp, stamp + 1, 0, 0, 65)));
        Task stop = vm.StopRecordingAsync();
        try
        {
            await recorder.StopEntered.Task.WaitAsync(OwnershipDeadline);
            Assert.False(stop.IsCompleted);
            Assert.Equal(HostInputOperationKind.Recording, arbiter.Snapshot.Kind);
            Assert.False(arbiter.TryAcquire(HostInputOperationKind.Playback, out _, out _));
        }
        finally { recorder.StopRelease.TrySetResult(); await stop.WaitAsync(OwnershipDeadline); }
        Assert.False(arbiter.Snapshot.IsBusy);
        Assert.NotNull(documents.LastSaved);
        Assert.NotEmpty(documents.LastSaved!.Steps.OfType<KeyPressStep>());
        await vm.ShutdownAsync();
    });

    [Theory]
    [InlineData(HostInputOperationKind.Playback)]
    [InlineData(HostInputOperationKind.Recording)]
    [InlineData(HostInputOperationKind.AutoClicker)]
    public Task EditorOwnership_ExplicitPickerParentNeverBorrowsInputProducerLeases(HostInputOperationKind kind) => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, new OwnershipPlayer());
        int prompts = 0; vm.OwnedLocationPickPrompter = (_, _) => { prompts++; return Task.FromResult<ScreenPoint?>(new(1, 2)); };
        Assert.True(arbiter.TryAcquire(kind, out var parent, out _));
        try { Assert.Null(await vm.PickScreenPointAsync(parent!)); Assert.Equal(0, prompts); }
        finally { await parent!.CompleteAsync(true); }
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorOwnership_ExplicitPickerRejectsForeignArbiterAndRetainsItsValidParent() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter(); var other = new HostInputOperationArbiter();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, new OwnershipPlayer());
        HostInputOperationLease? observed = null;
        vm.OwnedLocationPickPrompter = (parent, _) => { observed = parent; return Task.FromResult<ScreenPoint?>(new(3, 4)); };
        Assert.True(other.TryAcquire(HostInputOperationKind.Picker, out var foreign, out _));
        try { Assert.Null(await vm.PickScreenPointAsync(foreign!)); Assert.Null(observed); }
        finally { await foreign!.CompleteAsync(true); }
        Assert.True(arbiter.TryAcquire(HostInputOperationKind.CaptureTest, out var valid, out _));
        Assert.Equal(new ScreenPoint(3, 4), await vm.PickScreenPointAsync(valid!));
        Assert.Same(valid, observed); Assert.True(valid!.CanContinue); Assert.False(valid.Completion.IsCompleted);
        await valid.CompleteAsync(true); await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorOwnership_StopCallbackFailureRetainsTheOwnerAndPublishesRecovery() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, new OwnershipPlayer());
        var activation = new OwnershipActivation(); vm.ActivationService = activation;
        var messages = new List<string>(); vm.StatusChanged += messages.Add;
        Task run = vm.RunMacroAsync();
        try
        {
            await activation.Entered.Task.WaitAsync(OwnershipDeadline);
            var lease = GetOwnedPlaybackLease(vm)!;
            using var failedStop = lease.StopToken.Register(() => throw new InvalidOperationException("Synthetic signal failure"));
            lease.SignalStop(); activation.Release.TrySetResult();
            await run.WaitAsync(OwnershipDeadline);
            Assert.True(arbiter.Snapshot.IsQuarantined);
            Assert.True(arbiter.Owns(lease));
            Assert.Contains(messages, message => message.StartsWith("Input cleanup needs recovery", StringComparison.Ordinal));
            await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => vm.RecoverInputOwnershipAsync());
        }
        finally { activation.Release.TrySetResult(); await run.WaitAsync(OwnershipDeadline); }
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorOwnership_StoppedBorrowedPickerDoesNotRestoreAnActiveModeOrPublishThePoint() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, new OwnershipPlayer());
        Assert.True(arbiter.TryAcquire(HostInputOperationKind.CaptureTest, out var parent, out _));
        InputMode previous = InputModeGate.Current;
        try
        {
            InputModeGate.Set(InputMode.Recording);
            vm.OwnedLocationPickPrompter = (owner, _) =>
            { owner.SignalStop(); return Task.FromResult<ScreenPoint?>(new(3, 4)); };
            Assert.Null(await vm.PickScreenPointAsync(parent!));
            Assert.Equal(InputMode.Normal, InputModeGate.Current);
            Assert.True(parent!.OwnsCleanup); Assert.False(parent.Completion.IsCompleted);
        }
        finally { await parent!.CompleteAsync(true); InputModeGate.Set(previous); }
        await vm.ShutdownAsync();
    });

    [Fact]
    public Task EditorOwnership_ScreenSnipUsesTheSharedServiceAndDoesNotPublishAfterStop() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter();
        using var capture = new OwnershipCapture();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, new OwnershipPlayer(), capture: capture);
        int prompts = 0; vm.RegionPrompter = () => { prompts++; return new System.Drawing.Rectangle(1, 2, 4, 5); };
        Task<CaptureFrame?> snip = vm.CaptureOwnedScreenSnipAsync(); Task? shutdown = null;
        try
        {
            await capture.Entered.Task.WaitAsync(OwnershipDeadline);
            Assert.Equal(1, prompts); Assert.Equal(HostInputOperationKind.CaptureTest, arbiter.Snapshot.Kind);
            shutdown = vm.ShutdownAsync();
            Assert.False(shutdown.IsCompleted); Assert.False(snip.IsCompleted);
            Assert.False(arbiter.TryAcquire(HostInputOperationKind.Playback, out _, out _));
        }
        finally
        {
            capture.Release.TrySetResult();
            Assert.Null(await snip.WaitAsync(OwnershipDeadline));
            if (shutdown is not null) await shutdown.WaitAsync(OwnershipDeadline);
        }
        Assert.Equal(1, capture.Calls);
        Assert.NotNull(capture.Frame); Assert.True(capture.Frame!.IsDisposed);
        Assert.False(arbiter.Snapshot.IsBusy);
    });

    [Fact]
    public Task EditorOwnership_SuccessfulScreenSnipTransfersOnlyTheCallerOwnedFrame() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter();
        using var capture = new OwnershipCapture();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, new OwnershipPlayer(), capture: capture);
        vm.RegionPrompter = () => new System.Drawing.Rectangle(7, 9, 4, 5);
        Task<CaptureFrame?> snip = vm.CaptureOwnedScreenSnipAsync();
        try
        {
            await Task.WhenAny(capture.Entered.Task, snip).WaitAsync(OwnershipDeadline);
            if (snip.IsCompleted) await snip; // Surface the real setup failure instead of waiting on an unreachable fake gate.
            Assert.True(capture.Entered.Task.IsCompleted);
        }
        finally { capture.Release.TrySetResult(); }
        using var frame = await snip.WaitAsync(OwnershipDeadline);
        Assert.NotNull(frame); Assert.Same(capture.Frame, frame);
        Assert.False(frame!.IsDisposed);
        Assert.Equal(4, frame.Width); Assert.Equal(5, frame.Height);
        Assert.False(arbiter.Snapshot.IsBusy); Assert.False(arbiter.Snapshot.IsQuarantined);
        await vm.ShutdownAsync();
        Assert.False(frame.IsDisposed); // The editor's shutdown cannot dispose the transferred result.
    });

    [Fact]
    public Task EditorOwnership_ScreenSnipRetirementFailureDisposesTheFrameBeforeAnyTransfer() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter();
        using var capture = new OwnershipCapture();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, new OwnershipPlayer(), capture: capture);
        vm.RegionPrompter = () => new System.Drawing.Rectangle(1, 2, 4, 5);
        Task<CaptureFrame?> snip = vm.CaptureOwnedScreenSnipAsync();
        try
        {
            await capture.Entered.Task.WaitAsync(OwnershipDeadline);
            var lease = (HostInputOperationLease)typeof(MacroEditorViewModel)
                .GetField("_editorSnipLease", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            using var failedStop = lease.StopToken.Register(() => throw new InvalidOperationException("Synthetic snip stop failure"));
            lease.SignalStop(); capture.Release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => snip);
            Assert.NotNull(capture.Frame); Assert.True(capture.Frame!.IsDisposed);
            Assert.True(arbiter.Snapshot.IsQuarantined); Assert.True(arbiter.Owns(lease));
        }
        finally { capture.Release.TrySetResult(); await vm.ShutdownAsync(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EditorOwnership_TargetImagePromptReservesBeforeItsDelegateAndRefusesBusy(bool import) => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, new OwnershipPlayer());
        int prompts = 0;
        MacroSuite.App.Overlays.ImageTemplateSelection? Prompt(double confidence)
        {
            prompts++;
            Assert.Equal(.75, confidence);
            Assert.Equal(import ? HostInputOperationKind.Picker : HostInputOperationKind.CaptureTest, arbiter.Snapshot.Kind);
            Assert.False(arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out _, out _));
            return null;
        }
        vm.ImageImportPrompter = Prompt; vm.ImageCaptureOnlyPrompter = Prompt;
        Assert.True(arbiter.TryAcquire(HostInputOperationKind.Playback, out var foreign, out _));
        try { Assert.Null(vm.PromptOwnedTargetImage(import, .75)); Assert.Equal(0, prompts); }
        finally { await foreign!.CompleteAsync(true); }
        Assert.Null(vm.PromptOwnedTargetImage(import, .75)); Assert.Equal(1, prompts);
        await vm.ShutdownAsync(); Assert.False(arbiter.Snapshot.IsBusy);
    });

    [Fact]
    public Task EditorOwnership_SynchronousPromptCleanupUncertaintyStillQuarantines() => OwnershipOnUi(async () =>
    {
        using var folder = new RoutingTempFolder(); var arbiter = new HostInputOperationArbiter();
        using var vm = CreateOwnershipEditor(folder.Library, arbiter, new OwnershipPlayer());
        vm.RegionPrompter = () => throw new HostInputOperationCleanupException();
        Assert.Throws<HostInputOperationCleanupException>(() => vm.PromptOwnedRegion());
        Guid owner = arbiter.Snapshot.OperationId!.Value;
        await arbiter.WaitForRetirementAsync(owner).WaitAsync(OwnershipDeadline);
        Assert.True(arbiter.Snapshot.IsQuarantined); Assert.Equal(owner, arbiter.Snapshot.OperationId);
        Assert.False(arbiter.TryAcquire(HostInputOperationKind.Playback, out _, out _));
        await vm.ShutdownAsync();
    });

    private MacroEditorViewModel CreateOwnershipEditor(string directory, HostInputOperationArbiter arbiter,
        OwnershipPlayer player, OwnershipRecorder? recorder = null, TimeSpan? delay = null, OwnershipDocuments? documents = null,
        IScreenCaptureService? capture = null)
    {
        var feature = new DummyFeatureGate();
        var vm = new MacroEditorViewModel(capture ?? new DummyCaptureService(), player,
            new AIVisionRouter(new InMemorySecretVault()), new DummyApprovalGate(), feature,
            new UsageTracker(new DummyUsageRepository(), feature), new DummyAuditLogger(),
            customMacrosDirectory: directory, inputRecorder: recorder ?? new OwnershipRecorder(),
            recordingDesktop: new ExternalRecordingDesktop(), documentStore: documents ?? new OwnershipDocuments(directory, delay ?? TimeSpan.Zero),
            inputOperations: arbiter) { RecordingGracePeriodMs = 0 };
        typeof(MacroEditorViewModel).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, new OwnershipTargets());
        // Supply the fixture's actual dispatcher without creating a process-global WPF Application.
        typeof(MacroEditorViewModel).GetField("_uiDispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, Dispatcher.CurrentDispatcher);
        return vm;
    }

    private static HostInputOperationLease? GetOwnedPlaybackLease(MacroEditorViewModel vm)
    {
        object? owned = typeof(MacroEditorViewModel).GetField("_ownedEditorPlayback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
        return (HostInputOperationLease?)owned?.GetType().GetProperty("Lease")!.GetValue(owned);
    }

    private static async Task OwnershipOnUi(Func<Task> body)
    {
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher; Exception? error = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                Task task = body();
                Task stop = task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Dispatcher.Run(); stop.GetAwaiter().GetResult(); task.GetAwaiter().GetResult();
            }
            catch (Exception failure) { error = failure; }
            finally { if (!dispatcher.HasShutdownFinished) dispatcher.InvokeShutdown(); SynchronizationContext.SetSynchronizationContext(null); done.TrySetResult(error); }
        }) { IsBackground = true, Name = "Owned editor input fixture" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Exception? failure = await done.Task.ConfigureAwait(false); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class OwnershipActivation : IActivationService
    {
        public readonly TaskCompletionSource Entered = OwnershipGate(), Release = OwnershipGate();
        public int Calls;
        public bool IsActive => true; public string StatusMessage => "Synthetic active"; public VerifiedLicense? CurrentLicense => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public async Task<ActivationOperationResult> RefreshAsync(CancellationToken ct = default)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Release.Task; return new(true, "Synthetic active"); }
        public Task<ActivationRequestResult> CreateRequestAsync(bool allowSoftwareFallback = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ActivationOperationResult> ImportAsync(string text, CancellationToken ct = default) => throw new NotSupportedException();
        public void RequireActive() { }
    }

    private sealed class OwnershipPlayer : IInputPlayer, IHostInputOperationAwarePlayer
    {
        public int Attachments, Releases, Plays; public bool FailRelease, BlockRelease;
        public readonly TaskCompletionSource ReleaseEntered = OwnershipGate();
        public readonly ManualResetEventSlim AllowRelease = new(false);
        public HostInputOperationLease? LastLease;
        public Action? OnDetach;
        public bool IsPlaying => false;
        public IDisposable AttachHostOperation(HostInputOperationLease lease)
        {
            LastLease = lease; Interlocked.Increment(ref Attachments);
            return new OwnershipToken(() => { if (ReferenceEquals(LastLease, lease)) LastLease = null; OnDetach?.Invoke(); });
        }
        public void ReleaseHostOperationInputs(HostInputOperationLease lease)
        {
            Assert.True(lease.OwnsCleanup); Interlocked.Increment(ref Releases); ReleaseEntered.TrySetResult();
            if (BlockRelease && !AllowRelease.Wait(OwnershipDeadline)) throw new TimeoutException("Controlled cleanup gate was not released.");
            if (FailRelease) throw new InvalidOperationException("Synthetic owned release failed");
        }
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Plays); return Task.CompletedTask; }
        public void ReleaseAllHeldKeys() => throw new InvalidOperationException("Managed editor must use captured cleanup.");
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) => throw new NotSupportedException();
        public void SendRelativeMove(int dx, int dy) => throw new NotSupportedException();
        public void Dispose() { AllowRelease.Set(); AllowRelease.Dispose(); }
    }
    private sealed class OwnershipToken(Action release) : IDisposable { public void Dispose() => release(); }
    private sealed class OwnershipCapture : IScreenCaptureService
    {
        public readonly TaskCompletionSource Entered = OwnershipGate(), Release = OwnershipGate();
        public int Calls; public CaptureFrame? Frame;
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public async Task<CaptureFrame> CaptureRegionAsync(System.Drawing.Rectangle region, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false); // Controlled uncooperative provider; never actual desktop capture.
            return Frame = await new DummyCaptureService().CaptureRegionAsync(region);
        }
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { Release.TrySetResult(); Frame?.Dispose(); }
    }
    private sealed class OwnershipRecorder : IInputRecorder
    {
        public int Starts; public bool BlockStop; public bool IsRecording { get; private set; }
        public ChannelWriter<InputEvent>? Writer;
        public readonly TaskCompletionSource StopEntered = OwnershipGate(), StopRelease = OwnershipGate();
        public Task StartAsync(ChannelWriter<InputEvent> writer, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Starts++; Writer = writer; IsRecording = true; return Task.CompletedTask; }
        public async Task StopAsync()
        { StopEntered.TrySetResult(); if (BlockStop) await StopRelease.Task; IsRecording = false; Writer?.TryComplete(); }
        public void Dispose() { StopRelease.TrySetResult(); }
    }
    private sealed class OwnershipDocuments(string directory, TimeSpan delay) : IMacroDocumentStore
    {
        private readonly MacroModel _model = new() { Name = "Owned synthetic macro", Steps = [new DelayStep(delay)],
            Settings = new() { Target = new() { Mode = MacroTargetMode.Screen }, MaxRuntime = TimeSpan.FromMinutes(1) } };
        public MacroModel? LastSaved;
        public bool CanWriteLibrary => true;
        public IReadOnlyList<string> EnumerateLibraryFiles() => [GetLibraryPath(_model.Id)];
        public MacroDocumentLoadResult Load(string path, string? passphrase = null) => new([_model], path, false);
        public MacroDocumentLoadResult MigrateLibraryDocument(string path) => Load(path);
        public void Save(MacroModel model, string path) => LastSaved = model;
        public string GetLibraryPath(Guid id) => Path.Combine(directory, id.ToString("N") + ".zmacro");
        public void ExportBackup(IReadOnlyList<MacroModel> models, string path, string passphrase) => throw new NotSupportedException();
        public void RecoverLibraryFromBackup(string path, string passphrase) => throw new NotSupportedException();
    }
    private sealed class OwnershipTargets : ITargetWindowService
    {
        public bool SupportsWindowTargeting => true;
        public TargetWindowSnapshot Snapshot => new(null, null, 1, 1, true);
        public event EventHandler? Changed { add { } remove { } }
        public Task<IReadOnlyList<TargetWindowInfo>> EnumerateAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TargetWindowInfo>>([]);
        public ResolvedMacroTarget Resolve(MacroTargetRule rule, TargetWindowInfo? preferredExternal = null) => new(rule, null);
        public TargetValidation Inspect(ResolvedMacroTarget target) => new(true, true, false, new(0, 0, 100, 100), 1);
        public void RegisterOwnWindow(IntPtr window) { } public bool TryActivate(ResolvedMacroTarget target) => true; public void Dispose() { }
    }
}
