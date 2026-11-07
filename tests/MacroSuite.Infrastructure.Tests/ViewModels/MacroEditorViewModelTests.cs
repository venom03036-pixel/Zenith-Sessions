using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Channels;
using FluentAssertions;
using MacroSuite.App.Common;
using MacroSuite.App.Overlays;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Audit;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Security;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Steps;
using MacroSuite.Infrastructure.AIProviders;
using MacroSuite.Core.Usage;
using MacroSuite.Core.Vision;
using MacroSuite.SDK.AI;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void Save_WhenExistingFileIsLocked_ReportsFailureWithoutSuccessMessage()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zenith-editor-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var featureGate = new DummyFeatureGate();
            using var vm = new MacroEditorViewModel(new DummyCaptureService(), new DummyInputPlayer(),
                new AIVisionRouter(new InMemorySecretVault()), new DummyApprovalGate(), featureGate,
                new UsageTracker(new DummyUsageRepository(), featureGate), new DummyAuditLogger(),
                seedDemoData: false, customMacrosDirectory: folder);
            vm.AddDelayCommand.Execute(null);
            vm.SaveMacroCommand.Execute(null);
            string path = vm.SelectedScript!.FilePath!;
            string original = File.ReadAllText(path);
            var messages = new List<string>();
            vm.StatusChanged += messages.Add;
            vm.MacroName = "Edited but unsaved";
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                vm.SaveMacroCommand.Execute(null);
            messages.Should().Contain(m => m.StartsWith("Could not save"));
            messages.Should().NotContain(m => m.StartsWith("Saved "));
            File.ReadAllText(path).Should().Be(original);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Playback_WhenEditorWasCleared_DoesNotRunStaleScriptActions()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        var script = vm.SelectedScript!;
        script.Actions.Add(new DelayStep(TimeSpan.Zero));
        vm.SelectedStep = vm.Steps[0];
        vm.RemoveStepCommand.Execute(null);
        var messages = new List<string>();
        vm.StatusChanged += messages.Add;
        await vm.RunMacroAsync();
        script.Actions.Should().BeEmpty();
        vm.IsRunning.Should().BeFalse();
        messages.Should().Contain("Macro has no steps to execute.");
    }

    [Fact]
    public async Task Playback_WhenOverlaySetupFails_RestoresIdleState()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.EnterCompactOverlay = () => throw new InvalidOperationException("Overlay unavailable");
        var messages = new List<string>();
        vm.StatusChanged += messages.Add;
        await vm.RunMacroAsync();
        vm.IsRunning.Should().BeFalse();
        vm.SelectedScript!.IsRunning.Should().BeFalse();
        messages.Should().Contain(m => m.Contains("Overlay unavailable"));
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
        public byte SampleB { get; init; } = 40;
        public byte SampleG { get; init; } = 90;
        public byte SampleR { get; init; } = 180;
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            int width = Math.Max(1, region.Width);
            int height = Math.Max(1, region.Height);
            int stride = width * 4;
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(stride * height);
            Span<byte> span = owner.Memory.Span[..(stride * height)];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * stride) + (x * 4);
                    span[offset] = SampleB;
                    span[offset + 1] = SampleG;
                    span[offset + 2] = SampleR;
                    span[offset + 3] = 255;
                }
            }

            return Task.FromResult(new CaptureFrame(
                width,
                height,
                stride,
                FramePixelFormat.Bgra32,
                CaptureSource.Gdi,
                owner,
                originX: region.X,
                originY: region.Y));
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) =>
            CaptureRegionAsync(new Rectangle(0, 0, 16, 16), ct);

        public void Dispose() { }
    }

    private sealed class DummyInputPlayer : IInputPlayer
    {
        public bool IsPlaying => false;
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default) =>
            Task.CompletedTask;
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

    private sealed class MockVisionProvider : IAIVisionProvider
    {
        public string ProviderId { get; set; } = "openai";
        public string DisplayName { get; set; } = "OpenAI";
        public IReadOnlyList<string> SupportedModels { get; set; } = ["gpt-4o"];
        public string DefaultModel { get; set; } = "gpt-4o";

        public Task<AIResponse> AnalyzeAsync(AIRequest request, string apiKey, CancellationToken ct = default)
            => Task.FromResult(new AIResponse { Action = new AIAction { ActionType = "click", X = 1, Y = 1 } });

        public Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey, string? modelId = null, CancellationToken ct = default)
            => Task.FromResult(ApiKeyValidationResult.Success(ProviderId, modelId ?? DefaultModel, TimeSpan.FromMilliseconds(1)));
    }

    private sealed class DummyAuditLogger : IAuditLogger
    {
        public AuditLoggerHealth Health => AuditLoggerHealth.Running;
        public void Enqueue(AuditEntry entry) { }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestInputRecorder : IInputRecorder
    {
        public bool IsRecording { get; private set; }
        public ChannelWriter<InputEvent>? Writer { get; private set; }

        public Task StartAsync(ChannelWriter<InputEvent> writer, CancellationToken ct)
        {
            IsRecording = true;
            Writer = writer;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsRecording = false;
            Writer?.TryComplete();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _ = StopAsync();
        }
    }

    private sealed class StubLocationPicker : ILocationPicker
    {
        public ScreenPoint? Next { get; set; }
        public bool IsArmed => false;
        public Task<ScreenPoint?> ArmAndWaitAsync(CancellationToken ct = default) => Task.FromResult(Next);
        public void Cancel() { }
        public void Dispose() { }
    }

    private MacroEditorViewModel CreateViewModel() => CreateViewModel(null);

    private MacroEditorViewModel CreateViewModel(ILocationPicker? locationPicker)
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var gate = new DummyApprovalGate();
        var featureGate = new DummyFeatureGate();
        var usageTracker = new UsageTracker(new DummyUsageRepository(), featureGate);
        var audit = new DummyAuditLogger();

        var vm = new MacroEditorViewModel(
            new DummyCaptureService(),
            new DummyInputPlayer(),
            router,
            gate,
            featureGate,
            usageTracker,
            audit,
            locationPicker: locationPicker);
        vm.AIVisionChatPrompter = () => "Default Test AI Prompt";
        return vm;
    }

    [Fact]
    public void AddClickStep_AddsAtCursor_WithoutAskingForAScreenRegion()
    {
        var vm = CreateViewModel();
        bool regionPrompted = false;
        vm.RegionPrompter = () => { regionPrompted = true; return new Rectangle(350, 450, 100, 50); };

        vm.AddClickCommand.Execute(null);

        // The screen-region overlay belongs to color/image capture only.
        regionPrompted.Should().BeFalse();
        vm.Steps.Should().HaveCount(1);
        var item = vm.Steps[0];
        item.TypeLabel.Should().Be("MOUSE");
        item.Step.Should().BeOfType<ClickStep>();
    }

    [Fact]
    public void AddKeypressStep_AddsEnterKeyToCollection()
    {
        var vm = CreateViewModel();

        vm.AddKeypressCommand.Execute(null);

        vm.Steps.Should().HaveCount(1);
        var item = vm.Steps[0];
        item.TypeLabel.Should().Be("KEYBOARD");
        item.Step.Should().BeOfType<KeyPressStep>();
        ((KeyPressStep)item.Step).KeyCode.Should().Be(13);
    }

    [Fact]
    public void AddDelayStep_AddsDelayStepToCollection()
    {
        var vm = CreateViewModel();

        vm.AddDelayCommand.Execute(null);

        vm.Steps.Should().HaveCount(1);
        var item = vm.Steps[0];
        item.TypeLabel.Should().Be("FLOW");
        item.Step.Should().BeOfType<DelayStep>();
        ((DelayStep)item.Step).Duration.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void AddAIStep_PromptsRegionAndAddsAIStep()
    {
        var vm = CreateViewModel();
        vm.RegionPrompter = () => new Rectangle(100, 200, 300, 400);

        vm.AddAIStepCommand.Execute(null);

        vm.Steps.Should().HaveCount(1);
        var item = vm.Steps[0];
        item.TypeLabel.Should().Be("AI VISION");
        item.Step.Should().BeOfType<AIStep>();
        var aiStep = (AIStep)item.Step;
        aiStep.TargetRegion.Should().Be(new Rectangle(100, 200, 300, 400));
        aiStep.ProviderId.Should().BeNull();
        aiStep.ModelId.Should().BeNull();
    }

    [Fact]
    public void AddAIStep_StampsProviderAndModelFromSettings()
    {
        var vm = CreateViewModel();
        var vault = new InMemorySecretVault();
        var gemini = new MockVisionProvider { ProviderId = "gemini", DefaultModel = "gemini-1.5-flash", SupportedModels = new[] { "gemini-1.5-flash" } };
        var router = new AIVisionRouter(vault, [gemini]);
        vm.AppSettings = new SettingsViewModel(
            vault,
            router,
            paths: new AppDataPathResolver(Path.Combine(Path.GetTempPath(), "ZenithMacrosEditorTests", Guid.NewGuid().ToString("N"))));
        vm.AppSettings.SelectedProvider = "gemini";
        vm.RegionPrompter = () => new Rectangle(1, 2, 3, 4);

        vm.AddAIStepCommand.Execute(null);

        var aiStep = vm.Steps[0].Step.Should().BeOfType<AIStep>().Subject;
        aiStep.ProviderId.Should().Be("gemini");
        aiStep.ModelId.Should().Be("gemini-1.5-flash");
    }

    [Fact]
    public void AddColorConditionStep_PromptsColorAndAddsWaitUntilStep()
    {
        var vm = CreateViewModel();
        vm.ColorPrompter = () => Color.FromArgb(200, 100, 50);

        vm.AddColorConditionCommand.Execute(null);

        vm.Steps.Should().HaveCount(1);
        var item = vm.Steps[0];
        item.TypeLabel.Should().Be("RULE");
        item.Step.Should().BeOfType<WaitUntilStep>().Which.ThrowOnTimeout.Should().BeTrue();
    }

    [Fact]
    public void ReorderSteps_MoveUpAndMoveDown_AdjustsCollectionOrder()
    {
        var vm = CreateViewModel();
        vm.AddKeypressCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);

        vm.Steps.Should().HaveCount(2);
        vm.Steps[0].TypeLabel.Should().Be("KEYBOARD");
        vm.Steps[1].TypeLabel.Should().Be("FLOW");

        // Select second item and move up
        vm.SelectedStep = vm.Steps[1];
        vm.MoveUpCommand.Execute(null);

        vm.Steps[0].TypeLabel.Should().Be("FLOW");
        vm.Steps[1].TypeLabel.Should().Be("KEYBOARD");

        // Move down
        vm.SelectedStep = vm.Steps[0];
        vm.MoveDownCommand.Execute(null);

        vm.Steps[0].TypeLabel.Should().Be("KEYBOARD");
        vm.Steps[1].TypeLabel.Should().Be("FLOW");
    }

    [Fact]
    public void RemoveStep_RemovesSelectedStep()
    {
        var vm = CreateViewModel();
        vm.AddKeypressCommand.Execute(null);
        vm.Steps.Should().HaveCount(1);

        vm.SelectedStep = vm.Steps[0];
        vm.RemoveStepCommand.Execute(null);

        vm.Steps.Should().BeEmpty();
        vm.SelectedStep.Should().BeNull();
    }

    [Fact]
    public void EditWaitDuration_UpdatesExistingStepWithoutAppending()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];

        vm.ActionEditor.DurationMs = 250;
        vm.ActionEditor.Apply();

        vm.Steps.Should().HaveCount(1);
        vm.Steps[0].ParameterTokens.Should().Be("250ms");
        vm.GetStepAt(0).Should().BeOfType<DelayStep>().Which.Duration.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void ChangeActionKind_ReplacesStepAtSameIndex()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];

        vm.ChangeSelectedActionKind(EditableActionKind.MouseClick);

        vm.Steps.Should().HaveCount(1);
        vm.Steps[0].Step.Should().BeOfType<ClickStep>();
    }

    [Fact]
    public void UndoRedo_RestoresEditedWait()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.DurationMs = 250;
        vm.ActionEditor.Apply();

        vm.UndoEditCommand.Execute(null);
        vm.GetStepAt(0).Should().BeOfType<DelayStep>().Which.Duration.Should().Be(TimeSpan.FromMilliseconds(500));

        vm.RedoEditCommand.Execute(null);
        vm.GetStepAt(0).Should().BeOfType<DelayStep>().Which.Duration.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void DuplicateStep_InsertsCloneAfterSelection()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.AddKeypressCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];

        vm.DuplicateStepCommand.Execute(null);

        vm.Steps.Should().HaveCount(3);
        vm.Steps[0].Step.Should().BeOfType<DelayStep>();
        vm.Steps[1].Step.Should().BeOfType<DelayStep>();
        vm.Steps[2].Step.Should().BeOfType<KeyPressStep>();
    }

    [Fact]
    public async Task PickLocation_WritesScreenCoordinatesOntoClick()
    {
        var picker = new StubLocationPicker { Next = new ScreenPoint(2733, 794) };
        var vm = CreateViewModel(picker);
        vm.RegionPrompter = () => new Rectangle(1, 1, 1, 1);
        vm.AddClickCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];

        await vm.ActionEditor.ArmPickLocationAsync();

        var click = vm.GetStepAt(0).Should().BeOfType<ClickStep>().Subject;
        click.Position.Should().Be(new Point(2733, 794));
        click.IsClientRelative.Should().BeFalse();
        vm.ActionEditor.IsPickingLocation.Should().BeFalse();
    }

    [Fact]
    public void AddIfAndWhile_UseVisualPresenceNotSpecialClickActions()
    {
        var vm = CreateViewModel();
        vm.RegionPrompter = () => new Rectangle(10, 20, 40, 30);

        vm.AddIfCommand.Execute(null);
        vm.AddWhileCommand.Execute(null);

        vm.Steps.Should().HaveCount(2);
        var iff = vm.Steps[0].Step.Should().BeOfType<IfThenElseStep>().Subject;
        iff.Condition.Should().BeOfType<VisualPresenceCondition>()
            .Which.Kind.Should().Be(VisualPresenceKind.ColorPresent);
        var presence = (VisualPresenceCondition)iff.Condition;
        presence.R.Should().Be(180);
        presence.G.Should().Be(90);
        presence.B.Should().Be(40);
        var loop = vm.Steps[1].Step.Should().BeOfType<LoopStep>().Subject;
        loop.WhileCondition.Should().BeOfType<VisualPresenceCondition>();
        loop.MaxDurationMs.Should().Be(30_000);

        vm.SelectedStep = vm.Steps[1];
        vm.AppendToSelectedBlock("body", new KeyPressStep(70) { Stroke = "Down" });
        vm.AppendToSelectedBlock("body", new DelayStep(TimeSpan.FromMilliseconds(50)));
        vm.AppendToSelectedBlock("body", new KeyPressStep(70) { Stroke = "Up" });
        ((LoopStep)vm.GetStepAt(1)!).BodySteps.Should().HaveCount(3);
    }

    [Fact]
    public void StepIsEnabled_TogglingUpdatesUnderlyingMacroStep()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);

        var item = vm.Steps[0];
        item.IsEnabled.Should().BeTrue();
        item.Step.IsEnabled.Should().BeTrue();

        item.IsEnabled = false;
        item.IsEnabled.Should().BeFalse();
        item.Step.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void PanicMacro_FiresStatusChangeAndEmergencyMessage()
    {
        var vm = CreateViewModel();
        string? statusReceived = null;
        vm.StatusChanged += s => statusReceived = s;

        vm.PanicCommand.Execute(null);

        statusReceived.Should().Contain("Emergency stop initiated");
    }

    [Fact]
    public void NewMacroCommand_CreatesNewScriptAndSelectsIt()
    {
        var vm = CreateViewModel();
        var initialCount = vm.MacroScripts.Count;

        vm.NewMacroCommand.Execute(null);

        vm.MacroScripts.Should().HaveCount(initialCount + 1);
        vm.MacroName.Should().StartWith("Script #");
        vm.IsEditingMacro.Should().BeTrue();
        vm.IsScriptsTabVisible.Should().BeTrue();
        vm.HasNoSteps.Should().BeTrue();
        vm.HasSteps.Should().BeFalse();
    }

    [Fact]
    public void HasSteps_And_HasNoSteps_TrackStepCountProperly()
    {
        var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);

        vm.HasNoSteps.Should().BeTrue();
        vm.HasSteps.Should().BeFalse();

        vm.AddDelayCommand.Execute(null);

        vm.HasSteps.Should().BeTrue();
        vm.HasNoSteps.Should().BeFalse();

        vm.SelectedStep = vm.Steps[0];
        vm.RemoveStepCommand.Execute(null);

        vm.HasNoSteps.Should().BeTrue();
        vm.HasSteps.Should().BeFalse();
    }

    [Fact]
    public void AddImageCommand_AddsWaitUntilAndClickSteps_WhenAutoClickIsTrue()
    {
        var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);

        vm.ImagePrompter = () => new ImageTemplateSelection("c:\\test\\my_target.png", 0.85, true, new Point(640, 360));

        vm.AddImageCommand.Execute(null);

        vm.Steps.Should().HaveCount(2);

        var waitStep = vm.Steps[0];
        waitStep.ActionKeyword.Should().Be("WAIT_IMAGE");
        waitStep.ParameterTokens.Should().Contain("my_target.png");
        waitStep.ParameterTokens.Should().Contain("85%");
        waitStep.Title.Should().Contain("my_target.png");
        waitStep.Step.Should().BeOfType<WaitUntilStep>().Which.ThrowOnTimeout.Should().BeTrue();
        ((WaitUntilStep)waitStep.Step).PollInterval.Should().Be(TimeSpan.FromMilliseconds(100));

        var clickStep = vm.Steps[1];
        clickStep.ActionKeyword.Should().Be("LEFT_CLICK");
        clickStep.ParameterTokens.Should().Be("detected image");
        var click = clickStep.Step.Should().BeOfType<ClickStep>().Subject;
        click.XVariable.Should().Be(ImageCondition.LastMatchXVariable);
        click.YVariable.Should().Be(ImageCondition.LastMatchYVariable);
    }

    [Fact]
    public void AddImageCommand_DoesNothing_WhenCancelled()
    {
        var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);

        vm.ImagePrompter = () => null;

        vm.AddImageCommand.Execute(null);

        vm.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task LoopStep_WithFailingCondition_ExitsCleanlyWithoutCrashing()
    {
        var vm = CreateViewModel();
        var condition = new ImageCondition("non_existent_file.png", minConfidence: 0.9);
        var loopStep = new LoopStep(5, new List<MacroStep> { new DelayStep(TimeSpan.FromMilliseconds(10)) }, whileCondition: condition);

        var controller = new MacroController();
        var context = new MacroContext(new DummyCaptureService(), controller);

        var act = async () => await loopStep.ExecuteAsync(context);
        await act.Should().ThrowAsync<ConditionEvaluationException>();
    }

    [Fact]
    public async Task ImageCondition_WithMissingMatcher_FailsClosed()
    {
        var condition = new ImageCondition("c:\\missing\\template.png", minConfidence: 0.8);
        var controller = new MacroController();
        var context = new MacroContext(new DummyCaptureService(), controller);

        var act = async () => await condition.EvaluateAsync(context);
        await act.Should().ThrowAsync<ConditionEvaluationException>();
    }

    [Fact]
    public void RepeatUntilStopped_TogglesCanEditRepeatCount_AndUpdatesDisplayText()
    {
        var vm = CreateViewModel();
        vm.RepeatUntilStopped.Should().BeFalse();
        vm.CanEditRepeatCount.Should().BeTrue();
        vm.RepeatCount = 3;
        vm.RepeatCountText.Should().Be("3");

        vm.RepeatUntilStopped = true;
        vm.CanEditRepeatCount.Should().BeFalse();
        vm.RepeatCountText.Should().Be("∞");

        vm.RepeatUntilStopped = false;
        vm.CanEditRepeatCount.Should().BeTrue();
        vm.RepeatCountText.Should().Be("3");
    }

    [Fact]
    public void RepeatUntilStopped_IsPerScript_AndRestoredOnSelection()
    {
        var vm = CreateViewModel();
        vm.MacroScripts.Count.Should().BeGreaterThan(1);

        var first = vm.MacroScripts[0];
        var second = vm.MacroScripts[1];
        vm.SelectedScript = first;
        vm.RepeatUntilStopped = true;
        vm.RepeatCount = 7;
        first.RepeatUntilStopped.Should().BeTrue();
        first.RepeatCount.Should().Be(7);

        vm.SelectedScript = second;
        vm.RepeatUntilStopped.Should().BeFalse();

        vm.SelectedScript = first;
        vm.RepeatUntilStopped.Should().BeTrue();
        vm.RepeatCount.Should().Be(7);
        vm.RepeatCountText.Should().Be("∞");
    }

    [Fact]
    public void RepeatCountInputText_UpdatesRepeatCount_AndSynchronizes()
    {
        var vm = CreateViewModel();
        vm.RepeatCount = 1;
        vm.RepeatCountInputText.Should().Be("1");

        // Typing valid number updates RepeatCount
        vm.RepeatCountInputText = "25";
        vm.RepeatCount.Should().Be(25);

        // Setting RepeatCount updates RepeatCountInputText
        vm.RepeatCount = 50;
        vm.RepeatCountInputText.Should().Be("50");

        // Typing invalid string does not crash
        vm.RepeatCountInputText = "abc";
        vm.RepeatCount.Should().Be(50); // Unchanged
    }

    [Fact]
    public void TryChangeHotkey_SetsCustomKeystroke_SuchAsPeriod()
    {
        var vm = CreateViewModel();
        vm.SelectedScript.Should().NotBeNull();

        bool result = vm.TryChangeHotkey(".");

        result.Should().BeTrue();
        vm.SelectedScript!.Hotkey.Should().Be(".");
        vm.CurrentHotkey.Should().Be(".");
    }

    [Fact]
    public void TryChangeHotkey_WithConflict_ReassignsWhenUserConfirms()
    {
        var vm = CreateViewModel();
        vm.MacroScripts.Count.Should().BeGreaterThan(1);

        var script1 = vm.MacroScripts[0];
        var script2 = vm.MacroScripts[1];
        script1.Hotkey = "F9";
        script2.Hotkey = "F11";

        vm.SelectedScript = script2;

        bool prompterCalled = false;
        string? promptedKey = null;
        string? promptedMacro = null;

        vm.ConfirmHotkeyConflictPrompter = prompt =>
        {
            prompterCalled = true;
            promptedKey = prompt.Hotkey;
            promptedMacro = prompt.PreviousOwner;
            return HotkeyConflictChoice.UnbindPrevious;
        };

        bool result = vm.TryChangeHotkey("F9");

        result.Should().BeTrue();
        prompterCalled.Should().BeTrue();
        promptedKey.Should().Be("F9");
        promptedMacro.Should().Be(script1.Name);

        // Previous owner is unbound so the key is free
        script1.Hotkey.Should().BeEmpty();
        // Target script received the hotkey
        script2.Hotkey.Should().Be("F9");
        vm.CurrentHotkey.Should().Be("F9");
        vm.SelectedScript.Should().BeSameAs(script2);
    }

    [Fact]
    public void CompleteQuickBindCapture_ScriptConflict_DoesNotOpenTheOtherMacro()
    {
        var vm = CreateViewModel();
        vm.MacroScripts.Count.Should().BeGreaterThan(1);

        var owner = vm.MacroScripts[0];
        var target = vm.MacroScripts[1];
        owner.Hotkey = "Ctrl+5";
        target.Hotkey = "Ctrl+9";
        vm.SelectedScript = target;

        vm.ConfirmHotkeyConflictPrompter = _ => HotkeyConflictChoice.UnbindPrevious;

        vm.BeginQuickBindCapture($"script:{target.Id:N}");
        vm.CompleteQuickBindCapture("Ctrl+5").Should().BeTrue();

        vm.SelectedScript.Should().BeSameAs(target);
        owner.Hotkey.Should().BeEmpty();
        target.Hotkey.Should().Be("Ctrl+5");
        vm.QuickBindCaptureTarget.Should().BeEmpty();
    }

    [Fact]
    public void TryChangeHotkey_WithConflict_CancelsWhenUserRejects()
    {
        var vm = CreateViewModel();
        vm.MacroScripts.Count.Should().BeGreaterThan(1);

        var script1 = vm.MacroScripts[0];
        var script2 = vm.MacroScripts[1];
        script1.Hotkey = "F8";
        script2.Hotkey = "F9";

        vm.SelectedScript = script2;

        bool prompterCalled = false;
        vm.ConfirmHotkeyConflictPrompter = prompt =>
        {
            prompterCalled = true;
            return HotkeyConflictChoice.Cancel;
        };

        bool result = vm.TryChangeHotkey("F8");

        result.Should().BeFalse();
        prompterCalled.Should().BeTrue();

        // Script 1 still has F8
        script1.Hotkey.Should().Be("F8");
        // Script 2 kept its original F9
        script2.Hotkey.Should().Be("F9");
        vm.CurrentHotkey.Should().Be("F9");
    }

    [Fact]
    public void TryChangeHotkey_Cancel_RestoresOriginalAndLeavesOtherOwnerAlone()
    {
        var vm = CreateViewModel();
        vm.MacroScripts.Count.Should().BeGreaterThan(1);

        var owner = vm.MacroScripts[0];
        var target = vm.MacroScripts[1];
        owner.Hotkey = "Ctrl+4";
        target.Hotkey = "Ctrl+2";
        vm.SelectedScript = target;
        vm.CurrentHotkey = "Ctrl+9";

        string? originalFromPrompt = null;
        vm.ConfirmHotkeyConflictPrompter = prompt =>
        {
            originalFromPrompt = prompt.OriginalHotkey;
            return HotkeyConflictChoice.Cancel;
        };

        vm.TryChangeHotkey("Ctrl+4").Should().BeFalse();
        originalFromPrompt.Should().Be("Ctrl+2");
        owner.Hotkey.Should().Be("Ctrl+4");
        target.Hotkey.Should().Be("Ctrl+2");
        vm.CurrentHotkey.Should().Be("Ctrl+2");
        vm.SelectedScript.Should().BeSameAs(target);
    }

    [Fact]
    public void ScriptPersistence_SavesToDisk_AndLoadsOnNewInstance()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"zenith_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var vault = new InMemorySecretVault();
            var router = new AIVisionRouter(vault);
            var gate = new DummyApprovalGate();
            var featureGate = new DummyFeatureGate();
            var usageTracker = new UsageTracker(new DummyUsageRepository(), featureGate);
            var audit = new DummyAuditLogger();

            var vm1 = new MacroEditorViewModel(
                new DummyCaptureService(),
                new DummyInputPlayer(),
                router,
                gate,
                featureGate,
                usageTracker,
                audit,
                seedDemoData: true,
                customMacrosDirectory: tempDir);

            // Create a custom script
            vm1.NewMacroCommand.Execute(null);
            var createdScript = vm1.SelectedScript;
            createdScript.Should().NotBeNull();
            createdScript!.Name = "My Persisted Custom Macro";
            createdScript.Hotkey = ".";
            vm1.RepeatUntilStopped = true;
            vm1.RepeatCount = 12;
            vm1.PlaybackSpeed = 1.5;
            vm1.SaveMacroScript(createdScript);

            // Files should exist in tempDir
            var files = Directory.GetFiles(tempDir, "*.zmacro");
            files.Should().NotBeEmpty();

            // Create a second ViewModel pointing to the same directory
            var vm2 = new MacroEditorViewModel(
                new DummyCaptureService(),
                new DummyInputPlayer(),
                router,
                gate,
                featureGate,
                usageTracker,
                audit,
                seedDemoData: false,
                customMacrosDirectory: tempDir);

            // Verify it was loaded into MacroScripts
            var loadedScript = vm2.MacroScripts.FirstOrDefault(s => s.Name == "My Persisted Custom Macro");
            loadedScript.Should().NotBeNull();
            loadedScript!.Hotkey.Should().Be(".");
            loadedScript.RepeatUntilStopped.Should().BeTrue();
            loadedScript.RepeatCount.Should().Be(12);
            loadedScript.PlaybackSpeed.Should().Be(1.5);

            vm2.SelectedScript = loadedScript;
            vm2.RepeatUntilStopped.Should().BeTrue();
            vm2.RepeatCount.Should().Be(12);
            vm2.RepeatCountText.Should().Be("∞");

            // Now test Delete removes it from disk and list
            vm2.DeleteScript(loadedScript);
            vm2.MacroScripts.Should().NotContain(s => s.Name == "My Persisted Custom Macro");
            File.Exists(loadedScript.FilePath).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void ScriptHotkey_MatchingTransportBind_IsRejected()
    {
        var vm = CreateViewModel();
        vm.SetReservedTransportHotkeys(new[] { "F7", "F10", "F4", "F5", "ESC" });

        var script = vm.MacroScripts[0];
        string original = script.Hotkey;

        vm.TryChangeHotkey("F7", script).Should().BeFalse();
        script.Hotkey.Should().Be(original);

        vm.TryChangeHotkey("Ctrl+Shift+J", script).Should().BeTrue();
        script.Hotkey.Should().Be("Ctrl+Shift+J");
    }

    [Fact]
    public void ResolveTransportHotkeyConflicts_MovesCollidingScriptsToFreeKeys()
    {
        var vm = CreateViewModel();
        var script = vm.MacroScripts[0];
        script.Hotkey = "F7";

        vm.SetReservedTransportHotkeys(new[] { "F7", "F10", "F4", "F5", "ESC" });
        vm.ResolveTransportHotkeyConflicts();

        script.Hotkey.Should().NotBeEmpty();
        script.Hotkey.Should().StartWith("Ctrl+");
        vm.IsReservedTransportHotkey(script.Hotkey).Should().BeFalse();
        vm.MacroScripts.Where(s => !string.IsNullOrWhiteSpace(s.Hotkey))
            .Should().OnlyHaveUniqueItems(s => s.Hotkey.ToUpperInvariant());
    }

    [Fact]
    public void AssignMissingScriptHotkeys_FillsBlankBindsWithCtrlCombos()
    {
        var vm = CreateViewModel();
        var script = vm.MacroScripts[0];
        script.Hotkey = string.Empty;

        vm.AssignMissingScriptHotkeys();

        script.Hotkey.Should().StartWith("Ctrl+");
        HotkeyCodec.TryParse(script.Hotkey, out _, out _).Should().BeTrue();
        vm.MacroScripts.Where(s => !string.IsNullOrWhiteSpace(s.Hotkey))
            .Should().OnlyHaveUniqueItems(s => s.Hotkey.ToUpperInvariant());
    }

    [Fact]
    public async Task StartRecording_WhenCurrentScriptHasActions_RecordsIntoThatScript()
    {
        var recorder = new TestInputRecorder();
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var gate = new DummyApprovalGate();
        var featureGate = new DummyFeatureGate();
        var usageTracker = new UsageTracker(new DummyUsageRepository(), featureGate);
        var audit = new DummyAuditLogger();

        var vm = new MacroEditorViewModel(
            new DummyCaptureService(),
            new DummyInputPlayer(),
            router,
            gate,
            featureGate,
            usageTracker,
            audit,
            seedDemoData: false,
            inputRecorder: recorder);

        vm.NewMacroCommand.Execute(null);
        var occupied = vm.SelectedScript;
        occupied.Should().NotBeNull();
        vm.AddDelayCommand.Execute(null);
        vm.Steps.Should().NotBeEmpty();
        int countBefore = vm.MacroScripts.Count;
        int stepsBefore = vm.Steps.Count;

        await vm.StartRecordingAsync();

        try
        {
            vm.IsRecording.Should().BeTrue();
            vm.MacroScripts.Should().HaveCount(countBefore);
            vm.SelectedScript.Should().BeSameAs(occupied);
            vm.Steps.Should().HaveCount(stepsBefore);
        }
        finally
        {
            await vm.StopRecordingAsync();
        }
    }

    [Fact]
    public async Task StartRecording_WhenCurrentScriptIsEmpty_ReusesIt()
    {
        var recorder = new TestInputRecorder();
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var gate = new DummyApprovalGate();
        var featureGate = new DummyFeatureGate();
        var usageTracker = new UsageTracker(new DummyUsageRepository(), featureGate);
        var audit = new DummyAuditLogger();

        var vm = new MacroEditorViewModel(
            new DummyCaptureService(),
            new DummyInputPlayer(),
            router,
            gate,
            featureGate,
            usageTracker,
            audit,
            seedDemoData: false,
            inputRecorder: recorder);

        vm.NewMacroCommand.Execute(null);
        var empty = vm.SelectedScript;
        empty.Should().NotBeNull();
        empty!.Actions.Should().BeEmpty();
        int countBefore = vm.MacroScripts.Count;

        await vm.StartRecordingAsync();

        try
        {
            vm.IsRecording.Should().BeTrue();
            vm.MacroScripts.Should().HaveCount(countBefore);
            vm.SelectedScript.Should().BeSameAs(empty);
        }
        finally
        {
            await vm.StopRecordingAsync();
        }
    }

    [Fact]
    public void QuickBindCapture_AcceptsAnyKey_AndRefusesReuseAcrossTransports()
    {
        var vm = CreateViewModel();
        string? changedTarget = null;
        vm.QuickBindChanged += (target, _) => changedTarget = target;

        vm.BeginQuickBindCapture("record");
        vm.CompleteQuickBindCapture("Ctrl+Space").Should().BeTrue();
        vm.RecordHotkey.Should().Be("Ctrl+Space");
        changedTarget.Should().Be("record");

        // Stop may not steal the key Record already owns.
        vm.BeginQuickBindCapture("stop");
        vm.CompleteQuickBindCapture("Ctrl+Space").Should().BeTrue();
        vm.StopHotkey.Should().NotBe("Ctrl+Space");

        // ESC cancels transport capture.
        vm.BeginQuickBindCapture("play");
        vm.CompleteQuickBindCapture("ESC").Should().BeTrue();
        vm.PlayHotkey.Should().Be("F4");
    }

    [Fact]
    public void NamedKeybinds_ListAppControlsAndMacros()
    {
        var vm = CreateViewModel();

        vm.AppKeybinds.Select(r => r.Name).Should().Contain(new[]
        {
            "Panic Stop", "Toggle Record", "Stop Playback / Recording", "Play Macro", "Pause Macro"
        });
        vm.AppKeybinds.First(r => r.Id == "panic").IsEditable.Should().BeTrue();
        vm.MacroKeybinds.Should().HaveCount(vm.MacroScripts.Count);
        vm.MacroKeybinds.Select(r => r.Name).Should().BeEquivalentTo(vm.MacroScripts.Select(s => s.Name));
    }

    [Fact]
    public void TryAssignBind_WhenConfirmed_UnbindsThePreviousOwner()
    {
        var vm = CreateViewModel();
        vm.ConfirmHotkeyConflictPrompter = prompt =>
        {
            prompt.PreviousOwner.Should().Be("Toggle Record");
            prompt.NewOwner.Should().Be("Stop Playback / Recording");
            prompt.SuggestedHotkey.Should().StartWith("Ctrl+");
            return HotkeyConflictChoice.UnbindPrevious;
        };

        vm.TryAssignBind("stop", vm.RecordHotkey).Should().BeTrue();
        vm.StopHotkey.Should().Be("F6");
        vm.RecordHotkey.Should().BeEmpty();
    }

    [Fact]
    public void TryAssignBind_WhenSuggestedChosen_KeepsPreviousOwner()
    {
        var vm = CreateViewModel();
        string originalRecord = vm.RecordHotkey;
        vm.ConfirmHotkeyConflictPrompter = prompt => HotkeyConflictChoice.UseSuggested;

        vm.TryAssignBind("stop", originalRecord).Should().BeTrue();
        vm.RecordHotkey.Should().Be(originalRecord);
        vm.StopHotkey.Should().StartWith("Ctrl+");
        vm.StopHotkey.Should().NotBe(originalRecord);
    }

    [Fact]
    public void AddClickStep_InsertsAfterTheSelectedAction()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.AddKeypressCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];

        vm.AddClickCommand.Execute(null);

        vm.Steps.Should().HaveCount(3);
        vm.Steps[1].Step.Should().BeOfType<ClickStep>();
        vm.SelectedStep.Should().Be(vm.Steps[1]);
    }

    [Fact]
    public void ScriptPersistence_ReloadedScript_MaterializesActionsIntoEditor()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"zenith_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var vault = new InMemorySecretVault();
            var router = new AIVisionRouter(vault);
            var gate = new DummyApprovalGate();
            var featureGate = new DummyFeatureGate();
            var usageTracker = new UsageTracker(new DummyUsageRepository(), featureGate);
            var audit = new DummyAuditLogger();

            var vm1 = new MacroEditorViewModel(
                new DummyCaptureService(),
                new DummyInputPlayer(),
                router,
                gate,
                featureGate,
                usageTracker,
                audit,
                seedDemoData: false,
                customMacrosDirectory: tempDir);

            // A brand new persistent library offers a single empty macro to record into.
            vm1.MacroScripts.Should().HaveCount(1);

            vm1.Steps.AddStep(new ClickStep(new System.Drawing.Point(410, 220)));
            vm1.Steps.AddStep(new DelayStep(TimeSpan.FromMilliseconds(75)));
            vm1.Steps.Count.Should().Be(2);
            vm1.SaveMacroCommand.Execute(null);

            var vm2 = new MacroEditorViewModel(
                new DummyCaptureService(),
                new DummyInputPlayer(),
                router,
                gate,
                featureGate,
                usageTracker,
                audit,
                seedDemoData: false,
                customMacrosDirectory: tempDir);

            vm2.MacroScripts.Should().HaveCount(1);
            vm2.SelectedScript!.ActionCount.Should().Be(2);
            // The regression: the script appeared in the list but the editor/playback store was empty.
            vm2.Steps.Count.Should().Be(2);
            vm2.RunCommand.CanExecute(null).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task StartRecording_CapturesRealCoordinates_NotHardcoded960()
    {
        var recorder = new TestInputRecorder();
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var gate = new DummyApprovalGate();
        var featureGate = new DummyFeatureGate();
        var usageTracker = new UsageTracker(new DummyUsageRepository(), featureGate);
        var audit = new DummyAuditLogger();

        var vm = new MacroEditorViewModel(
            new DummyCaptureService(),
            new DummyInputPlayer(),
            router,
            gate,
            featureGate,
            usageTracker,
            audit,
            seedDemoData: false,
            inputRecorder: recorder);

        // Start recording with 0 grace period for deterministic testing
        vm.RecordingGracePeriodMs = 0;
        await vm.StartRecordingAsync();
        vm.IsRecording.Should().BeTrue();
        recorder.IsRecording.Should().BeTrue();

        // Write a MouseDown event at custom coordinates (1420, 850)
        recorder.Writer.Should().NotBeNull();
        var ev = new InputEvent(InputEventType.MouseDown, Stopwatch.GetTimestamp() + 10000000, 1420, 850, (int)MacroSuite.Core.Models.MouseButton.Left, 0);
        recorder.Writer!.TryWrite(ev);

        // Give the background processor a moment to read and dispatch
        await Task.Delay(250);

        // Stop recording
        await vm.StopRecordingAsync();
        vm.IsRecording.Should().BeFalse();

        // Validate step was recorded with real coordinates
        vm.Steps.Should().NotBeEmpty();
        var step = vm.Steps.First();
        step.Step.Should().BeOfType<ClickStep>();
        var click = (ClickStep)step.Step;
        click.Position.X.Should().Be(1420);
        click.Position.Y.Should().Be(850);
        step.ParameterTokens.Should().Contain("x=1420 y=850");
    }

    [Fact]
    public async Task StopMacroAsync_WhenRecording_StopsRecording()
    {
        var recorder = new TestInputRecorder();
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var gate = new DummyApprovalGate();
        var featureGate = new DummyFeatureGate();
        var usageTracker = new UsageTracker(new DummyUsageRepository(), featureGate);
        var audit = new DummyAuditLogger();

        var vm = new MacroEditorViewModel(
            new DummyCaptureService(),
            new DummyInputPlayer(),
            router,
            gate,
            featureGate,
            usageTracker,
            audit,
            seedDemoData: false,
            inputRecorder: recorder);

        await vm.StartRecordingAsync();
        vm.IsRecording.Should().BeTrue();

        // Simulate F3 StopCommand execution
        await vm.StopMacroAsync();

        vm.IsRecording.Should().BeFalse();
        recorder.IsRecording.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessRecordedEvents_F3Key_StopsRecordingWithoutAddingStep()
    {
        var recorder = new TestInputRecorder();
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);
        var gate = new DummyApprovalGate();
        var featureGate = new DummyFeatureGate();
        var usageTracker = new UsageTracker(new DummyUsageRepository(), featureGate);
        var audit = new DummyAuditLogger();

        var vm = new MacroEditorViewModel(
            new DummyCaptureService(),
            new DummyInputPlayer(),
            router,
            gate,
            featureGate,
            usageTracker,
            audit,
            seedDemoData: false,
            inputRecorder: recorder);

        vm.RecordingGracePeriodMs = 0;
        vm.StopHotkey = "F3";
        await vm.StartRecordingAsync();
        vm.IsRecording.Should().BeTrue();

        // Send F3 keydown (VK_F3 = 0x72)
        var ev = new InputEvent(InputEventType.KeyDown, Stopwatch.GetTimestamp() + 10000000, 0, 0, 0x72, 0);
        recorder.Writer!.TryWrite(ev);

        // Wait for background consumer to process F3
        await Task.Delay(250);

        vm.IsRecording.Should().BeFalse();
        // Ensure F3 was not added as a macro step
        vm.Steps.Should().BeEmpty();
    }

    [Fact]
    public void ScriptSorting_OrderAndReverseToggle_PreservesDeterministicOrder()
    {
        var vm = CreateViewModel();

        // By default, SortMode is AlphaNumeric (Words A-Z -> Letter suffixes A-Z -> Numbers at bottom in order)
        vm.SortMode.Should().Be(MacroEditorViewModel.ScriptSortMode.AlphaNumeric);
        vm.SortOrderButtonText.Should().Be("⇅ A→Z / 1→9");

        // Words are sorted alphabetically at top
        var scriptNames = vm.MacroScripts.Select(s => s.Name).ToList();
        scriptNames[0].Should().Be("Auto Heal");
        scriptNames[1].Should().Be("Auto-Click Farm");
        scriptNames[2].Should().Be("Boss Rush Combo");
        scriptNames[3].Should().Be("Inventory Sweep");
        scriptNames[4].Should().Be("Quick Loot");

        // Letter suffix scripts are right above the numbered scripts, in A then B order
        scriptNames[5].Should().Be("Skill Rotation A");
        scriptNames[6].Should().Be("Skill Rotation B");

        // Toggle to Newest First
        vm.ToggleSortOrderCommand.Execute(null);
        vm.SortMode.Should().Be(MacroEditorViewModel.ScriptSortMode.NewestFirst);
        vm.SortOrderButtonText.Should().Be("⇅ Newest First");
        vm.IsSortNewestFirst.Should().BeTrue();

        // Toggle to Oldest First
        vm.ToggleSortOrderCommand.Execute(null);
        vm.SortMode.Should().Be(MacroEditorViewModel.ScriptSortMode.OldestFirst);
        vm.SortOrderButtonText.Should().Be("⇅ Oldest First");
        vm.IsSortNewestFirst.Should().BeFalse();

        // Toggle back to AlphaNumeric
        vm.ToggleSortOrderCommand.Execute(null);
        vm.SortMode.Should().Be(MacroEditorViewModel.ScriptSortMode.AlphaNumeric);
        vm.SortOrderButtonText.Should().Be("⇅ A→Z / 1→9");

        // Add a brand new macro - ends in a number ("Script #8"), so it must be at the very bottom (in numeric order)
        vm.NewMacroCommand.Execute(null);
        vm.MacroScripts.Last().Name.Should().Be("Script #8");
        vm.SelectedScript.Should().Be(vm.MacroScripts.Last());
    }

    [Fact]
    public void ScriptSorting_WordsAboveLettersAboveNumbers_MatchesUserHierarchy()
    {
        var vm = CreateViewModel();

        // Add custom scripts with various name formats
        var scriptZebra = new MacroScriptItemViewModel("Zebra Farm", 0, "F10", true, "Never");
        var scriptLetterC = new MacroScriptItemViewModel("Skill Rotation C", 0, "F11", true, "Never");
        var scriptNum2 = new MacroScriptItemViewModel("Script #2", 0, "F7", true, "Never");
        var scriptNum10 = new MacroScriptItemViewModel("Script #10", 0, "F8", true, "Never");

        vm.MacroScripts.Add(scriptZebra);
        vm.MacroScripts.Add(scriptLetterC);
        vm.MacroScripts.Add(scriptNum2);
        vm.MacroScripts.Add(scriptNum10);

        vm.ApplyScriptSorting();

        var names = vm.MacroScripts.Select(s => s.Name).ToList();

        // Group 0: Words (A-Z)
        names.Should().ContainInOrder("Auto Heal", "Auto-Click Farm", "Boss Rush Combo", "Inventory Sweep", "Quick Loot", "Zebra Farm");

        // Group 1: Letter suffixes right above numbered scripts (A -> B -> C)
        names.Should().ContainInOrder("Skill Rotation A", "Skill Rotation B", "Skill Rotation C");

        // Group 2: Numbers at bottom in numerical order (2 -> 10)
        names.Should().ContainInOrder("Script #2", "Script #10");

        // Category 0 must be before Category 1, which must be before Category 2
        names.IndexOf("Zebra Farm").Should().BeLessThan(names.IndexOf("Skill Rotation A"));
        names.IndexOf("Skill Rotation C").Should().BeLessThan(names.IndexOf("Script #2"));
        names.IndexOf("Script #2").Should().BeLessThan(names.IndexOf("Script #10"));
    }

    [Fact]
    public void ToggleScriptItemActiveCommand_TogglesActiveState()
    {
        var vm = CreateViewModel();
        var target = vm.MacroScripts[0];
        bool initial = target.IsActive;

        vm.ToggleScriptItemActiveCommand.Execute(target);
        target.IsActive.Should().Be(!initial);

        vm.ToggleScriptItemActiveCommand.Execute(target);
        target.IsActive.Should().Be(initial);
    }

    [Fact]
    public void KeyboardNavigation_SelectNextAndPrevious_NavigatesMacroList()
    {
        var vm = CreateViewModel();
        vm.MacroScripts.Count.Should().BeGreaterThan(2);

        // Select first macro
        vm.SelectedScript = vm.MacroScripts[0];
        vm.SelectedScript.Should().Be(vm.MacroScripts[0]);

        // Navigate down
        vm.SelectNextMacroCommand.Execute(null);
        vm.SelectedScript.Should().Be(vm.MacroScripts[1]);

        // Navigate down again
        vm.SelectNextMacro();
        vm.SelectedScript.Should().Be(vm.MacroScripts[2]);

        // Navigate up
        vm.SelectPreviousMacroCommand.Execute(null);
        vm.SelectedScript.Should().Be(vm.MacroScripts[1]);

        // Navigate up back to index 0
        vm.SelectPreviousMacro();
        vm.SelectedScript.Should().Be(vm.MacroScripts[0]);

        // Clamping at top boundary: should stay at 0
        vm.SelectPreviousMacro();
        vm.SelectedScript.Should().Be(vm.MacroScripts[0]);
    }

    [Fact]
    public void SelectScriptCommand_WithParameter_SelectsSpecifiedScript()
    {
        var vm = CreateViewModel();
        vm.MacroScripts.Count.Should().BeGreaterThan(1);

        var target = vm.MacroScripts[1];
        vm.SelectScriptCommand.Execute(target);

        vm.SelectedScript.Should().Be(target);
        target.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void DeleteSelectedMacroCommand_DeletesCurrentlySelectedMacro()
    {
        var vm = CreateViewModel();
        int initialCount = vm.MacroScripts.Count;
        var toDelete = vm.MacroScripts[0];
        vm.SelectedScript = toDelete;

        vm.DeleteSelectedMacroCommand.Execute(null);

        vm.MacroScripts.Should().NotContain(toDelete);
        vm.MacroScripts.Count.Should().Be(initialCount - 1);
    }

    [Fact]
    public void ColorWait_LoadApply_PreservesModePaletteStrideAndReliability()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        var wait = new WaitUntilStep(new ColorCondition(
            new Rectangle(-10, 20, 40, 30),
            null,
            255,
            10,
            5,
            18,
            0.08,
            ColorComparisonMode.Hsv,
            2,
            [new PixelColor(0, 255, 0)],
            80,
            3,
            2,
            120,
            4500,
            ConditionFailurePolicy.Skip))
        {
            Timeout = TimeSpan.FromMilliseconds(4500),
            PollInterval = TimeSpan.FromMilliseconds(80),
            RequiredConfirmations = 3
        };
        vm.ReplaceStepAt(0, wait, reloadPanel: true);

        vm.ActionEditor.DetectionModeIndex.Should().Be((int)ColorComparisonMode.Hsv);
        vm.ActionEditor.SampleStride.Should().Be(2);
        vm.ActionEditor.PaletteText.Should().Be("#00FF00");
        vm.ActionEditor.RequiredFalseConfirmations.Should().Be(2);
        vm.ActionEditor.StabilityDurationMs.Should().Be(120);
        vm.ActionEditor.FailurePolicyIndex.Should().Be((int)ConditionFailurePolicy.Skip);

        vm.ActionEditor.Apply();

        vm.Steps.Should().HaveCount(1);
        var loaded = vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Subject;
        var color = loaded.Condition.Should().BeOfType<ColorCondition>().Subject;
        color.ComparisonMode.Should().Be(ColorComparisonMode.Hsv);
        color.SampleStride.Should().Be(2);
        color.AdditionalTargets.Should().Equal(new PixelColor(0, 255, 0));
        color.RequiredFalseConfirmations.Should().Be(2);
        color.RequiredConfirmations.Should().Be(3);
        color.PollIntervalMs.Should().Be(80);
        color.TimeoutMs.Should().Be(4500);
        color.StabilityDurationMs.Should().Be(120);
        color.FailurePolicy.Should().Be(ConditionFailurePolicy.Skip);
        color.Region.Should().Be(new Rectangle(-10, 20, 40, 30));
        vm.Steps[0].ParameterTokens.Should().Contain("Hsv");
        vm.Steps[0].ParameterTokens.Should().Contain("RGB(255,10,5)");
    }

    [Fact]
    public void VisualPresence_LoadApply_DoesNotDropConditionFields()
    {
        var vm = CreateViewModel();
        vm.RegionPrompter = () => new Rectangle(10, 20, 40, 30);
        vm.AddIfCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        var iff = (IfThenElseStep)vm.GetStepAt(0)!;
        vm.ReplaceStepAt(0, MacroActionFactory.WithIfCondition(iff, new VisualPresenceCondition
        {
            Kind = VisualPresenceKind.ColorAbsent,
            X = 11,
            Y = 22,
            Width = 33,
            Height = 44,
            R = 1,
            G = 2,
            B = 3,
            ComparisonMode = ColorComparisonMode.Cie76,
            SampleStride = 3,
            AdditionalTargets = [new PixelColor(9, 8, 7)],
            RequiredPixelFraction = 0.2,
            PollIntervalMs = 90,
            RequiredConfirmations = 4,
            RequiredFalseConfirmations = 2,
            StabilityDurationMs = 75,
            TimeoutMs = 8000,
            FailurePolicy = ConditionFailurePolicy.Retry,
            SearchMode = "Nearby"
        }), reloadPanel: true);

        vm.ActionEditor.Apply();

        var saved = vm.GetStepAt(0).Should().BeOfType<IfThenElseStep>().Subject;
        var presence = saved.Condition.Should().BeOfType<VisualPresenceCondition>().Subject;
        presence.Kind.Should().Be(VisualPresenceKind.ColorAbsent);
        presence.ComparisonMode.Should().Be(ColorComparisonMode.Cie76);
        presence.SampleStride.Should().Be(3);
        presence.PollIntervalMs.Should().Be(90);
        presence.RequiredConfirmations.Should().Be(4);
        presence.RequiredFalseConfirmations.Should().Be(2);
        presence.StabilityDurationMs.Should().Be(75);
        presence.TimeoutMs.Should().Be(8000);
        presence.FailurePolicy.Should().Be(ConditionFailurePolicy.Retry);
        presence.SearchMode.Should().Be("Nearby");
        presence.AdditionalTargets.Should().ContainSingle(c => c.R == 9 && c.G == 8 && c.B == 7);
        vm.Steps[0].ParameterTokens.Should().Contain("absent");
    }

    [Fact]
    public void SwitchingScripts_FlushesUnappliedEditorDraft()
    {
        var vm = CreateViewModel();
        vm.MacroScripts.Count.Should().BeGreaterThan(1);
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.DurationMs = 250;

        var first = vm.SelectedScript!;
        var other = vm.MacroScripts.First(s => s != first);
        vm.SelectedScript = other;

        first.Actions.Should().ContainSingle()
            .Which.Should().BeOfType<DelayStep>()
            .Which.Duration.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void WhileLoop_LoadApply_PreservesConditionPollSeparateFromLoopCheck()
    {
        var vm = CreateViewModel();
        vm.RegionPrompter = () => new Rectangle(10, 20, 40, 30);
        vm.AddWhileCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        var loop = (LoopStep)vm.GetStepAt(0)!;
        vm.ReplaceStepAt(0, MacroActionFactory.WithLoopBody(
            loop,
            loop.BodySteps,
            new VisualPresenceCondition
            {
                Kind = VisualPresenceKind.ColorPresent,
                X = 5,
                Y = 6,
                Width = 7,
                Height = 8,
                R = 9,
                G = 10,
                B = 11,
                PollIntervalMs = 90,
                RequiredConfirmations = 3,
                SearchMode = "Screen",
                ComparisonMode = ColorComparisonMode.Hsv
            },
            maxDurationMs: 12_000,
            checkIntervalMs: 40), reloadPanel: true);

        vm.ActionEditor.PollIntervalMs.Should().Be(90);
        vm.ActionEditor.LoopCheckIntervalMs.Should().Be(40);
        vm.ActionEditor.SearchModeIndex.Should().Be(2);
        vm.ActionEditor.RequiredConfirmations.Should().Be(3);

        vm.ActionEditor.Apply();

        var saved = vm.GetStepAt(0).Should().BeOfType<LoopStep>().Subject;
        saved.CheckIntervalMs.Should().Be(40);
        saved.MaxDurationMs.Should().Be(12_000);
        var presence = saved.WhileCondition.Should().BeOfType<VisualPresenceCondition>().Subject;
        presence.PollIntervalMs.Should().Be(90);
        presence.RequiredConfirmations.Should().Be(3);
        presence.SearchMode.Should().Be("Screen");
        presence.ComparisonMode.Should().Be(ColorComparisonMode.Hsv);
        vm.Steps[0].ParameterTokens.Should().Contain("present");
    }

    [Fact]
    public void AddIf_CancelRegion_DoesNotInsertStep()
    {
        var vm = CreateViewModel();
        vm.RegionPrompter = () => null;
        int before = vm.Steps.Count;

        vm.AddIfCommand.Execute(null);

        vm.Steps.Should().HaveCount(before);
    }

    [Fact]
    public void AddColorCondition_CancelPick_DoesNotInsertStep()
    {
        var vm = CreateViewModel();
        vm.ColorSamplePrompter = () => null;
        vm.ColorPrompter = () => null;

        vm.AddColorConditionCommand.Execute(null);

        vm.Steps.Should().BeEmpty();
    }

    [Fact]
    public void AddWaitWhile_CreatesInvertedColorWait()
    {
        var vm = CreateViewModel();
        vm.ColorPrompter = () => Color.FromArgb(10, 20, 30);

        vm.AddWaitWhileCommand.Execute(null);

        var wait = vm.Steps[0].Step.Should().BeOfType<WaitUntilStep>().Subject;
        wait.WaitWhile.Should().BeTrue();
        wait.Label.Should().StartWith("Wait while:");
        vm.Steps[0].ActionKeyword.Should().Be("WAIT_WHILE_COLOR");
    }

    [Fact]
    public void AddRepeatUntilGone_UsesColorAbsentUntilPredicate()
    {
        var vm = CreateViewModel();
        vm.RegionPrompter = () => new Rectangle(10, 20, 40, 30);

        vm.AddRepeatUntilGoneCommand.Execute(null);

        var loop = vm.Steps[0].Step.Should().BeOfType<LoopStep>().Subject;
        loop.RepeatUntil.Should().BeTrue();
        loop.WhileCondition.Should().BeOfType<VisualPresenceCondition>()
            .Which.Kind.Should().Be(VisualPresenceKind.ColorAbsent);
        loop.Label.Should().Contain("gone");
    }

    [Fact]
    public void ChangeActionKind_RepeatUntilGone_ReplacesWithoutAppending()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];

        vm.ChangeSelectedActionKind(EditableActionKind.RepeatUntilGone);

        vm.Steps.Should().HaveCount(1);
        var loop = vm.GetStepAt(0).Should().BeOfType<LoopStep>().Subject;
        loop.RepeatUntil.Should().BeTrue();
        loop.WhileCondition.Should().BeOfType<VisualPresenceCondition>()
            .Which.Kind.Should().Be(VisualPresenceKind.ColorAbsent);
        MacroActionFactory.KindOf(loop).Should().Be(EditableActionKind.RepeatUntilGone);
    }

    [Fact]
    public void InsertActionKind_RepeatUntilGone_InsertsAfterSelection()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];

        vm.InsertDefaultAction(EditableActionKind.RepeatUntilGone);

        vm.Steps.Should().HaveCount(2);
        vm.GetStepAt(0).Should().BeOfType<DelayStep>();
        var loop = vm.GetStepAt(1).Should().BeOfType<LoopStep>().Subject;
        loop.WhileCondition.Should().BeOfType<VisualPresenceCondition>()
            .Which.Kind.Should().Be(VisualPresenceKind.ColorAbsent);
    }

    [Fact]
    public void Apply_AiVision_PersistsExpectedWindowAndMinConfidence()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        vm.ChangeSelectedActionKind(EditableActionKind.AiVision);

        vm.ActionEditor.Prompt = "Click play";
        vm.ActionEditor.ExpectedWindow = "Roblox";
        vm.ActionEditor.MinConfidence = "0.8";
        vm.ActionEditor.TimeoutMs = 12_000;
        vm.ActionEditor.Apply();

        vm.Steps.Should().HaveCount(1);
        var ai = vm.GetStepAt(0).Should().BeOfType<AIStep>().Subject;
        ai.Prompt.Should().Be("Click play");
        ai.ExpectedWindow.Should().Be("Roblox");
        ai.MinConfidence.Should().BeApproximately(0.8, 0.0001);
        ai.Timeout.Should().Be(TimeSpan.FromMilliseconds(12_000));
    }

    [Fact]
    public void ImageWait_LoadApply_PreservesTemplate()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        var wait = new WaitUntilStep(new ImageCondition("hero.png", 0.8))
        {
            Timeout = TimeSpan.FromSeconds(15),
            PollInterval = TimeSpan.FromMilliseconds(90),
            ThrowOnTimeout = true
        };
        vm.ReplaceStepAt(0, wait, reloadPanel: true);

        MacroActionFactory.KindOf(vm.GetStepAt(0)!).Should().Be(EditableActionKind.ImageWait);
        vm.ActionEditor.TimeoutMs = 12_000;
        vm.ActionEditor.Apply();

        var saved = vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Subject;
        saved.Condition.Should().BeOfType<ImageCondition>().Which.TemplatePath.Should().Be("hero.png");
        saved.Timeout.Should().Be(TimeSpan.FromMilliseconds(12_000));
        saved.Condition.Should().NotBeOfType<ColorCondition>();
    }

    [Fact]
    public void Apply_KeyPressToImageWait_PromptsForTemplateAndConverts()
    {
        var vm = CreateViewModel();
        vm.AddKeypressCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        vm.ImagePrompter = () => new ImageTemplateSelection("c:\\test\\from_key.png", 0.8, false, null);

        vm.ActionEditor.KindIndex = (int)EditableActionKind.ImageWait;
        vm.ActionEditor.CommitKindChange();

        var wait = vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Subject;
        wait.Condition.Should().BeOfType<ImageCondition>().Which.TemplatePath.Should().Be("c:\\test\\from_key.png");
        vm.Steps[0].ActionKeyword.Should().Be("WAIT_IMAGE");
    }

    [Fact]
    public void Apply_KeyPressToImageWait_CancelKeepsKeyPress()
    {
        var vm = CreateViewModel();
        vm.AddKeypressCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        vm.ImagePrompter = () => null;

        vm.ActionEditor.KindIndex = (int)EditableActionKind.ImageWait;
        vm.ActionEditor.Apply().Should().BeFalse();

        vm.GetStepAt(0).Should().BeOfType<KeyPressStep>();
        vm.ActionEditor.KindIndex.Should().Be((int)EditableActionKind.KeyPress);
    }

    [Fact]
    public void ChangeSelectedActionKind_ImageWait_PromptsForTemplate()
    {
        var vm = CreateViewModel();
        vm.AddKeypressCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        vm.ImagePrompter = () => new ImageTemplateSelection("c:\\test\\swapped.png", 0.75, true, new Point(1, 2));

        vm.ChangeSelectedActionKind(EditableActionKind.ImageWait);

        var wait = vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Subject;
        wait.Condition.Should().BeOfType<ImageCondition>().Which.TemplatePath.Should().Be("c:\\test\\swapped.png");
        vm.Steps.Should().HaveCount(1);
    }

    [Fact]
    public void ImageWait_WaitWhile_TogglesKeyword()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("hero.png")) { Timeout = TimeSpan.FromSeconds(15) }, reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];

        vm.ActionEditor.WaitWhile = true;

        var wait = vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Subject;
        wait.WaitWhile.Should().BeTrue();
        vm.Steps[0].ActionKeyword.Should().Be("WAIT_WHILE_IMAGE");
    }

    [Fact]
    public void CaptureImageTemplate_ReplacesExistingTemplate()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("hero.png")), reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];
        vm.ImagePrompter = () => new ImageTemplateSelection("c:\\test\\retake.png", 0.9, false, null);

        vm.ActionEditor.CaptureImageTemplateCommand.Execute(null);

        var wait = vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Subject;
        wait.Condition.Should().BeOfType<ImageCondition>().Which.TemplatePath.Should().Be("c:\\test\\retake.png");
        vm.ActionEditor.ImageTemplateSummary.Should().Contain("retake.png");
    }

    [Fact]
    public void ImageWait_Duplicate_PreservesImageCondition()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("hero.png")), reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];
        vm.DuplicateStepCommand.Execute(null);

        vm.GetStepAt(1).Should().BeOfType<WaitUntilStep>()
            .Which.Condition.Should().BeOfType<ImageCondition>()
            .Which.TemplatePath.Should().Be("hero.png");
    }

    [Fact]
    public void ImageWait_ThenColorWait_DoesNotKeepTemplate()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("hero.png")), reloadPanel: true);
        vm.ChangeSelectedActionKind(EditableActionKind.ColorWait);

        var wait = vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Subject;
        wait.Condition.Should().BeOfType<ColorCondition>();
    }

    [Fact]
    public void LoadFrom_ImageWaitAfterRegionWait_ResetsWidthHeight()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        var region = new WaitUntilStep(new ColorCondition(new Rectangle(1, 2, 80, 40), 1, 2, 3));
        vm.ReplaceStepAt(0, region, reloadPanel: true);
        vm.ActionEditor.Width.Should().Be(80);

        vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("hero.png")), reloadPanel: true);
        vm.ActionEditor.Width.Should().Be(1);
        vm.ActionEditor.Height.Should().Be(1);
    }

    [Fact]
    public void SelectingAnotherRow_CommitsImageWaitTimeout()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("hero.png")) { Timeout = TimeSpan.FromSeconds(15) }, reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.TimeoutMs = 9000;
        vm.SelectedStep = vm.Steps[1];

        var wait = vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Subject;
        wait.Timeout.Should().Be(TimeSpan.FromMilliseconds(9000));
        wait.Condition.Should().BeOfType<ImageCondition>();
    }

    [Fact]
    public void InvalidPaletteEdit_BlocksSelectionChange()
    {
        var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new WaitUntilStep(new ColorCondition(new Point(1, 2), 9, 8, 7)), reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.PaletteText = "not-a-color";
        var stay = vm.Steps[0];
        vm.SelectedStep = vm.Steps[1];
        vm.SelectedStep.Should().BeSameAs(stay);
        vm.ActionEditor.DraftError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RepeatingImageWait_EditAndJsonRoundTrip_PreservesTriggerSettings()
    {
        using var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        var original = new WaitUntilStep(new ImageCondition("hero.png", 0.87))
        {
            RepeatWhileVisible = true,
            RepeatDelayMs = 750,
            RequiredConfirmations = 3,
            StabilityDurationMs = 120,
            ThrowOnTimeout = true
        };
        vm.ReplaceStepAt(0, original, reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];

        vm.ActionEditor.RepeatImageWhileVisible.Should().BeTrue();
        vm.ActionEditor.ImageRepeatDelayMs.Should().Be(750);
        vm.ActionEditor.ImageRepeatDelayMs = 1200;
        vm.ActionEditor.PollIntervalMs = 110;
        vm.ActionEditor.Apply().Should().BeTrue();

        var serializer = new MacroJsonSerializer();
        var loaded = serializer.Deserialize(serializer.Serialize(new MacroModel { Steps = [vm.GetStepAt(0)!] }));
        var saved = loaded.Steps.Single().Should().BeOfType<WaitUntilStep>().Subject;
        saved.StepId.Should().Be(original.StepId);
        saved.RepeatWhileVisible.Should().BeTrue();
        saved.RepeatDelayMs.Should().Be(1200);
        saved.WaitWhile.Should().BeFalse();
        saved.PollInterval.Should().Be(TimeSpan.FromMilliseconds(110));
        saved.RequiredConfirmations.Should().Be(3);
        saved.StabilityDurationMs.Should().Be(120);
        saved.Condition.Should().BeOfType<ImageCondition>().Which.TemplatePath.Should().Be("hero.png");
        vm.Steps[0].ActionKeyword.Should().Be("REPEAT_IMAGE");
    }

    [Theory]
    [InlineData(-10, 50)]
    [InlineData(100_000, 60_000)]
    public void RepeatingImageWait_RepeatDelay_IsBounded(int requestedDelay, int expectedDelay)
    {
        using var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("hero.png")), reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];

        vm.ActionEditor.RepeatImageWhileVisible = true;
        vm.ActionEditor.ImageRepeatDelayMs = requestedDelay;
        vm.ActionEditor.Apply().Should().BeTrue();

        vm.ActionEditor.ImageRepeatDelayMs.Should().Be(expectedDelay);
        vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Which.RepeatDelayMs.Should().Be(expectedDelay);
    }

    [Fact]
    public void RepeatingImageWait_AndWaitWhile_AreMutuallyExclusive()
    {
        using var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("hero.png")) { WaitWhile = true }, reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];

        vm.ActionEditor.RepeatImageWhileVisible = true;

        var repeating = vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Subject;
        repeating.RepeatWhileVisible.Should().BeTrue();
        repeating.WaitWhile.Should().BeFalse();
        vm.ActionEditor.WaitWhile.Should().BeFalse();

        vm.ActionEditor.WaitWhile = true;

        var oneShot = vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Subject;
        oneShot.RepeatWhileVisible.Should().BeFalse();
        oneShot.WaitWhile.Should().BeTrue();
        vm.ActionEditor.RepeatImageWhileVisible.Should().BeFalse();
        vm.Steps[0].ActionKeyword.Should().Be("WAIT_WHILE_IMAGE");
    }

    [Fact]
    public void RepeatingImageWait_DuplicateAndReplaceTemplate_PreserveRepeatSettings()
    {
        using var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        var original = new WaitUntilStep(new ImageCondition("hero.png"))
        {
            RepeatWhileVisible = true,
            RepeatDelayMs = 950
        };
        vm.ReplaceStepAt(0, original, reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];
        vm.DuplicateStepCommand.Execute(null);

        var clone = vm.GetStepAt(1).Should().BeOfType<WaitUntilStep>().Subject;
        clone.StepId.Should().NotBe(original.StepId);
        clone.RepeatWhileVisible.Should().BeTrue();
        clone.RepeatDelayMs.Should().Be(950);

        vm.ImagePrompter = () => new ImageTemplateSelection("retake.png", 0.9, false, null);
        vm.ActionEditor.CaptureImageTemplateCommand.Execute(null);

        var replaced = vm.GetStepAt(1).Should().BeOfType<WaitUntilStep>().Subject;
        replaced.RepeatWhileVisible.Should().BeTrue();
        replaced.RepeatDelayMs.Should().Be(950);
        replaced.Condition.Should().BeOfType<ImageCondition>().Which.TemplatePath.Should().Be("retake.png");
        vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>()
            .Which.Condition.Should().BeOfType<ImageCondition>().Which.TemplatePath.Should().Be("hero.png");
    }

    [Fact]
    public void SelectingAnotherImageWait_CommitsRepeatDelayAndLoadsIndependentSettings()
    {
        using var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("first.png"))
        {
            RepeatWhileVisible = true,
            RepeatDelayMs = 700
        }, reloadPanel: false);
        vm.ReplaceStepAt(1, new WaitUntilStep(new ImageCondition("second.png")), reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.ImageRepeatDelayMs = 1800;

        vm.SelectedStep = vm.Steps[1];

        vm.GetStepAt(0).Should().BeOfType<WaitUntilStep>().Which.RepeatDelayMs.Should().Be(1800);
        vm.ActionEditor.RepeatImageWhileVisible.Should().BeFalse();
        vm.ActionEditor.ImageRepeatDelayMs.Should().Be(500);
        vm.GetStepAt(1).Should().BeOfType<WaitUntilStep>().Which.RepeatWhileVisible.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureImageTemplate_ForControlFlow_PreservesKindChildrenAndPendingCondition(bool isWhile)
    {
        using var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        var thenOrBody = new KeyPressStep(65);
        var otherwise = new DelayStep(TimeSpan.FromMilliseconds(200));
        var condition = new VisualPresenceCondition { Kind = VisualPresenceKind.ColorPresent };
        MacroStep source = isWhile
            ? new LoopStep(20, [thenOrBody], condition) { MaxDurationMs = 12_000, CheckIntervalMs = 125 }
            : new IfThenElseStep(condition, [thenOrBody], [otherwise]);
        vm.ReplaceStepAt(0, source, reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.ConditionKindIndex = (int)VisualPresenceKind.ImageAbsent;
        vm.ImagePrompter = () => new ImageTemplateSelection("absent-target.png", 0.92, true, new Point(10, 20));

        vm.ActionEditor.CaptureImageTemplateCommand.Execute(null);

        vm.Steps.Should().ContainSingle();
        vm.GetStepAt(0)!.StepId.Should().Be(source.StepId);
        VisualPresenceCondition captured;
        if (isWhile)
        {
            var loop = vm.GetStepAt(0).Should().BeOfType<LoopStep>().Subject;
            loop.BodySteps.Should().ContainSingle().Which.StepId.Should().Be(thenOrBody.StepId);
            loop.MaxDurationMs.Should().Be(12_000);
            loop.CheckIntervalMs.Should().Be(125);
            captured = loop.WhileCondition.Should().BeOfType<VisualPresenceCondition>().Subject;
        }
        else
        {
            var branch = vm.GetStepAt(0).Should().BeOfType<IfThenElseStep>().Subject;
            branch.ThenSteps.Should().ContainSingle().Which.StepId.Should().Be(thenOrBody.StepId);
            branch.ElseSteps.Should().ContainSingle().Which.StepId.Should().Be(otherwise.StepId);
            captured = branch.Condition.Should().BeOfType<VisualPresenceCondition>().Subject;
        }
        captured.Kind.Should().Be(VisualPresenceKind.ImageAbsent);
        captured.TemplatePath.Should().Be("absent-target.png");
        captured.MinConfidence.Should().Be(0.92);
        vm.ActionEditor.PresenceTemplateSummary.Should().Contain("absent-target.png");
    }

    [Fact]
    public void CaptureImageTemplate_ForIf_CancelKeepsExistingConditionAndChildren()
    {
        using var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        var child = new KeyPressStep(65);
        var condition = new VisualPresenceCondition { Kind = VisualPresenceKind.ImagePresent, TemplatePath = "original.png" };
        vm.ReplaceStepAt(0, new IfThenElseStep(condition, [child]), reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];
        vm.ImagePrompter = () => null;

        vm.ActionEditor.CaptureImageTemplateCommand.Execute(null);

        vm.Steps.Should().ContainSingle();
        var branch = vm.GetStepAt(0).Should().BeOfType<IfThenElseStep>().Subject;
        branch.Condition.Should().BeOfType<VisualPresenceCondition>().Which.TemplatePath.Should().Be("original.png");
        branch.ThenSteps.Should().ContainSingle().Which.StepId.Should().Be(child.StepId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageControlFlow_WithoutTemplate_RejectsApplyAndKeepsOriginalCondition(bool isWhile)
    {
        using var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        var condition = new VisualPresenceCondition { Kind = VisualPresenceKind.ColorPresent };
        MacroStep source = isWhile ? new LoopStep(20, [], condition) : new IfThenElseStep(condition, []);
        vm.ReplaceStepAt(0, source, reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.ConditionKindIndex = (int)VisualPresenceKind.ImagePresent;

        vm.ActionEditor.Apply().Should().BeFalse();

        vm.ActionEditor.DraftError.Should().NotBeNullOrWhiteSpace();
        ICondition saved = isWhile
            ? vm.GetStepAt(0).Should().BeOfType<LoopStep>().Subject.WhileCondition!
            : vm.GetStepAt(0).Should().BeOfType<IfThenElseStep>().Subject.Condition;
        saved.Should().BeOfType<VisualPresenceCondition>().Which.Kind.Should().Be(VisualPresenceKind.ColorPresent);
    }

    [Fact]
    public void AppendToWhileBody_CommitsPendingConditionAndDurationBeforeAddingChild()
    {
        using var vm = CreateViewModel();
        vm.RegionPrompter = () => new Rectangle(1, 2, 30, 40);
        vm.AddWhileCommand.Execute(null);
        vm.ActionEditor.ConditionKindIndex = (int)VisualPresenceKind.ColorAbsent;
        vm.ActionEditor.R = 42;
        vm.ActionEditor.TimeoutMs = 45_000;
        vm.ActionEditor.LoopCheckIntervalMs = 140;
        vm.ActionEditor.MinBodyDelayMs = 350;
        var child = new KeyPressStep(65);

        vm.AppendToSelectedBlock("body", child);

        var loop = vm.GetStepAt(0).Should().BeOfType<LoopStep>().Subject;
        loop.MaxDurationMs.Should().Be(45_000);
        loop.CheckIntervalMs.Should().Be(140);
        loop.MinBodyDelayMs.Should().Be(350);
        loop.BodySteps.Should().ContainSingle().Which.StepId.Should().Be(child.StepId);
        var condition = loop.WhileCondition.Should().BeOfType<VisualPresenceCondition>().Subject;
        condition.Kind.Should().Be(VisualPresenceKind.ColorAbsent);
        condition.R.Should().Be(42);
    }

    [Fact]
    public void AppendToIfBranch_WithInvalidImageDraft_DoesNotLoseDraftOrAddChild()
    {
        using var vm = CreateViewModel();
        vm.RegionPrompter = () => new Rectangle(1, 2, 30, 40);
        vm.AddIfCommand.Execute(null);
        vm.ActionEditor.ConditionKindIndex = (int)VisualPresenceKind.ImagePresent;

        vm.AppendToSelectedBlock("then", new KeyPressStep(65));

        vm.GetStepAt(0).Should().BeOfType<IfThenElseStep>().Which.ThenSteps.Should().BeEmpty();
        vm.ActionEditor.ConditionKindIndex.Should().Be((int)VisualPresenceKind.ImagePresent);
        vm.ActionEditor.DraftError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void WhileBodyKeyPicker_KeepsMatchingDownAndUpAcrossApplyAndAppend()
    {
        using var vm = CreateViewModel();
        vm.RegionPrompter = () => new Rectangle(1, 2, 30, 40);
        vm.AddWhileCommand.Execute(null);
        vm.ActionEditor.ArmCaptureKeyCommand.Execute(null);
        vm.ActionEditor.TryCaptureKey(87).Should().BeTrue();
        vm.ActionEditor.Apply().Should().BeTrue();

        vm.ActionEditor.AddBodyKeyDownCommand.Execute(null);
        vm.ActionEditor.AddBodyWaitCommand.Execute(null);
        vm.ActionEditor.AddBodyKeyUpCommand.Execute(null);

        var loop = vm.GetStepAt(0).Should().BeOfType<LoopStep>().Subject;
        loop.BodySteps.Should().HaveCount(3);
        var keyDown = loop.BodySteps[0].Should().BeOfType<KeyPressStep>().Subject;
        var keyUp = loop.BodySteps[2].Should().BeOfType<KeyPressStep>().Subject;
        keyDown.KeyCode.Should().Be(87);
        keyDown.Stroke.Should().Be("Down");
        loop.BodySteps[1].Should().BeOfType<DelayStep>();
        keyUp.KeyCode.Should().Be(87);
        keyUp.Stroke.Should().Be("Up");
        vm.ActionEditor.KeyName.Should().Be("W");

        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[1];
        vm.SelectedStep = vm.Steps[0];

        vm.ActionEditor.KeyName.Should().Be("ENTER");
        vm.ActionEditor.AddBodyKeyPressCommand.Execute(null);
        ((LoopStep)vm.GetStepAt(0)!).BodySteps[^1].Should().BeOfType<KeyPressStep>().Which.KeyCode.Should().Be(13);
    }

    [Theory]
    [InlineData(false, VisualPresenceKind.ImagePresent, EditableActionKind.RepeatUntil)]
    [InlineData(true, VisualPresenceKind.ImageAbsent, EditableActionKind.RepeatUntilGone)]
    public void ImageRepeatUntil_CaptureApplyAndDuplicate_PreserveImagePredicateAndBody(
        bool untilGone, VisualPresenceKind expectedCondition, EditableActionKind expectedKind)
    {
        using var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        var child = new KeyPressStep(87) { Stroke = "Press" };
        var original = new LoopStep(20, [child], new VisualPresenceCondition
        {
            Kind = untilGone ? VisualPresenceKind.ColorAbsent : VisualPresenceKind.ColorPresent
        })
        {
            RepeatUntil = true,
            MaxDurationMs = 18_000,
            CheckIntervalMs = 120,
            MinBodyDelayMs = 250
        };
        vm.ReplaceStepAt(0, original, reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.ConditionKindIndex = (int)expectedCondition;
        vm.ImagePrompter = () => new ImageTemplateSelection("repeat-target.png", 0.91, false, null);

        vm.ActionEditor.CaptureImageTemplateCommand.Execute(null);
        vm.ActionEditor.Apply().Should().BeTrue();

        var saved = vm.GetStepAt(0).Should().BeOfType<LoopStep>().Subject;
        MacroActionFactory.KindOf(saved).Should().Be(expectedKind);
        saved.RepeatUntil.Should().BeTrue();
        var predicate = saved.WhileCondition.Should().BeOfType<VisualPresenceCondition>().Subject;
        predicate.Kind.Should().Be(expectedCondition);
        predicate.TemplatePath.Should().Be("repeat-target.png");
        predicate.MinConfidence.Should().Be(0.91);
        saved.BodySteps.Should().ContainSingle().Which.StepId.Should().Be(child.StepId);

        vm.DuplicateStepCommand.Execute(null);

        var clone = vm.GetStepAt(1).Should().BeOfType<LoopStep>().Subject;
        clone.StepId.Should().NotBe(saved.StepId);
        MacroActionFactory.KindOf(clone).Should().Be(expectedKind);
        clone.RepeatUntil.Should().BeTrue();
        clone.MaxDurationMs.Should().Be(18_000);
        clone.CheckIntervalMs.Should().Be(120);
        clone.MinBodyDelayMs.Should().Be(250);
        var clonedPredicate = clone.WhileCondition.Should().BeOfType<VisualPresenceCondition>().Subject;
        clonedPredicate.Kind.Should().Be(expectedCondition);
        clonedPredicate.TemplatePath.Should().Be("repeat-target.png");
        clonedPredicate.MinConfidence.Should().Be(0.91);
        clone.BodySteps.Should().ContainSingle().Which.Should().BeOfType<KeyPressStep>().Which.KeyCode.Should().Be(87);
    }

    [Theory]
    [InlineData("then")]
    [InlineData("else")]
    [InlineData("body")]
    public void ControlFlow_MoveNextInAndLastOut_PreservesActionOrderAndIdentity(string slot)
    {
        using var vm = CreateViewModel();
        vm.AddDelayCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        var existingChild = new KeyPressStep(65);
        var next = new KeyPressStep(66);
        var remaining = new KeyPressStep(67);
        var condition = new VisualPresenceCondition();
        MacroStep block = slot == "body"
            ? new LoopStep(20, [existingChild], condition)
            : new IfThenElseStep(condition, slot == "then" ? [existingChild] : [], slot == "else" ? [existingChild] : []);
        vm.ReplaceStepAt(0, block, reloadPanel: false);
        vm.ReplaceStepAt(1, next, reloadPanel: false);
        vm.ReplaceStepAt(2, remaining, reloadPanel: true);
        vm.SelectedStep = vm.Steps[0];

        vm.MoveNextIntoSelectedBlock(slot);

        vm.Steps.Should().HaveCount(2);
        vm.GetStepAt(1)!.StepId.Should().Be(remaining.StepId);
        IReadOnlyList<MacroStep> children = slot switch
        {
            "body" => ((LoopStep)vm.GetStepAt(0)!).BodySteps,
            "else" => ((IfThenElseStep)vm.GetStepAt(0)!).ElseSteps,
            _ => ((IfThenElseStep)vm.GetStepAt(0)!).ThenSteps
        };
        children.Select(step => step.StepId).Should().Equal(existingChild.StepId, next.StepId);
        vm.SelectedStepIndex.Should().Be(0);

        vm.RemoveLastFromSelectedBlock(slot);

        vm.Steps.Should().HaveCount(3);
        vm.GetStepAt(1)!.StepId.Should().Be(next.StepId);
        vm.GetStepAt(2)!.StepId.Should().Be(remaining.StepId);
        children = slot switch
        {
            "body" => ((LoopStep)vm.GetStepAt(0)!).BodySteps,
            "else" => ((IfThenElseStep)vm.GetStepAt(0)!).ElseSteps,
            _ => ((IfThenElseStep)vm.GetStepAt(0)!).ThenSteps
        };
        children.Should().ContainSingle().Which.StepId.Should().Be(existingChild.StepId);

        vm.UndoEditCommand.Execute(null);

        vm.Steps.Should().HaveCount(2);
        vm.GetStepAt(1)!.StepId.Should().Be(remaining.StepId);
        children = slot switch
        {
            "body" => ((LoopStep)vm.GetStepAt(0)!).BodySteps,
            "else" => ((IfThenElseStep)vm.GetStepAt(0)!).ElseSteps,
            _ => ((IfThenElseStep)vm.GetStepAt(0)!).ThenSteps
        };
        children.Select(step => step.StepId).Should().Equal(existingChild.StepId, next.StepId);
    }
}

