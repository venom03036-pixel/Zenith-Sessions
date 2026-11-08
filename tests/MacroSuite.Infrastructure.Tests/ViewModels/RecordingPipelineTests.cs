using System.Diagnostics;
using System.Threading.Channels;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Usage;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Input;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    private MacroEditorViewModel CreateRecordingViewModel(IInputRecorder recorder, IRecordingDesktopState? desktop = null,
        string? directory = null, IMacroDocumentStore? documents = null)
    {
        var gate = new DummyFeatureGate();
        return new MacroEditorViewModel(new DummyCaptureService(), new DummyInputPlayer(),
            new AIVisionRouter(new InMemorySecretVault()), new DummyApprovalGate(), gate,
            new UsageTracker(new DummyUsageRepository(), gate), new DummyAuditLogger(),
            inputRecorder: recorder, recordingDesktop: desktop ?? new ExternalRecordingDesktop(),
            customMacrosDirectory: directory, documentStore: documents)
        { RecordingGracePeriodMs = 0 };
    }

    [Fact]
    public async Task RecordToggle_StopsActiveRecording_AndRestoresTheEditor()
    {
        var recorder = new TestInputRecorder();using var vm = CreateRecordingViewModel(recorder);int restored = 0;var labels = new List<string>();
        vm.PropertyChanged += (_,e) => { if(e.PropertyName == nameof(vm.PrimaryTransportText)) labels.Add(vm.PrimaryTransportText); };
        vm.LeaveCompactOverlay = () => restored++;
        await vm.ToggleRecordingAsync();vm.IsRecording.Should().BeTrue();
        await vm.ToggleRecordingAsync();recorder.IsRecording.Should().BeFalse();vm.IsRecordingBusy.Should().BeFalse();restored.Should().Be(1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));while(labels.LastOrDefault() != "Run") await Task.Delay(5,deadline.Token);
        vm.RecordingButtonText.Should().Be("Record");
    }

    [Fact]
    public async Task RecordToggle_DuringStartup_CancelsAndRestoresWithoutGhostCapture()
    {
        var recorder = new DelayedStartRecorder();using var vm = CreateRecordingViewModel(recorder);int restored = 0;
        vm.LeaveCompactOverlay = () => restored++;
        Task starting = vm.ToggleRecordingAsync();await recorder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await vm.ToggleRecordingAsync();await starting;vm.IsRecordingBusy.Should().BeFalse();restored.Should().Be(1);
    }

    [Fact]
    public async Task StopShortcut_WithModifiers_PreservesThePlainKeyAndStopsOnTheWholeChord()
    {
        var recorder = new TestInputRecorder();using var vm = CreateRecordingViewModel(recorder);vm.StopHotkey = "Ctrl+L";
        await vm.StartRecordingAsync();long now = Stopwatch.GetTimestamp();
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, now++, 0, 0, 76));recorder.Writer.TryWrite(new(InputEventType.KeyUp, now++, 0, 0, 76));
        await Task.Delay(30);vm.IsRecording.Should().BeTrue();
        recorder.Writer.TryWrite(new(InputEventType.KeyDown, now++, 0, 0, 0xA2));recorder.Writer.TryWrite(new(InputEventType.KeyDown, now++, 0, 0, 76));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));while(vm.IsRecording) await Task.Delay(5,deadline.Token);
        await vm.StopRecordingAsync();vm.Steps.Select(s => s.Step).OfType<KeyPressStep>().Count(s => s.KeyCode == 76).Should().Be(2);
    }

    [Fact]
    public async Task Stop_RestoresBeforeBackgroundEncryption_WritesOneSnapshotAndDrainsTail()
    {
        using var folder = new RoutingTempFolder();var documents = new RecordingDocumentSpy(folder.Library);var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder,directory:folder.Library,documents:documents);int baseline = documents.Saves,restored = 0;
        vm.LeaveCompactOverlay = () => restored++;
        await vm.StartRecordingAsync();recorder.Writer!.TryWrite(new(InputEventType.KeyDown,Stopwatch.GetTimestamp(),0,0,87));documents.Block = true;
        Task stopping = vm.StopRecordingAsync();
        try
        {
            await documents.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));restored.Should().Be(1);recorder.IsRecording.Should().BeFalse();
            stopping.IsCompleted.Should().BeFalse();vm.IsRecordingBusy.Should().BeTrue();documents.Saves.Should().Be(baseline + 1);
        }
        finally { documents.Release.Set(); }
        await stopping;vm.IsRecordingBusy.Should().BeFalse();vm.Steps.Should().HaveCount(2);documents.Saves.Should().Be(baseline + 1);
    }

    [Fact]
    public async Task Stop_SaveFailure_RestoresAndKeepsRecordedActionsWithAccurateError()
    {
        using var folder = new RoutingTempFolder();var documents = new RecordingDocumentSpy(folder.Library);var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder,directory:folder.Library,documents:documents);int restored = 0;var messages = new List<string>();
        vm.LeaveCompactOverlay = () => restored++;vm.StatusChanged += messages.Add;
        await vm.StartRecordingAsync();recorder.Writer!.TryWrite(new(InputEventType.KeyDown,Stopwatch.GetTimestamp(),0,0,87));documents.Fail = true;
        await vm.StopRecordingAsync();restored.Should().Be(1);vm.Steps.Should().HaveCount(2);vm.SaveStateText.Should().Be("Save failed");
        messages.Should().Contain(m => m.Contains("Recorded actions remain in the editor"));
    }

    private sealed class RecordingDocumentSpy(string directory) : IMacroDocumentStore
    {
        public int Saves;public bool Block,Fail;public readonly ManualResetEventSlim Release = new(false);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CanWriteLibrary => true;
        public IReadOnlyList<string> EnumerateLibraryFiles() => [];
        public MacroDocumentLoadResult Load(string path,string? passphrase=null) => throw new NotSupportedException();
        public MacroDocumentLoadResult MigrateLibraryDocument(string path) => throw new NotSupportedException();
        public string GetLibraryPath(Guid id) => Path.Combine(directory,id+".zmacro");
        public void Save(MacroModel model,string path)
        {
            Interlocked.Increment(ref Saves);if(Block) { Entered.TrySetResult();if(!Release.Wait(WpfTestApplication.LivenessBound)) throw new TimeoutException("Test write was not released."); }
            if(Fail) throw new IOException("test write denied");
        }
        public void ExportBackup(IReadOnlyList<MacroModel> models,string path,string passphrase) => throw new NotSupportedException();
        public void RecoverLibraryFromBackup(string path,string passphrase) => throw new NotSupportedException();
    }

    [Fact]
    public Task HiddenProductionEditor_DoesNotAccumulateFollowScrollDispatcherWork()
        => WpfTestApplication.RunAsync(async () =>
        {
            var application = System.Windows.Application.Current;
            var styles = new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/MacroSuite.App;component/Themes/ModernControls.xaml") };
            application.Resources.MergedDictionaries.Add(styles);
            try
            {
                using var vm = CreateRecordingViewModel(new TestInputRecorder());
                vm.Steps.ReplaceAll(Enumerable.Range(0,2000).Select(_ => new DelayStep(TimeSpan.Zero)));
                var view = new MacroSuite.App.Views.MacroEditorView { DataContext = vm };
                view.Measure(new System.Windows.Size(1320,740));view.Arrange(new System.Windows.Rect(0,0,1320,740));
                vm.IsRecording = true;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var recordButton = (System.Windows.Controls.Button)view.FindName("RecordToggleButton");
                vm.CanEditMacro.Should().BeFalse();recordButton.IsEnabled.Should().BeTrue("the recording control must stay usable while editing is locked");
                vm.RecordCommand.CanExecute(null).Should().BeTrue();vm.RecordingButtonText.Should().Be("Stop recording");
                int posted = 0;void Posted(object? sender,System.Windows.Threading.DispatcherHookEventArgs e) => posted++;
                var dispatcher = application.Dispatcher;dispatcher.Hooks.OperationPosted += Posted;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var recordFollow = typeof(MacroSuite.App.Views.MacroEditorView).GetMethod("OnRecordingBatchApplied",flags)!;
                var playFollow = typeof(MacroSuite.App.Views.MacroEditorView).GetMethod("OnStepNavigationRequested",flags)!;
                var elapsed = Stopwatch.StartNew();
                try
                {
                    for(int i=0;i<10_000;i++) { recordFollow.Invoke(view,null);playFollow.Invoke(view,[vm.Steps[1999]]); }
                    view.IsVisible.Should().BeFalse();posted.Should().Be(0,"a hidden editor must not queue deferred ScrollIntoView work on return from mini mode");
                    Console.WriteLine($"Hidden production editor:20,000 follow notifications in {elapsed.ElapsedMilliseconds}ms, {posted} queued dispatcher operations.");
                }
                finally { dispatcher.Hooks.OperationPosted -= Posted;vm.IsRecording = false;view.DataContext = null; }
            }
            finally { application.Resources.MergedDictionaries.Remove(styles); }
        });

    [Fact]
    public Task LargeRecordingStop_RestoresAndKeepsDispatcherResponsiveDuringEncryption()
        => WpfTestApplication.RunAsync(async () =>
        {
            using var folder = new RoutingTempFolder();var documents = new RecordingDocumentSpy(folder.Library);var recorder = new TestInputRecorder();
            using var vm = CreateRecordingViewModel(recorder,directory:folder.Library,documents:documents);
            vm.Steps.ReplaceAll(Enumerable.Range(0,100_000).Select(_ => new DelayStep(TimeSpan.Zero)));
            await vm.StartRecordingAsync();documents.Block = true;int restored = 0,heartbeats = 0,atSave = int.MaxValue;long restoredAfter = -1;var elapsed = Stopwatch.StartNew();
            vm.LeaveCompactOverlay = () => { restored++;restoredAfter = elapsed.ElapsedMilliseconds; };
            var responsive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_,_) => { if(++heartbeats - atSave > 1) responsive.TrySetResult(); };timer.Start();Task stopping = vm.StopRecordingAsync();
            try
            {
                await documents.Entered.Task.WaitAsync(WpfTestApplication.LivenessBound);restored.Should().Be(1);stopping.IsCompleted.Should().BeFalse();
                atSave = heartbeats;
                // A briefly slow machine keeps the dispatcher busy for longer than any fixed observation
                // window. Wait for the heartbeats themselves: only a dispatcher blocked by the save stops them.
                _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => Thread.Sleep(200));
                await Task.WhenAny(responsive.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                (heartbeats - atSave).Should().BeGreaterThan(1,"disk encryption must leave WPF's dispatcher responsive");
                stopping.IsCompleted.Should().BeFalse("the heartbeats must arrive while the encrypted save is still blocked");
                restoredAfter.Should().BeLessThan(2000,"100,000-action finalization must remain bounded before restoring");
                Console.WriteLine($"100,000 actions: restore callback after {restoredAfter} ms; {heartbeats - atSave} dispatcher heartbeats while encrypted save was blocked.");
            }
            finally { documents.Release.Set();timer.Stop(); }
            await stopping;vm.Steps.Should().HaveCount(100_000);
        });

    [Fact]
    public async Task NormalStop_DrainsQueuedTail_PreservesRepeatedDownsAndMatchingReleases()
    {
        var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder);
        await vm.StartRecordingAsync();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 100; i++)
            recorder.Writer!.TryWrite(new(InputEventType.KeyDown, start + i, 0, 0, 87)).Should().BeTrue();
        recorder.Writer!.TryWrite(new(InputEventType.KeyUp, start + 100, 0, 0, 87)).Should().BeTrue();
        // No delay: stop must drain events that the worker has not yet processed.
        await vm.StopRecordingAsync();
        vm.Steps.Should().HaveCount(101);
        vm.Steps.Take(100).Select(s => ((KeyPressStep)s.Step).Stroke).Should().OnlyContain(s => s == "Down");
        ((KeyPressStep)vm.GetStepAt(100)!).Stroke.Should().Be("Up");
    }

    [Fact]
    public async Task Recording_PreservesModifierDownAndUp_IncludingWindowsKey()
    {
        var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder);
        await vm.StartRecordingAsync();
        long start = Stopwatch.GetTimestamp();
        int[] keys = [0xA0, 0xA2, 0xA4, 0x5B];
        foreach (int key in keys) recorder.Writer!.TryWrite(new(InputEventType.KeyDown, start++, 0, 0, key));
        foreach (int key in keys.Reverse()) recorder.Writer!.TryWrite(new(InputEventType.KeyUp, start++, 0, 0, key));
        await vm.StopRecordingAsync();
        vm.Steps.Select(s => ((KeyPressStep)s.Step).KeyCode).Should().Equal(keys.Concat(keys.Reverse()));
        vm.Steps.Take(4).Should().OnlyContain(s => ((KeyPressStep)s.Step).Stroke == "Down");
        vm.Steps.Skip(4).Should().OnlyContain(s => ((KeyPressStep)s.Step).Stroke == "Up");
    }

    [Fact]
    public async Task HeldCameraPath_InterleavesKeysAndWheelsAtOriginalBoundaries()
    {
        var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder);
        await vm.StartRecordingAsync();
        long start = Stopwatch.GetTimestamp();
        long Tick(int ms) => start + ms * Stopwatch.Frequency / 1000;
        InputEvent[] events = [
            new(InputEventType.MouseDown, Tick(0), 100, 100, (int)MouseButton.Right),
            new(InputEventType.MouseMove, Tick(10), 110, 103, 0),
            new(InputEventType.KeyDown, Tick(20), 0, 0, 87),
            new(InputEventType.MouseMove, Tick(30), 107, 108, 0),
            new(InputEventType.MouseWheel, Tick(40), 107, 108, -120),
            new(InputEventType.MouseMove, Tick(50), 99, 101, 0),
            new(InputEventType.KeyUp, Tick(60), 0, 0, 87),
            new(InputEventType.MouseUp, Tick(70), 99, 101, (int)MouseButton.Right)];
        foreach (var ev in events) recorder.Writer!.TryWrite(ev).Should().BeTrue();
        await vm.StopRecordingAsync();
        vm.Steps.Select(s => s.Step.GetType()).Should().Equal(
            typeof(ClickStep), typeof(MouseTrajectoryStep), typeof(KeyPressStep), typeof(MouseTrajectoryStep),
            typeof(MouseWheelStep), typeof(MouseTrajectoryStep), typeof(KeyPressStep), typeof(ClickStep));
        var paths = vm.Steps.Select(s => s.Step).OfType<MouseTrajectoryStep>().ToArray();
        paths.Select(p => p.Packets![0].Dx).Should().Equal(10, -3, -8);
        paths.Select(p => p.Packets![0].Dy).Should().Equal(3, 5, -7);
        paths.Should().OnlyContain(p => p.TimestampIsStart);
        paths.Select(p => p.Packets![0].OffsetTicks).Should().Equal(
            TimeSpan.FromMilliseconds(10).Ticks, TimeSpan.FromMilliseconds(10).Ticks, TimeSpan.FromMilliseconds(10).Ticks);
        (paths[1].RelativeTimestamp - paths[0].RelativeTimestamp).Should().Be(TimeSpan.FromMilliseconds(20));
        (paths[2].RelativeTimestamp - paths[1].RelativeTimestamp).Should().Be(TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public async Task TinySameMillisecondReversal_PreservesBothPacketsAndPreciseOffsets()
    {
        var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder);
        await vm.StartRecordingAsync();
        long start = Stopwatch.GetTimestamp();
        recorder.Writer!.TryWrite(new(InputEventType.MouseDown, start, 100, 100, (int)MouseButton.Right));
        recorder.Writer.TryWrite(new(InputEventType.MouseMove, start + Stopwatch.Frequency / 10_000, 101, 100, 0));
        recorder.Writer.TryWrite(new(InputEventType.MouseMove, start + Stopwatch.Frequency / 5_000, 100, 100, 0));
        recorder.Writer.TryWrite(new(InputEventType.MouseUp, start + Stopwatch.Frequency / 2_000, 100, 100, (int)MouseButton.Right));
        await vm.StopRecordingAsync();
        var path = vm.Steps.Select(s => s.Step).OfType<MouseTrajectoryStep>().Should().ContainSingle().Subject;
        path.Packets!.Select(p => p.Dx).Should().Equal(1, -1);
        path.Packets!.Select(p => p.T).Should().Equal(0, 0);
        path.Packets!.Select(p => p.OffsetTicks).Should().Equal(1000, 2000);
        path.Samples.Should().HaveCountGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task RestartWhileStopIsDraining_DoesNotReplaceTheActiveSession()
    {
        var recorder = new DelayedStopRecorder();
        using var vm = CreateRecordingViewModel(recorder);
        var original = vm.SelectedScript;
        vm.NewMacroCommand.Execute(null);
        var other = vm.SelectedScript;
        vm.SelectedScript = original;
        int scriptsBefore = vm.MacroScripts.Count;
        await vm.StartRecordingAsync();
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
        Task stopping = vm.StopRecordingAsync();
        vm.IsRecordingBusy.Should().BeTrue();
        vm.SelectedScript = other;
        vm.NewMacroCommand.Execute(null);
        vm.DeleteMacroCommand.Execute(null);
        vm.SelectedScript.Should().BeSameAs(original);
        vm.MacroScripts.Should().HaveCount(scriptsBefore);
        await vm.StartRecordingAsync();
        recorder.Starts.Should().Be(1);
        vm.StopRecordingAsync().Should().BeSameAs(stopping);
        recorder.AllowStop.TrySetResult();
        await stopping;
        vm.IsRecordingBusy.Should().BeFalse();
        vm.Steps.Should().HaveCount(2); // The accepted down and synthesized matching up.
        await vm.StartRecordingAsync();
        recorder.Starts.Should().Be(2);
        await vm.StopRecordingAsync();
        vm.Steps.Should().HaveCount(2);
    }

    private sealed class ExternalRecordingDesktop : IRecordingDesktopState
    {
        public bool IsOwnWindowAt(int x, int y) => false;
        public bool IsOwnForegroundWindow() => false;
        public bool IsAnyMouseButtonDown() => false;
        public bool IsPointerConstrained() => false;
        public bool IsMouseButtonDown(MouseButton button) => false;
    }

    [Fact]
    public async Task RawMouseMovement_WithStationaryCursorAndNoHeldButton_PreservesEveryDeltaAcrossKeysAndWheel()
    {
        var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder);
        await vm.StartRecordingAsync();
        long start = Stopwatch.GetTimestamp();
        long Tick(int ms) => start + ms * Stopwatch.Frequency / 1000;
        InputEvent[] events = [
            new(InputEventType.MouseMoveRelative, Tick(0), 1, -1, 0),
            new(InputEventType.MouseMoveRelative, Tick(1), -1, 1, 0),
            new(InputEventType.KeyDown, Tick(2), 0, 0, 87),
            new(InputEventType.MouseMoveRelative, Tick(3), -210, 89, 0),
            new(InputEventType.MouseWheel, Tick(4), 100, 100, -120),
            new(InputEventType.MouseMoveRelative, Tick(5), 3, -5, 0),
            new(InputEventType.KeyUp, Tick(6), 0, 0, 87)];
        foreach (var ev in events) recorder.Writer!.TryWrite(ev).Should().BeTrue();
        await vm.StopRecordingAsync();
        vm.Steps.Select(s => s.Step.GetType()).Should().Equal(typeof(MouseTrajectoryStep), typeof(KeyPressStep),
            typeof(MouseTrajectoryStep), typeof(MouseWheelStep), typeof(MouseTrajectoryStep), typeof(KeyPressStep));
        var paths = vm.Steps.Select(s => s.Step).OfType<MouseTrajectoryStep>().ToArray();
        paths.SelectMany(p => p.Packets!).Select(p => (p.Dx, p.Dy)).Should().Equal((1, -1), (-1, 1), (-210, 89), (3, -5));
        paths[0].Packets!.Select(p => p.OffsetTicks).Should().Equal(0, TimeSpan.FromMilliseconds(1).Ticks);
        paths.Skip(1).Should().OnlyContain(p => p.Packets![0].OffsetTicks == TimeSpan.FromMilliseconds(1).Ticks);
        paths.Should().OnlyContain(p => p.TimestampIsStart);
    }

    [Fact]
    public async Task RecordingOverlayFailure_StopsProducer_DrainsTail_AndReleasesStartupLatch()
    {
        var recorder = new TestInputRecorder();
        using var vm = CreateRecordingViewModel(recorder);
        var messages = new List<string>();
        vm.StatusChanged += message => messages.Add(message);
        int restored = 0;
        vm.LeaveCompactOverlay += () => restored++;
        void ThrowingOverlay()
        {
            recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
            throw new InvalidOperationException("overlay initialization failed");
        }
        vm.EnterCompactOverlay += ThrowingOverlay;
        await vm.StartRecordingAsync();
        recorder.IsRecording.Should().BeFalse();
        vm.IsRecording.Should().BeFalse();
        vm.IsRecordingBusy.Should().BeFalse();
        vm.Steps.Should().HaveCount(2);
        restored.Should().Be(1);
        messages.Should().Contain(message => message.Contains("overlay initialization failed"));
        vm.EnterCompactOverlay -= ThrowingOverlay;
        await vm.StartRecordingAsync();
        vm.IsRecording.Should().BeTrue();
        await vm.StopRecordingAsync();
    }

    [Fact]
    public async Task DisposeDuringRecording_DrainsAcceptedTail_AndCannotRestart()
    {
        var recorder = new TestInputRecorder();
        var vm = CreateRecordingViewModel(recorder);
        await vm.StartRecordingAsync();
        long tick = Stopwatch.GetTimestamp();
        for (int i = 0; i < 100; i++) recorder.Writer!.TryWrite(new(InputEventType.KeyDown, tick + i, 0, 0, 87));
        vm.Dispose();
        recorder.IsRecording.Should().BeFalse();
        vm.Steps.Should().HaveCount(101);
        ((KeyPressStep)vm.Steps[^1].Step).Stroke.Should().Be("Up");
        await vm.StartRecordingAsync();
        vm.IsRecording.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeDuringStartup_CancelsPendingStart_WithoutGhostRecording()
    {
        var recorder = new DelayedStartRecorder();
        var vm = CreateRecordingViewModel(recorder);
        Task starting = vm.StartRecordingAsync();
        await recorder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        vm.Dispose();
        await starting.WaitAsync(TimeSpan.FromSeconds(1));
        recorder.IsRecording.Should().BeFalse();
        vm.IsRecording.Should().BeFalse();
        vm.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeWhileStopDrains_JoinsCaptureAndConsumer_WithoutReplacingSession()
    {
        var recorder = new DelayedStopRecorder();
        var vm = CreateRecordingViewModel(recorder);
        await vm.StartRecordingAsync();
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
        Task stopping = vm.StopRecordingAsync();
        recorder.AllowStop.TrySetResult();
        vm.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(1));
        recorder.IsRecording.Should().BeFalse();
        vm.IsRecording.Should().BeFalse();
        vm.Steps.Should().HaveCount(2);
        recorder.Starts.Should().Be(1);
    }

    [Fact]
    public async Task StopDuringStartup_CancelsStartupAndDoesNotCreateAGhostRecording()
    {
        var recorder = new DelayedStartRecorder();
        using var vm = CreateRecordingViewModel(recorder);
        Task starting = vm.StartRecordingAsync();
        await recorder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await vm.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await starting;
        vm.IsRecording.Should().BeFalse();
        recorder.IsRecording.Should().BeFalse();
        vm.Steps.Should().BeEmpty();
    }

    private sealed class DelayedStartRecorder : IInputRecorder
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRecording { get; private set; }
        public async Task StartAsync(ChannelWriter<InputEvent> writer, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            IsRecording = true;
        }
        public Task StopAsync() { IsRecording = false; return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class DelayedStopRecorder : IInputRecorder
    {
        public readonly TaskCompletionSource AllowStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChannelWriter<InputEvent>? Writer { get; private set; }
        public int Starts { get; private set; }
        public bool IsRecording { get; private set; }
        public Task StartAsync(ChannelWriter<InputEvent> writer, CancellationToken ct)
        {
            Starts++;
            Writer = writer;
            IsRecording = true;
            return Task.CompletedTask;
        }
        public async Task StopAsync()
        {
            await AllowStop.Task;
            Writer?.TryComplete();
            IsRecording = false;
        }
        public void Dispose() { }
    }
}
