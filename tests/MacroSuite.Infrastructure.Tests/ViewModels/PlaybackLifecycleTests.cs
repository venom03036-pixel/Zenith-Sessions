using System.Windows.Threading;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Usage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void DisposePlayback_JoinsExecutorWithoutWaitingForItsUiContinuation()
        => OnPlaybackDispatcher(async () =>
        {
            var input = new PlaybackShutdownProbe();
            using var vm = CreatePlaybackLifecycleViewModel(input);
            vm.NewMacroCommand.Execute(null);
            vm.Steps.AddStep(new KeyPressStep(65, 0));
            vm.Steps.AddStep(new KeyPressStep(66, 0));
            var lateMessages = new List<string>();
            bool disposed = false;
            vm.StatusChanged += message => { if (disposed) lateMessages.Add(message); };

            Task run = vm.RunMacroAsync();
            await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            vm.Dispose(); // The UI dispatcher cannot run the awaiting continuation here.
            disposed = true;
            input.Joined.Should().BeTrue("shutdown must join the input worker before services are disposed");
            vm.IsRunning.Should().BeFalse();
            vm.SelectedScript!.IsRunning.Should().BeFalse();
            input.ReleaseCount.Should().BeGreaterThan(0);
            await run;
            await vm.RunMacroAsync();
            input.CallCount.Should().Be(1, "neither the second action nor a new run can start after disposal");
            lateMessages.Should().BeEmpty();
        });

    [Fact]
    public void DisposeDuringOverlayStartup_PreventsLatePlaybackActivation()
        => OnPlaybackDispatcher(async () =>
        {
            var input = new PlaybackShutdownProbe();
            using var vm = CreatePlaybackLifecycleViewModel(input);
            vm.NewMacroCommand.Execute(null);
            vm.Steps.AddStep(new KeyPressStep(65, 0));
            bool overlayEntered = false;
            vm.EnterCompactOverlay = () =>
            {
                overlayEntered = true;
                vm.Dispose(); // Close at the actual callback boundary, before any late activation.
            };
            Task run = vm.RunMacroAsync();
            await run;
            overlayEntered.Should().BeTrue();
            input.CallCount.Should().Be(0);
            vm.IsRunning.Should().BeFalse();
            vm.SelectedScript!.IsRunning.Should().BeFalse();
        });

    [Fact]
    public void InvalidatedScriptHotkey_DoesNotStartPlayback()
        => OnPlaybackDispatcher(async () =>
        {
            var input = new PlaybackShutdownProbe();
            using var vm = CreatePlaybackLifecycleViewModel(input);
            vm.NewMacroCommand.Execute(null);
            vm.Steps.AddStep(new KeyPressStep(65, 0));
            await vm.ToggleScriptAsync(vm.SelectedScript, () => false);
            input.CallCount.Should().Be(0);
            vm.IsRunning.Should().BeFalse();
        });

    [Fact]
    public void DirectPlaybackDuringRecordingStartup_IsBlockedBeforeInput()
        => OnPlaybackDispatcher(async () =>
        {
            var input = new PlaybackShutdownProbe();
            var recorder = new DelayedStartRecorder();
            using var vm = CreatePlaybackLifecycleViewModel(input, recorder);
            vm.Steps.AddStep(new KeyPressStep(65, 0));
            Task starting = vm.StartRecordingAsync();
            await recorder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            vm.IsRecording.Should().BeFalse();
            vm.IsRecordingBusy.Should().BeTrue();
            vm.RunCommand.CanExecute(null).Should().BeFalse();
            await vm.RunMacroAsync();
            input.CallCount.Should().Be(0);
            vm.IsRunning.Should().BeFalse();
            vm.Dispose();
            await starting;
        });

    [Fact]
    public void InvalidVisibleActionDraft_BlocksPlaybackInsteadOfRunningPreviousValues()
        => OnPlaybackDispatcher(async () =>
        {
            var input = new PlaybackShutdownProbe();
            using var vm = CreatePlaybackLifecycleViewModel(input);
            vm.AddDelayCommand.Execute(null);
            vm.ReplaceStepAt(0, new WaitUntilStep(new ColorCondition(new System.Drawing.Point(1, 2), 9, 8, 7))
                { Timeout = TimeSpan.FromMilliseconds(1) }, reloadPanel: true);
            vm.SelectedStep = vm.Steps[0];
            vm.ActionEditor.PaletteText = "not-a-color";
            var messages = new List<string>();
            vm.StatusChanged += messages.Add;
            await vm.RunMacroAsync();
            vm.ActionEditor.DraftError.Should().NotBeNullOrWhiteSpace();
            messages.Should().NotContain(message => message.StartsWith("Executing '"));
            vm.IsRunning.Should().BeFalse();
            input.CallCount.Should().Be(0);
        });

    private static MacroEditorViewModel CreatePlaybackLifecycleViewModel(IInputPlayer input, IInputRecorder? recorder = null)
    {
        var features = new DummyFeatureGate();
        return new MacroEditorViewModel(new DummyCaptureService(), input,
            new AIVisionRouter(new InMemorySecretVault()), new DummyApprovalGate(), features,
            new UsageTracker(new DummyUsageRepository(), features), new DummyAuditLogger(),
            seedDemoData: false, inputRecorder: recorder ?? new TestInputRecorder(),
            recordingDesktop: new ExternalRecordingDesktop()) { RecordingGracePeriodMs = 0 };
    }

    private sealed class PlaybackShutdownProbe : IInputPlayer
    {
        private int _calls;
        private int _releases;
        private int _joined;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _calls);
        public int ReleaseCount => Volatile.Read(ref _releases);
        public bool Joined => Volatile.Read(ref _joined) != 0;
        public bool IsPlaying => CallCount != 0 && !Joined;
        public async Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            finally { Volatile.Write(ref _joined, 1); }
        }
        public void ReleaseAllHeldKeys() => Interlocked.Increment(ref _releases);
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { }
    }

    private static void OnPlaybackDispatcher(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                Task work = body();
                _ = work.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
                Dispatcher.Run();
                work.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("playback shutdown must not wait on the UI thread it occupies");
        failure.Should().BeNull();
    }
}
