using System.Threading.Channels;
using System.Windows.Threading;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Audit;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Security;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Usage;
using MacroSuite.Infrastructure.Licensing;
using MacroSuite.SDK.AI;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

// Actual view models share one backend, picker and arbiter. No hooks, window Show, capture,
// foreground discovery or native input are used; all files belong to the temporary fixture.
public sealed class MainHostInputOwnershipTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public Task ExplicitCompositionRequiresBothProducersToUseTheSameOwner() => OnUi(async () =>
    {
        await using var fixture = new Fixture();
        Assert.Same(fixture.Owner, fixture.Main.InputOperations);
        Assert.Same(fixture.Editor.InputOperations, fixture.Auto.InputOperations);
        Assert.Throws<ArgumentException>(() => fixture.CreateMain(new HostInputOperationArbiter()));
    });

    [Fact]
    public Task PendingAutoActivationExcludesEditorEvenBeforeRunningFlagsArePublished() => OnUi(async () =>
    {
        var activation = new Activation(); await using var fixture = new Fixture(activation);
        fixture.Editor.Steps.Add(MacroStepItemViewModel.FromStep(new DelayStep(TimeSpan.FromSeconds(10)), 0));
        fixture.Auto.ToggleRun(); await activation.Entered.Task.WaitAsync(Deadline);
        Guid? operation = fixture.Owner.Snapshot.OperationId;
        try
        {
            await fixture.Editor.RunMacroAsync().WaitAsync(Deadline);
            Assert.Equal(operation, fixture.Owner.Snapshot.OperationId);
            Assert.Equal(HostInputOperationKind.AutoClicker, fixture.Owner.Snapshot.Kind);
            Assert.Equal(0, fixture.Input.PlayCalls); Assert.Equal(0, fixture.Input.Clicks);
            fixture.Main.TriggerPanic();
            Assert.False(fixture.Auto.RunCompletion.IsCompleted);
            Assert.False(fixture.Main.InputRecovery.IsCompleted);
            Assert.True(fixture.Owner.Snapshot.IsBusy);
        }
        finally { activation.Release.TrySetResult(); await fixture.Auto.RunCompletion.WaitAsync(Deadline); await fixture.Main.InputRecovery.WaitAsync(Deadline); }
        Assert.Equal(0, fixture.Input.Clicks); Assert.Equal(0, fixture.Input.Releases);
        Assert.False(fixture.Owner.Snapshot.IsBusy);
    });

    [Fact]
    public Task PendingEditorActivationExcludesAutoAndClosingJoinsItsActualReturn() => OnUi(async () =>
    {
        var activation = new Activation(); await using var fixture = new Fixture();
        fixture.Editor.ActivationService = activation;
        fixture.Editor.Steps.Add(MacroStepItemViewModel.FromStep(new DelayStep(TimeSpan.FromSeconds(10)), 0));
        var run = fixture.Editor.RunMacroAsync(); await activation.Entered.Task.WaitAsync(Deadline);
        Task shutdown = Task.CompletedTask;
        try
        {
            fixture.Auto.ToggleRun(); await fixture.Auto.RunCompletion.WaitAsync(Deadline);
            Assert.Equal(HostInputOperationKind.Playback, fixture.Owner.Snapshot.Kind);
            Assert.Equal(0, fixture.Input.Clicks); Assert.Equal(0, fixture.Input.Attachments);
            shutdown = fixture.Main.ShutdownInputOperationsAsync();
            Assert.True(fixture.Owner.Snapshot.IsClosed); Assert.False(shutdown.IsCompleted);
            Assert.False(fixture.Input.Disposed);
            Assert.False(fixture.Owner.TryAcquire(HostInputOperationKind.Picker, out _, out _));
        }
        finally { activation.Release.TrySetResult(); await run.WaitAsync(Deadline); await shutdown.WaitAsync(Deadline); }
        Assert.False(fixture.Owner.Snapshot.IsBusy); Assert.Equal(0, fixture.Input.PlayCalls);
    });

    [Fact]
    public Task ShellEmergencySignalFencesTheSharedOwnerBeforeAnyProducerPresentation() => OnUi(async () =>
    {
        await using var fixture = new Fixture(); var lease = Acquire(fixture.Owner, HostInputOperationKind.Companion);
        try
        {
            fixture.Main.SignalEmergencyStop();
            Assert.False(lease.CanContinue); Assert.Equal(lease.OperationId, fixture.Owner.Snapshot.OperationId);
            Assert.False(lease.Completion.IsCompleted); Assert.Equal(0, fixture.Input.Releases);
            Assert.False(fixture.Owner.TryAcquire(HostInputOperationKind.Playback, out _, out _));
        }
        finally { await lease.CompleteAsync(true); }
    });

    [Fact]
    public Task ShutdownClosesBeforeAwaitingAndRetainsBackendThroughBlockedActualClick() => OnUi(async () =>
    {
        await using var fixture = new Fixture(); using var release = new ManualResetEventSlim();
        fixture.Input.ClickGate = release; fixture.Auto.ToggleRun();
        await fixture.Input.ClickEntered.Task.WaitAsync(Deadline);
        Task shutdown = fixture.Main.ShutdownInputOperationsAsync();
        try
        {
            Assert.True(fixture.Owner.Snapshot.IsClosed); Assert.True(fixture.Owner.Snapshot.IsBusy);
            Assert.Same(shutdown, fixture.Main.ShutdownInputOperationsAsync());
            Assert.False(shutdown.IsCompleted); Assert.False(fixture.Input.Disposed); Assert.Equal(0, fixture.Input.Releases);
        }
        finally { release.Set(); await shutdown.WaitAsync(Deadline); }
        Assert.Equal(1, fixture.Input.Clicks); Assert.Equal(1, fixture.Input.Releases);
        Assert.False(fixture.Owner.Snapshot.IsBusy); Assert.False(fixture.Input.Disposed);
    });

    [Fact]
    public Task DeliberatePanicRecoversOnlyTheRetiredAutoCleanupOwner() => OnUi(async () =>
    {
        await using var fixture = new Fixture(); fixture.Auto.RepeatOnce = true; fixture.Input.FailRelease = true;
        fixture.Auto.ToggleRun(); await fixture.Auto.RunCompletion.WaitAsync(Deadline);
        Assert.True(fixture.Owner.Snapshot.IsQuarantined); Assert.Equal(1, fixture.Input.Releases);
        fixture.Input.FailRelease = false; fixture.Main.TriggerPanic();
        await fixture.Main.InputRecovery.WaitAsync(Deadline);
        Assert.Equal(2, fixture.Input.Releases); Assert.False(fixture.Owner.Snapshot.IsBusy);
        Assert.Contains("cleanup completed", fixture.Main.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.All(fixture.Input.CleanupOwners, id => Assert.Equal(fixture.Input.LastLease!.OperationId, id));
    });

    [Theory]
    [InlineData(HostInputOperationKind.Picker)]
    [InlineData(HostInputOperationKind.CaptureTest)]
    [InlineData(HostInputOperationKind.Recording)]
    public Task PanicCannotUseGlobalKeyReleaseToClearAnUnownedResourceQuarantine(HostInputOperationKind kind) => OnUi(async () =>
    {
        await using var fixture = new Fixture(); var lease = Acquire(fixture.Owner, kind);
        fixture.Main.TriggerPanic();
        try
        {
            Assert.False(fixture.Main.InputRecovery.IsCompleted);
            await lease.CompleteAsync(false); await fixture.Main.InputRecovery.WaitAsync(Deadline);
            Assert.True(fixture.Owner.Snapshot.IsQuarantined); Assert.Equal(lease.OperationId, fixture.Owner.Snapshot.OperationId);
            Assert.Equal(0, fixture.Input.Releases);
            Assert.Contains("cleanup remains incomplete", fixture.Main.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await lease.CompleteAsync(false);
            await fixture.Owner.TryRecoverAsync(_ => Task.CompletedTask); // The synthetic resource owner explicitly confirms its own cleanup.
        }
    });

    [Fact]
    public Task OldPanicRecoveryNeverDrainsOrReleasesAReplacementOperation() => OnUi(async () =>
    {
        await using var fixture = new Fixture(); var old = Acquire(fixture.Owner, HostInputOperationKind.Picker);
        fixture.Main.TriggerPanic(); Task recovery = fixture.Main.InputRecovery;
        await old.CompleteAsync(true);
        var next = Acquire(fixture.Owner, HostInputOperationKind.Companion);
        try
        {
            await recovery.WaitAsync(Deadline);
            Assert.True(next.CanContinue); Assert.False(next.Completion.IsCompleted);
            Assert.Equal(next.OperationId, fixture.Owner.Snapshot.OperationId); Assert.Equal(0, fixture.Input.Releases);
        }
        finally { await next.CompleteAsync(true); }
    });

    [Fact]
    public Task PanicPublishesItsRecoveryBeforeReentrantClosingAndBothJoinActualOwner() => OnUi(async () =>
    {
        await using var fixture = new Fixture(); var lease = Acquire(fixture.Owner, HostInputOperationKind.Picker);
        Task? shutdown = null;
        fixture.Main.EmergencyStopped += () =>
        {
            Assert.False(fixture.Main.InputRecovery.IsCompleted);
            shutdown = fixture.Main.ShutdownInputOperationsAsync();
        };
        fixture.Main.TriggerPanic();
        try
        {
            Assert.NotNull(shutdown); Assert.False(shutdown!.IsCompleted);
            Assert.True(fixture.Owner.Snapshot.IsClosed); Assert.Equal(0, fixture.Input.Releases);
        }
        finally { await lease.CompleteAsync(true); if (shutdown is not null) await shutdown.WaitAsync(Deadline); }
        Assert.True(fixture.Main.InputRecovery.IsCompleted); Assert.False(fixture.Owner.Snapshot.IsBusy);
    });

    private static HostInputOperationLease Acquire(HostInputOperationArbiter owner, HostInputOperationKind kind)
    { Assert.True(owner.TryAcquire(kind, out var lease, out _)); return lease!; }

    private static async Task OnUi(Func<Task> body)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Host ownership synthetic STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
        finally { if (finished.Task.IsCompleted) Assert.True(await Task.Run(() => thread.Join(Deadline))); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Zenith_InputOwner_" + Guid.NewGuid().ToString("N"));
        private readonly LocalLicenseService _license;
        private readonly UsageDashboardViewModel _usage;
        private readonly Activation? _autoActivation;
        public HostInputOperationArbiter Owner { get; } = new();
        public Input Input { get; } = new();
        public MacroEditorViewModel Editor { get; }
        public AutoClickerViewModel Auto { get; }
        public SettingsViewModel Settings { get; }
        public LicensingViewModel Licensing { get; }
        public MainViewModel Main { get; }
        public Fixture(Activation? autoActivation = null)
        {
            _autoActivation = autoActivation;
            Directory.CreateDirectory(_root);
            var secrets = new Secrets(); var router = new AIVisionRouter(secrets); var features = new Features();
            var repository = new Usage(); var tracker = new UsageTracker(repository, features); var picker = new Picker();
            _license = new LocalLicenseService(Path.Combine(_root, "fixture-license.bin"));
            Editor = new(new Capture(), Input, router, new Approval(), features, tracker, new Audit(),
                inputRecorder: new Recorder(), locationPicker: picker, documentStore: new Documents(_root), inputOperations: Owner);
            Settings = new(secrets, router, paths: new AppDataPathResolver(_root));
            Licensing = new(_license); _usage = new(tracker, repository);
            Auto = new(Input, picker, paths: new AppDataPathResolver(_root), activationService: autoActivation, inputOperations: Owner);
            Main = CreateMain(Owner);
        }
        public MainViewModel CreateMain(HostInputOperationArbiter owner) => new(Editor, Settings, Licensing, _usage, Auto, _license, owner);
        public async ValueTask DisposeAsync()
        {
            _autoActivation?.Release.TrySetResult();
            (Editor.ActivationService as Activation)?.Release.TrySetResult();
            await Main.ShutdownInputOperationsAsync().WaitAsync(Deadline);
            await Settings.Appearance.ShutdownAsync().WaitAsync(Deadline);
            Editor.Dispose(); Auto.Dispose(); _usage.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Input : IInputPlayer, IHostInputOperationAwarePlayer
    {
        public int Clicks, Releases, PlayCalls, Attachments; public bool FailRelease, Disposed;
        public ManualResetEventSlim? ClickGate;
        public HostInputOperationLease? LastLease;
        public List<Guid> CleanupOwners { get; } = [];
        public TaskCompletionSource ClickEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsPlaying => false;
        public IDisposable AttachHostOperation(HostInputOperationLease lease)
        { Assert.True(lease.OwnsCleanup); LastLease = lease; Interlocked.Increment(ref Attachments); return new Attachment(); }
        public void ReleaseHostOperationInputs(HostInputOperationLease lease)
        { Assert.True(lease.OwnsCleanup); Assert.Same(LastLease, lease); Interlocked.Increment(ref Releases); CleanupOwners.Add(lease.OperationId); if (FailRelease) throw new InvalidOperationException("Synthetic cleanup failed."); }
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        { Interlocked.Increment(ref PlayCalls); return Task.CompletedTask; }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false)
        { Assert.True(LastLease?.CanContinue); Interlocked.Increment(ref Clicks); ClickEntered.TrySetResult(); if (ClickGate is { } gate && !gate.Wait(Deadline)) throw new TimeoutException("Synthetic click was not released."); }
        public void ReleaseAllHeldKeys() => throw new InvalidOperationException("Unscoped cleanup is forbidden in the composed fixture.");
        public void SendRelativeMove(int dx, int dy) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        private sealed class Attachment : IDisposable { public void Dispose() { } }
    }
    private sealed class Activation : IActivationService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsActive => true; public string StatusMessage => "Synthetic"; public VerifiedLicense? CurrentLicense => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public async Task<ActivationOperationResult> RefreshAsync(CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task; return new(true, "Synthetic"); }
        public Task<ActivationRequestResult> CreateRequestAsync(bool allowSoftwareFallback = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ActivationOperationResult> ImportAsync(string licenseText, CancellationToken ct = default) => throw new NotSupportedException();
        public void RequireActive() { }
    }
    private sealed class Picker : ILocationPicker
    {
        public bool IsArmed => false;
        public Task<ScreenPoint?> ArmAndWaitAsync(CancellationToken ct = default) => throw new NotSupportedException("No picker is invoked by this fixture.");
        public void Cancel() => throw new InvalidOperationException("An unrelated picker must not be cancelled.");
        public void Dispose() { }
    }
    private sealed class Recorder : IInputRecorder
    {
        public bool IsRecording => false;
        public Task StartAsync(ChannelWriter<InputEvent> writer, CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class Capture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi; public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(System.Drawing.Rectangle region, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
    private sealed class Documents(string root) : IMacroDocumentStore
    {
        public bool CanWriteLibrary => true;
        public IReadOnlyList<string> EnumerateLibraryFiles() => [];
        public string GetLibraryPath(Guid id) => Path.Combine(root, id.ToString("N") + ".zmacro");
        public void Save(MacroModel model, string path) { }
        public MacroDocumentLoadResult Load(string path, string? passphrase = null) => throw new NotSupportedException();
        public MacroDocumentLoadResult MigrateLibraryDocument(string path) => throw new NotSupportedException();
        public void ExportBackup(IReadOnlyList<MacroModel> models, string path, string passphrase) => throw new NotSupportedException();
        public void RecoverLibraryFromBackup(string path, string passphrase) => throw new NotSupportedException();
    }
    private sealed class Secrets : ISecretVault
    {
        public void SetSecret(string keyName, string secretValue) { }
        public string? GetSecret(string keyName) => null;
        public bool HasSecret(string keyName) => false;
        public bool RemoveSecret(string keyName) => false;
        public IReadOnlyList<string> ListSecretKeys() => [];
    }
    private sealed class Features : IFeatureGate
    { public bool IsAvailable(FeatureId feature) => true; public void Require(FeatureId feature) { } }
    private sealed class Approval : IApprovalGate
    {
        public ApprovalPolicy Policy => ApprovalPolicy.AutoApprove;
        public Task<ApprovalDecision> RequestApprovalAsync(AIAction action, ApprovalContext context, CancellationToken ct = default, bool forceInteractive = false)
            => Task.FromResult(ApprovalDecision.Approved);
    }
    private sealed class Audit : IAuditLogger
    {
        public AuditLoggerHealth Health => AuditLoggerHealth.Running;
        public void Enqueue(AuditEntry entry) { }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Usage : IUsageRepository
    {
        public Task RecordAsync(UsageRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<decimal> GetTotalEstimatedCostAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0m);
        public Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UsageRecord>>([]);
        public Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(default(UsageTotals));
        public void Dispose() { }
    }
}
