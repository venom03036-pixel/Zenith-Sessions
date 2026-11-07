using System.Diagnostics;
using FluentAssertions;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public async Task RecordingRepair_PanicBeforeConsumerDrain_PreservesEveryAcceptedInput()
    {
        var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder);
        await vm.StartRecordingAsync(); long time = Stopwatch.GetTimestamp();
        for (int i = 0; i < 1000; i++) recorder.Writer!.TryWrite(new(InputEventType.KeyDown, time + i, 0, 0, 87)).Should().BeTrue();
        vm.SignalEmergencyStop(); vm.PanicMacro();
        await vm.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        vm.Steps.Should().HaveCount(1001); vm.IsRecordingBusy.Should().BeFalse();
        vm.RecordingStateText.Should().Contain("emergency stop");
    }
    [Fact]
    public async Task RecordingRepair_ImmediateStartStopRepeated100Times_AppendsWithoutDuplicating()
    {
        var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder);
        var destination = vm.SelectedScript;
        for (int i = 0; i < 100; i++)
        {
            await vm.StartRecordingAsync(); await vm.StartRecordingAsync();
            recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
            Task first = vm.StopRecordingAsync(); Task second = vm.StopRecordingAsync();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        }
        vm.SelectedScript.Should().BeSameAs(destination); vm.Steps.Should().HaveCount(200);
        vm.IsRecordingBusy.Should().BeFalse();
    }
    [Fact]
    public async Task RecordingRepair_StopDuringLicenseValidation_CannotStartLater()
    {
        var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder);
        var license = new DelayedRecordingActivation(); vm.ActivationService = license;
        Task start = vm.StartRecordingAsync(); await license.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task stop = vm.StopRecordingAsync();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        license.Release.TrySetResult(); await start.WaitAsync(TimeSpan.FromSeconds(2));
        recorder.IsRecording.Should().BeFalse(); vm.IsRecordingBusy.Should().BeFalse(); vm.Steps.Should().BeEmpty();
    }
    [Fact]
    public async Task RecordingRepair_ManualPauseRemovesPauseTime_AndBalancesHeldKeys()
    {
        var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder);
        await vm.StartRecordingAsync();
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
        vm.PauseCommand.Execute(null); vm.IsRecordingPaused.Should().BeTrue();
        await Task.Delay(100);
        recorder.Writer.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 88));
        vm.PauseCommand.Execute(null); vm.IsRecordingPaused.Should().BeFalse();
        recorder.Writer.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 89));
        await vm.StopRecordingAsync();
        var keys = vm.Steps.Select(s => s.Step).OfType<KeyPressStep>().ToArray();
        keys.Select(k => k.KeyCode).Should().Equal(87,87,89,89);
        (keys[^1].RelativeTimestamp - keys[0].RelativeTimestamp).TotalMilliseconds.Should().BeLessThan(75);
    }
    [Fact]
    public async Task RecordingRepair_StopFromStartingNotification_UsesTheNewSessionCancellation()
    {
        var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder);
        Task? stopping = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.RecordingStateText) && vm.RecordingStateText == "Starting")
                stopping = vm.StopRecordingAsync();
        };
        await vm.StartRecordingAsync().WaitAsync(TimeSpan.FromSeconds(3));
        stopping.Should().NotBeNull(); await stopping!;
        recorder.IsRecording.Should().BeFalse(); vm.IsRecordingBusy.Should().BeFalse();
    }
    [Fact]
    public async Task RecordingRepair_OverlayStartupFailure_SavesAcceptedActions()
    {
        using var folder = new RoutingTempFolder(); var documents = new RecordingDocumentSpy(folder.Library);
        var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder, directory: folder.Library, documents: documents);
        int before = documents.Saves;
        vm.EnterCompactOverlay += () =>
        {
            recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87)).Should().BeTrue();
            throw new InvalidOperationException("test overlay unavailable");
        };
        await vm.StartRecordingAsync().WaitAsync(TimeSpan.FromSeconds(3));
        vm.Steps.Should().HaveCount(2); documents.Saves.Should().Be(before + 1);
        vm.SaveStateText.Should().Be("Saved"); vm.RecordingStateText.Should().Be("Faulted");
    }
    [Fact]
    public Task RecordingRepair_BackgroundStop_PublishesRecordingStateOnTheEditorDispatcher()
        => WpfTestApplication.RunAsync(async () =>
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder);
            await vm.StartRecordingAsync();
            bool offThread = false;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsRecording)) offThread |= !dispatcher.CheckAccess(); };
            await Task.Run(vm.StopRecordingAsync).WaitAsync(TimeSpan.FromSeconds(3));
            offThread.Should().BeFalse(); vm.IsRecordingBusy.Should().BeFalse();
        });
    [Fact]
    public async Task RecordingRepair_StalledRecoveryDrain_QuarantinesUntilSameSessionCanFinish()
    {
        using var folder = new RoutingTempFolder(); var documents = new DelayedRecoveryDocuments(folder.Library);
        var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder, directory: folder.Library, documents: documents);
        var deadlineReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveDeadline(string message)
        {
            if (message.StartsWith("Recording processing exceeded the stop deadline", StringComparison.Ordinal))
                deadlineReported.TrySetResult();
        }
        vm.StatusChanged += ObserveDeadline;
        Task? stopping = null;
        try
        {
            await vm.StartRecordingAsync();
            var originalOwner = vm.InputOperations.Snapshot;
            originalOwner.OperationId.Should().NotBeNull(); originalOwner.IsBusy.Should().BeTrue();
            recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87)).Should().BeTrue();
            await documents.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stopping = vm.StopRecordingAsync();
            // The existing five-second processing deadline still reports Faulted. Public
            // Stop now retains ownership until the actual consumer has exited as well.
            await deadlineReported.Task.WaitAsync(TimeSpan.FromSeconds(8));
            stopping.IsCompleted.Should().BeFalse();
            vm.RecordingStateText.Should().Be("Faulted"); vm.IsRecordingBusy.Should().BeTrue();
            vm.InputOperations.Snapshot.OperationId.Should().Be(originalOwner.OperationId);
            vm.InputOperations.Snapshot.IsBusy.Should().BeTrue();
            await vm.StartRecordingAsync(); recorder.IsRecording.Should().BeFalse();
            vm.InputOperations.Snapshot.OperationId.Should().Be(originalOwner.OperationId);
        }
        finally
        {
            // Always open the controlled checkpoint before joining Stop, including an
            // assertion failure. Waiting for Stop first would deadlock this fixture.
            documents.Release.TrySetResult();
            vm.StatusChanged -= ObserveDeadline;
            if (stopping is not null)
            {
                try { await stopping.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) when (stopping.IsCompleted) { }
            }
            else await vm.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        Func<Task> stop = () => stopping!;
        await stop.Should().ThrowAsync<TimeoutException>();
        vm.InputOperations.Snapshot.IsQuarantined.Should().BeTrue();
        await vm.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(3));
        vm.Steps.Should().HaveCount(2); vm.IsRecordingBusy.Should().BeFalse();
        vm.SaveStateText.Should().Be("Saved"); documents.Saved!.RecordingCommit.Should().NotBeNull();
    }
    [Fact]
    public async Task RecordingRepair_SaveUsesTheSessionSettingsSnapshot()
    {
        using var folder = new RoutingTempFolder(); var documents = new DelayedRecoveryDocuments(folder.Library);
        documents.Release.TrySetResult();
        var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder, directory: folder.Library, documents: documents);
        vm.Description = "Pinned description"; vm.RepeatCount = 3; vm.PlaybackSpeed = 1.5;
        await vm.StartRecordingAsync();
        vm.Description = "Later draft"; vm.RepeatCount = 7; vm.PlaybackSpeed = 2;
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
        await vm.StopRecordingAsync();
        documents.Saved!.Description.Should().Be("Pinned description");
        documents.Saved.Settings.RepeatCount.Should().Be(3);
        documents.Saved.Settings.PlaybackSpeed.Should().Be(1.5);
        documents.Saved.Steps.Should().HaveCount(2);
    }
    [Fact]
    public async Task RecordingRepair_ExternallyChangedDestinationIsPreserved()
    {
        using var folder = new RoutingTempFolder(); var documents = new RecordingDocumentSpy(folder.Library);
        var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder, directory: folder.Library, documents: documents);
        string path = documents.GetLibraryPath(vm.SelectedScript!.Id);
        Directory.CreateDirectory(folder.Library); await File.WriteAllTextAsync(path, "original fixture");
        await vm.StartRecordingAsync(); int saves = documents.Saves;
        recorder.Writer!.TryWrite(new(InputEventType.KeyDown, Stopwatch.GetTimestamp(), 0, 0, 87));
        await File.WriteAllTextAsync(path, "external replacement");
        await vm.StopRecordingAsync();
        documents.Saves.Should().Be(saves);
        (await File.ReadAllTextAsync(path)).Should().Be("external replacement");
        vm.Steps.Should().HaveCount(2); vm.SaveStateText.Should().Be("Save failed");
        vm.SaveMacroScript(vm.SelectedScript!); documents.Saves.Should().Be(saves);
        await vm.StartRecordingAsync(); recorder.IsRecording.Should().BeFalse();
    }
    [Fact]
    public async Task RecordingRepair_ConverterSettingsArePinnedBeforeLicenseValidation()
    {
        using var folder = new RoutingTempFolder(); var documents = new DelayedRecoveryDocuments(folder.Library);
        documents.Release.TrySetResult();
        var recorder = new TestInputRecorder(); using var vm = CreateRecordingViewModel(recorder, directory: folder.Library, documents: documents);
        var activation = new DelayedRecordingActivation(); vm.ActivationService = activation;
        vm.MouseMinDistancePx = 6; vm.MouseMinTimeMs = 12;
        Task starting = vm.StartRecordingAsync(); await activation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.MouseMinDistancePx = 500; vm.MouseMinTimeMs = 200;
        activation.Release.TrySetResult(); await starting;
        using var settings = System.Text.Json.JsonDocument.Parse(documents.SettingsSnapshot!);
        settings.RootElement.GetProperty("MouseMinDistancePx").GetInt32().Should().Be(6);
        settings.RootElement.GetProperty("MouseMinTimeMs").GetDouble().Should().Be(12);
        await vm.StopRecordingAsync();
    }
    private sealed class DelayedRecoveryDocuments(string directory) : IMacroDocumentStore, IRecordingRecoveryProvider, IRecordingRecoveryStore, IRecordingRecoverySession
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RecordingRecoveryStart _start = null!;
        public string? SettingsSnapshot => _start?.SettingsSnapshot;
        public MacroModel? Saved;
        public IRecordingRecoveryStore RecordingRecovery => this;
        public bool CanWriteLibrary => true;
        public Guid SessionId => _start.SessionId;
        public RecordingRecoveryCheckpoint Checkpoint => new(0, null, null);
        public IReadOnlyList<string> EnumerateLibraryFiles() => [];
        public string GetLibraryPath(Guid id) => Path.Combine(directory, id + ".zmacro");
        public void Save(MacroModel model, string path) => Saved = model;
        public MacroDocumentLoadResult Load(string path, string? passphrase = null) => throw new NotSupportedException();
        public MacroDocumentLoadResult MigrateLibraryDocument(string path) => throw new NotSupportedException();
        public void ExportBackup(IReadOnlyList<MacroModel> models, string path, string passphrase) => throw new NotSupportedException();
        public void RecoverLibraryFromBackup(string path, string passphrase) => throw new NotSupportedException();
        public Task<IRecordingRecoverySession> BeginAsync(RecordingRecoveryStart start, CancellationToken ct = default)
        { _start = start; return Task.FromResult<IRecordingRecoverySession>(this); }
        public Task<IReadOnlyList<RecordingRecoveryCandidate>> ReadPendingAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RecordingRecoveryCandidate>>([]);
        public Task<MacroModel?> RecoverAsync(RecordingRecoveryCandidate candidate, CancellationToken ct = default) => throw new NotSupportedException();
        public bool TryAppend(RecordingRecoveryPacket packet) => Release.Task.IsCompleted;
        public RecordingCommitMetadata CreateCapturedCommitMetadata(long sequence) => RecordingCommitMetadata.Create(SessionId, _start.Baseline, sequence);
        public async Task CheckpointAsync(CancellationToken ct = default) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
        public Task PrepareCommitAsync(MacroModel model, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteCommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class DelayedRecordingActivation : IActivationService
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsActive => true;
        public string StatusMessage => "Active";
        public VerifiedLicense? CurrentLicense => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public async Task<ActivationOperationResult> RefreshAsync(CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return new(true, "Active"); }
        public Task<ActivationRequestResult> CreateRequestAsync(bool allowSoftwareFallback = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ActivationOperationResult> ImportAsync(string text, CancellationToken ct = default) => throw new NotSupportedException();
        public void RequireActive() { }
    }
}
