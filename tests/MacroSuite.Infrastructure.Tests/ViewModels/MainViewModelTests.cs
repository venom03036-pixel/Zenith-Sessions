using System.Buffers;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Audit;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Security;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Usage;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Licensing;
using MacroSuite.SDK.AI;
using Xunit;
using DrawingRect = System.Drawing.Rectangle;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed class MainViewModelTests : IDisposable
{
    private readonly string _testLicensePath;

    public MainViewModelTests()
    {
        _testLicensePath = Path.Combine(Path.GetTempPath(), $"Zenith_MainVM_{Guid.NewGuid():N}.bin");
    }

    public void Dispose()
    {
        if (File.Exists(_testLicensePath))
        {
            try { File.Delete(_testLicensePath); } catch { }
        }
    }

    private sealed class InMemorySecretVault : ISecretVault
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

        public void SetSecret(string keyName, string secretValue) => _secrets[keyName] = secretValue;
        public string? GetSecret(string keyName) => _secrets.TryGetValue(keyName, out var val) ? val : null;
        public bool HasSecret(string keyName) => _secrets.ContainsKey(keyName);
        public bool RemoveSecret(string keyName) => _secrets.Remove(keyName);
        public IReadOnlyList<string> ListSecretKeys() => _secrets.Keys.ToList();
    }

    private sealed class DummyCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(DrawingRect region, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();
        public void Dispose() { }
    }

    private sealed class DummyInputPlayer : IInputPlayer
    {
        public int EmittedEvents { get; private set; }
        public bool IsPlaying => false;
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            EmittedEvents += events.Count;
            return Task.CompletedTask;
        }
        public void ReleaseAllHeldKeys() { }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { }
    }

    private sealed class DummyApprovalGate : IApprovalGate
    {
        public ApprovalPolicy Policy => ApprovalPolicy.AutoApprove;
        public Task<ApprovalDecision> RequestApprovalAsync(
            AIAction action,
            ApprovalContext context,
            CancellationToken ct = default,
            bool forceInteractive = false) =>
            Task.FromResult(ApprovalDecision.Approved);
    }

    private sealed class DummyFeatureGate : IFeatureGate
    {
        public bool IsAvailable(FeatureId feature) => true;
        public void Require(FeatureId feature) { }
    }

    private sealed class DummyUsageRepository : IUsageRepository
    {
        public Task RecordAsync(UsageRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<decimal> GetTotalEstimatedCostAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0m);
        public Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UsageRecord>>(Array.Empty<UsageRecord>());
        public Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(default(UsageTotals));
        public void Dispose() { }
    }

    private sealed class DummyAuditLogger : IAuditLogger
    {
        public AuditLoggerHealth Health => AuditLoggerHealth.Running;
        public void Enqueue(AuditEntry entry) { }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private MainViewModel CreateMainViewModel(IScreenCaptureService? captureService = null,
        IImageMatcher? imageMatcher = null, IInputPlayer? inputPlayer = null)
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var gate = new DummyApprovalGate();
        var featureGate = new DummyFeatureGate();
        var usageRepo = new DummyUsageRepository();
        var tracker = new UsageTracker(usageRepo, featureGate);
        var audit = new DummyAuditLogger();
        var licenseService = new LocalLicenseService(_testLicensePath);

        var editorVM = new MacroEditorViewModel(
            captureService ?? new DummyCaptureService(),
            inputPlayer ?? new DummyInputPlayer(),
            router,
            gate,
            featureGate,
            tracker,
            audit,
            imageMatcher: imageMatcher);

        string settingsRoot = Path.Combine(Path.GetTempPath(), "ZenithMacrosMainVmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsRoot);
        var settingsVM = new SettingsViewModel(vault, router, paths: new AppDataPathResolver(settingsRoot));
        var licensingVM = new LicensingViewModel(licenseService);
        var usageVM = new UsageDashboardViewModel(tracker, usageRepo);
        var autoClickerVM = new AutoClickerViewModel(new DummyInputPlayer());

        return new MainViewModel(editorVM, settingsVM, licensingVM, usageVM, autoClickerVM, licenseService);
    }

    [Fact]
    public void InitialState_StartsOnEditorViewModel()
    {
        var mainVM = CreateMainViewModel();

        mainVM.CurrentViewModel.Should().BeSameAs(mainVM.EditorVM);
        mainVM.ExecutionState.Should().Be("Ready");
        mainVM.LicenseTier.Should().Be("FREE");
    }

    [Fact]
    public void PanicButton_ReflectsSavedBindChanges()
    {
        var mainVM = CreateMainViewModel();
        var changed = new List<string?>();
        mainVM.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        mainVM.SettingsVM.PanicHotkey = "Ctrl+Shift+L";

        mainVM.PanicHotkeyDisplay.Should().Be("Ctrl+Shift+L");
        mainVM.PanicButtonText.Should().Be("Panic stop · Ctrl+Shift+L");
        changed.Should().Contain(nameof(MainViewModel.PanicButtonText));
        changed.Should().Contain(nameof(MainViewModel.PanicHotkeyDisplay));
    }

    [Fact]
    public void NavigationCommands_SwitchCurrentViewModel()
    {
        var mainVM = CreateMainViewModel();

        mainVM.NavigateSettingsCommand.Execute(null);
        mainVM.CurrentViewModel.Should().BeSameAs(mainVM.SettingsVM);

        mainVM.NavigateLicensingCommand.Execute(null);
        mainVM.CurrentViewModel.Should().BeSameAs(mainVM.LicensingVM);

        mainVM.NavigateUsageCommand.Execute(null);
        mainVM.CurrentViewModel.Should().BeSameAs(mainVM.UsageVM);

        mainVM.NavigateEditorCommand.Execute(null);
        mainVM.CurrentViewModel.Should().BeSameAs(mainVM.EditorVM);
    }

    [Fact]
    public void PanicCommand_TriggersEmergencyTermination()
    {
        var mainVM = CreateMainViewModel();

        mainVM.PanicCommand.Execute(null);

        mainVM.ExecutionState.Should().Be("PANIC TERMINATED");
        mainVM.StatusMessage.Should().Contain("Emergency stop");
    }

    [Fact]
    public void ProviderChange_UpdatesActiveProvider()
    {
        var mainVM = CreateMainViewModel();

        mainVM.ActiveProvider.Should().Be(mainVM.SettingsVM.SelectedProvider.ToUpperInvariant());

        mainVM.SettingsVM.SelectedProvider = "anthropic";
        mainVM.ActiveProvider.Should().Be("ANTHROPIC");
    }

    [Fact]
    public async Task Panic_StopsInputEvenWhenAuxiliaryCancellationThrows()
    {
        var main = CreateMainViewModel();
        using var editor = main.EditorVM;
        using var clicker = main.AutoClickerVM;
        bool laterSubscriberCalled = false;
        main.EmergencyStopped += () => throw new InvalidOperationException("Test cancellation failure");
        main.EmergencyStopped += () => laterSubscriberCalled = true;
        clicker.ToggleRun();
        clicker.IsRunning.Should().BeTrue();

        main.TriggerPanic();
        await clicker.RunCompletion.WaitAsync(TimeSpan.FromSeconds(2));

        clicker.IsRunning.Should().BeFalse();
        laterSubscriberCalled.Should().BeTrue();
        main.TopStatusText.Should().Be("Emergency stopped");
    }

    [Fact]
    public async Task PausedRun_UsesSameStateAcrossShellAndTransport_AndExcludesOtherInput()
    {
        var main = CreateMainViewModel();
        using var editor = main.EditorVM;
        using var clicker = main.AutoClickerVM;
        editor.NewMacroCommand.Execute(null);
        editor.MacroName = "Inventory check";
        editor.Steps.Add(MacroStepItemViewModel.FromStep(new DelayStep(TimeSpan.FromSeconds(20)), 0));
        Task run = editor.RunMacroAsync();
        try
        {
            await Task.Delay(50);
            editor.PrimaryTransportCommand.Execute(null);
            for (int i = 0; i < 100 && !editor.IsPaused; i++) await Task.Delay(10);
            editor.IsPaused.Should().BeTrue();
            editor.PrimaryTransportText.Should().Be("Resume");
            main.TopStatusText.Should().Be(editor.TransportStateText).And.StartWith("Paused · Inventory check");
            main.GenerateMacroWithAICommand.CanExecute(null).Should().BeFalse();
            clicker.IsMacroActive.Should().BeTrue();
            clicker.ToggleRun();
            clicker.IsRunning.Should().BeFalse();
        }
        finally { await editor.StopMacroAsync(); await run.WaitAsync(TimeSpan.FromSeconds(3)); }
        main.GenerateMacroWithAICommand.CanExecute(null).Should().BeTrue();
        clicker.IsMacroActive.Should().BeFalse();
    }

    [Fact]
    public void ClickerShortcut_UsesSharedCaptureAndPersistsChanges()
    {
        var main = CreateMainViewModel();
        using var editor = main.EditorVM;
        using var clicker = main.AutoClickerVM;
        clicker.BeginHotkeyCaptureCommand.Execute(null);
        editor.QuickBindCaptureTarget.Should().Be("autoclicker");
        clicker.IsCapturingHotkey.Should().BeTrue();
        editor.TryAssignBind("autoclicker", "Ctrl+Shift+9").Should().BeTrue();
        clicker.StartHotkey.Should().Be("Ctrl+Shift+9");
        main.SettingsVM.AutoClickerHotkey.Should().Be("Ctrl+Shift+9");
    }

    [Fact]
    public void AIEntry_RequestsAuthoringWithoutStartingMacro()
    {
        var main = CreateMainViewModel();
        using var editor = main.EditorVM;
        using var clicker = main.AutoClickerVM;
        int requests = 0;
        main.MacroAuthoringRequested += () => requests++;
        main.GenerateMacroWithAICommand.Execute(null);
        requests.Should().Be(1);
        editor.IsRunning.Should().BeFalse();
        clicker.IsRunning.Should().BeFalse();
        main.NavigateAutoClickerCommand.Execute(null);
        main.IsAutoClickerSelected.Should().BeTrue();
        main.IsEditorSelected.Should().BeFalse();
    }

    [Fact]
    public void LeavingShortcutCaptureForAuthoring_ClearsEveryCaptureWithoutChangingBindings()
    {
        var main = CreateMainViewModel();
        using var editor = main.EditorVM;
        using var clicker = main.AutoClickerVM;
        string oldPanic = main.SettingsVM.PanicHotkey;
        main.SettingsVM.BeginHotkeyCaptureCommand.Execute("panic");
        editor.BeginQuickBindCapture("autoclicker");
        editor.ActionEditor.IsCapturingKey = true;
        editor.IsScriptHotkeyBoxFocused = true;

        main.CancelShortcutCapture();

        main.SettingsVM.HotkeyCaptureTarget.Should().BeEmpty();
        editor.QuickBindCaptureTarget.Should().BeEmpty();
        editor.ActionEditor.IsCapturingKey.Should().BeFalse();
        editor.IsScriptHotkeyBoxFocused.Should().BeFalse();
        clicker.IsCapturingHotkey.Should().BeFalse();
        main.SettingsVM.PanicHotkey.Should().Be(oldPanic);
    }

    [Fact]
    public async Task PanicCommand_WhenMacroIsRunning_TerminatesCleanly()
    {
        var mainVM = CreateMainViewModel();
        var waitStep = new DelayStep(TimeSpan.FromSeconds(10));
        mainVM.EditorVM.Steps.Add(new MacroStepItemViewModel(waitStep, "Wait", "Wait", "DELAY"));

        mainVM.EditorVM.RunCommand.Execute(null);
        await Task.Delay(50);
        mainVM.EditorVM.IsRunning.Should().BeTrue();

        mainVM.PanicCommand.Execute(null);

        for (int i = 0; i < 50; i++)
        {
            if (!mainVM.EditorVM.IsRunning) break;
            await Task.Delay(10);
        }

        mainVM.EditorVM.IsRunning.Should().BeFalse();
        mainVM.ExecutionState.Should().Be("PANIC TERMINATED");
    }

    [Fact]
    public async Task RunningMacro_UpdatesExecutionStateAndTopStatusPill()
    {
        var mainVM = CreateMainViewModel();
        var waitStep = new DelayStep(TimeSpan.FromSeconds(5));
        mainVM.EditorVM.Steps.Add(new MacroStepItemViewModel(waitStep, "Wait", "Wait", "DELAY"));

        mainVM.ExecutionState.Should().Be("Ready");
        mainVM.TopStatusText.Should().Be("Ready");
        mainVM.IsAnythingRunning.Should().BeFalse();

        mainVM.EditorVM.RunCommand.Execute(null);
        await Task.Delay(50);

        mainVM.EditorVM.IsRunning.Should().BeTrue();
        mainVM.IsAnythingRunning.Should().BeTrue();
        mainVM.ExecutionState.Should().StartWith("Running ·");
        mainVM.TopStatusText.Should().Be(mainVM.EditorVM.TransportStateText);
        mainVM.TopStatusDotFill.Should().Be("#4ADEA3");

        await mainVM.EditorVM.StopMacroAsync();
        for (int i = 0; i < 50; i++)
        {
            if (!mainVM.EditorVM.IsRunning) break;
            await Task.Delay(10);
        }

        mainVM.EditorVM.IsRunning.Should().BeFalse();
        mainVM.IsAnythingRunning.Should().BeFalse();
        mainVM.ExecutionState.Should().Be("Ready");
        mainVM.TopStatusText.Should().Be("Ready");
        mainVM.StatusMessage.Should().Contain("Macro stopped");
    }

    [Fact]
    public async Task FailedImagePlayback_KeepsTheErrorVisibleAfterReturningToIdle()
    {
        using var capture = new StatusTestCapture();
        using var matcher = new StatusTestMatcher();
        using var input = new DummyInputPlayer();
        var main = CreateMainViewModel(capture, matcher, input);
        using var editor = main.EditorVM;
        editor.NewMacroCommand.Execute(null);
        editor.AddDelayCommand.Execute(null);
        editor.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("test-template.png"))
        { RepeatWhileVisible = true }, reloadPanel: true);
        var messages = new List<string>();
        editor.StatusChanged += messages.Add;

        await editor.RunMacroAsync().WaitAsync(TimeSpan.FromSeconds(5));

        matcher.Calls.Should().Be(1, "the real repeat dispatcher must reach the failing fake matcher");
        capture.Frames.Should().ContainSingle().Which.IsDisposed.Should().BeTrue();
        input.EmittedEvents.Should().Be(0);
        editor.IsRunning.Should().BeFalse();
        main.ExecutionState.Should().Be("Ready");
        main.TopStatusText.Should().Be("Ready");
        main.StatusMessage.Should().StartWith("Macro error:").And.Contain(StatusTestMatcher.FailureMessage);
        main.StatusMessage.Should().Be(messages.Last());
        editor.MacroName = "Changed name after failure";
        main.StatusMessage.Should().Contain(StatusTestMatcher.FailureMessage,
            "an idle telemetry refresh must not erase the explanation either");

        editor.ReplaceStepAt(0, new DelayStep(TimeSpan.Zero), reloadPanel: true);
        await editor.RunMacroAsync().WaitAsync(TimeSpan.FromSeconds(5));
        editor.IsRunning.Should().BeFalse();
        main.StatusMessage.Should().Contain("completed successfully").And.NotContain("Macro error:",
            "a later successful run should replace the previous error with its own result");
    }

    private sealed class StatusTestCapture : IScreenCaptureService
    {
        public List<CaptureFrame> Frames { get; } = [];
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => CaptureRegionAsync(new DrawingRect(0, 0, 16, 16), ct);
        public Task<CaptureFrame> CaptureRegionAsync(DrawingRect region, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var frame = new CaptureFrame(16, 16, 64, FramePixelFormat.Bgra32, CaptureSource.Gdi,
                MemoryPool<byte>.Shared.Rent(16 * 64));
            Frames.Add(frame);
            return Task.FromResult(frame);
        }
        public void Dispose() { foreach (var frame in Frames) frame.Dispose(); }
    }

    private sealed class StatusTestMatcher : IImageMatcher
    {
        public const string FailureMessage = "The image search area is smaller than the saved template. Choose a larger area.";
        public int Calls { get; private set; }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, string templatePath, double minConfidence = 0.8,
            DrawingRect? searchRegion = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new MatchResult(false, 0, DrawingRect.Empty, MatchOutcome.Failed, FailureMessage));
        }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, byte[] templateImageBytes, double minConfidence = 0.8,
            DrawingRect? searchRegion = null, CancellationToken ct = default)
            => MatchAsync(frame, "bytes", minConfidence, searchRegion, ct);
        public void Dispose() { }
    }

    [Fact]
    public async Task AutoClicker_Running_UpdatesTelemetryAndTogglesCleanly()
    {
        var mainVM = CreateMainViewModel();
        mainVM.AutoClickerVM.ToggleRun();
        await Task.Delay(30);

        mainVM.AutoClickerVM.IsRunning.Should().BeTrue();
        mainVM.IsAnythingRunning.Should().BeTrue();
        mainVM.ExecutionState.Should().Contain("Auto clicker");
        mainVM.TopStatusText.Should().Contain("Auto clicker");
        mainVM.TopStatusDotFill.Should().Be("#4ADEA3");

        mainVM.AutoClickerVM.ToggleRun();
        await Task.Delay(30);

        mainVM.AutoClickerVM.IsRunning.Should().BeFalse();
        mainVM.IsAnythingRunning.Should().BeFalse();
        mainVM.ExecutionState.Should().Be("Ready");
        mainVM.TopStatusText.Should().Be("Ready");
        mainVM.StatusMessage.Should().Be("Ready", "the auto-clicker's transient active banner must be cleared");
    }

    [Fact]
    public async Task ToggleScriptAsync_TogglesRunningStateAndSelectedScript()
    {
        var mainVM = CreateMainViewModel();
        var waitStep = new DelayStep(TimeSpan.FromSeconds(5));

        var testScript = new MacroScriptItemViewModel("Test Script", 1, "F6", true);
        testScript.Steps.Add(new MacroStepItemViewModel(waitStep, "Wait", "Wait", "DELAY"));
        mainVM.EditorVM.MacroScripts.Add(testScript);

        // First toggle: starts running
        await mainVM.EditorVM.ToggleScriptAsync(testScript);
        await Task.Delay(50);

        mainVM.EditorVM.IsRunning.Should().BeTrue();
        testScript.IsRunning.Should().BeTrue();
        mainVM.TopStatusText.Should().Contain("Test Script");

        // Second toggle: stops running
        await mainVM.EditorVM.ToggleScriptAsync(testScript);
        for (int i = 0; i < 50; i++)
        {
            if (!mainVM.EditorVM.IsRunning) break;
            await Task.Delay(10);
        }

        mainVM.EditorVM.IsRunning.Should().BeFalse();
        testScript.IsRunning.Should().BeFalse();
        mainVM.TopStatusText.Should().Be("Ready");
    }
}
