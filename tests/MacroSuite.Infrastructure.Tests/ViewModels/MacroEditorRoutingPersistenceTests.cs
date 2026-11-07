using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.AI;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Usage;
using Xunit;
using MacroSuite.Infrastructure.Storage;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void RouteDestination_RealComboBoxBinding_PreservesIdWhenChoicesRefreshAndReorder()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var vm = CreateViewModel();
                vm.NewMacroCommand.Execute(null);
                vm.AddDelayCommand.Execute(null);
                vm.AddDelayCommand.Execute(null);
                Guid target = vm.GetStepAt(1)!.StepId;
                vm.ReplaceStepAt(0, new IfThenElseStep(new VisualPresenceCondition(), [])
                { Routing = new BlockRoutingOptions { OnNotSatisfied = FlowRoute.Jump, TargetStepId = target } }, true);
                var combo = new System.Windows.Controls.ComboBox { DisplayMemberPath = "DisplayName", SelectedValuePath = "StepId" };
                combo.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty,
                    new System.Windows.Data.Binding(nameof(ActionPropertiesViewModel.RouteTargets)) { Source = vm.ActionEditor });
                combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                    new System.Windows.Data.Binding(nameof(ActionPropertiesViewModel.RouteTargetStepId))
                    { Source = vm.ActionEditor, Mode = System.Windows.Data.BindingMode.TwoWay });
                PumpRoutingBindings();
                combo.SelectedValue.Should().Be(target);

                vm.MoveDownCommand.Execute(null);
                vm.ActionEditor.RefreshRouteTargets();
                PumpRoutingBindings();

                vm.ActionEditor.RouteTargetStepId.Should().Be(target);
                combo.SelectedValue.Should().Be(target);
                ((IfThenElseStep)vm.GetStepAt(1)!).Routing.TargetStepId.Should().Be(target);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
        error.Should().BeNull();
    }

    private static void PumpRoutingBindings()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private const string StorageTestPassphrase = "routing export backup passphrase 2026";

    private static byte[] RoutingTemplatePng()
    {
        using var image = new OpenCvSharp.Mat(8, 8, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(17, 99, 201));
        OpenCvSharp.Cv2.ImEncode(".png", image, out byte[] bytes); return bytes;
    }

    private MacroEditorViewModel CreatePersistentEditor(string directory)
    {
        var gate = new DummyFeatureGate();
        return new MacroEditorViewModel(new DummyCaptureService(), new DummyInputPlayer(),
            new AIVisionRouter(new InMemorySecretVault()), new DummyApprovalGate(), gate,
            new UsageTracker(new DummyUsageRepository(), gate), new DummyAuditLogger(),
            seedDemoData: false, customMacrosDirectory: directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockRoutes_ApplyAndReload_PreserveExplicitPolicyAndStableDestination(bool loop)
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        var child = new KeyPressStep(65);
        var condition = new VisualPresenceCondition { FailurePolicy = ConditionFailurePolicy.Stop };
        MacroStep block = loop ? new LoopStep(2, [child], condition) : new IfThenElseStep(condition, [child]);
        Guid destination = vm.GetStepAt(1)!.StepId;
        vm.ReplaceStepAt(0, block, reloadPanel: true);
        vm.ActionEditor.OnNotSatisfiedRouteIndex = (int)FlowRoute.Jump;
        vm.ActionEditor.OnFailureRouteIndex = (int)FlowRoute.Retry;
        vm.ActionEditor.RouteTargetStepId = destination;
        vm.ActionEditor.RouteRetryCount = 4;
        vm.ActionEditor.RouteRetryDelayMs = 230;

        vm.ActionEditor.TryCommit().Should().BeTrue();
        var saved = vm.GetStepAt(0)!;
        var routing = saved is LoopStep savedLoop ? savedLoop.Routing : ((IfThenElseStep)saved).Routing;
        routing.OnNotSatisfied.Should().Be(FlowRoute.Jump);
        routing.OnFailure.Should().Be(FlowRoute.Retry);
        routing.TargetStepId.Should().Be(destination);
        routing.RetryCount.Should().Be(4);
        routing.RetryDelayMs.Should().Be(230);
        vm.ActionEditor.FailurePolicyIndex.Should().Be((int)ConditionFailurePolicy.Stop);
        (saved is LoopStep l ? l.BodySteps : ((IfThenElseStep)saved).ThenSteps)[0].StepId.Should().Be(child.StepId);

        vm.MoveDownCommand.Execute(null);
        var moved = vm.GetStepAt(1)!;
        (moved is LoopStep ml ? ml.Routing : ((IfThenElseStep)moved).Routing).TargetStepId.Should().Be(destination);
        vm.ActionEditor.RouteTargets.Should().Contain(t => t.StepId == destination && t.DisplayName.StartsWith("1."));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("nested")]
    [InlineData("watcher")]
    public void BlockRoute_InvalidDestination_RejectsApplyWithoutReplacingBlock(string invalidKind)
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        var nested = new DelayStep(TimeSpan.Zero);
        var original = new IfThenElseStep(new VisualPresenceCondition(), [nested]);
        var second = new DelayStep(TimeSpan.Zero) { IsEnabled = false };
        MacroStep target = invalidKind == "watcher"
            ? new WaitUntilStep(new ImageCondition("image.png", null))
            { AppearanceRedirect = new AppearanceRedirectOptions { TargetStepId = original.StepId } }
            : second;
        vm.ReplaceStepAt(1, target, reloadPanel: false);
        vm.ReplaceStepAt(0, original, reloadPanel: true);
        vm.ActionEditor.OnNotSatisfiedRouteIndex = (int)FlowRoute.Jump;
        vm.ActionEditor.RouteTargetStepId = invalidKind switch
        {
            "missing" => Guid.NewGuid(),
            "nested" => nested.StepId,
            _ => target.StepId
        };

        vm.ActionEditor.TryCommit().Should().BeFalse();
        vm.GetStepAt(0).Should().BeSameAs(original);
        vm.ActionEditor.DraftError.Should().Contain("destination");
    }

    [Fact]
    public void RoutingEdit_PreservesCombinedConditionAndUnboundedCountedLoop()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        var condition = new AndCondition(new VisualPresenceCondition(), new VisualPresenceCondition());
        vm.ReplaceStepAt(0, new IfThenElseStep(condition, []), reloadPanel: true);
        vm.ActionEditor.OnFailureRouteIndex = (int)FlowRoute.Stop;
        vm.ActionEditor.CanEditPresenceCondition.Should().BeFalse();
        vm.ActionEditor.TryCommit().Should().BeTrue();
        ((IfThenElseStep)vm.GetStepAt(0)!).Condition.Should().BeSameAs(condition);

        vm.ReplaceStepAt(0, new LoopStep(3, []) { MaxDurationMs = 0 }, reloadPanel: true);
        vm.ActionEditor.OnFailureRouteIndex = (int)FlowRoute.Continue;
        vm.ActionEditor.TryCommit().Should().BeTrue();
        var loop = (LoopStep)vm.GetStepAt(0)!;
        loop.WhileCondition.Should().BeNull();
        loop.MaxDurationMs.Should().Be(0);
        loop.MaxIterations.Should().Be(3);
    }

    [Fact]
    public void AppearanceRedirect_ApplyCopyAndExport_RetainsTargetAndExcludesRepeatingMode()
    {
        using var folder = new RoutingTempFolder();
        using var vm = CreatePersistentEditor(folder.Library);
        vm.AddDelayCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        Guid targetId = vm.GetStepAt(1)!.StepId;
        vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition(RoutingTemplatePng()))
        { RepeatWhileVisible = true }, reloadPanel: true);
        vm.ActionEditor.RedirectOnAppearance = true;
        vm.ActionEditor.AppearanceTargetStepId = targetId;
        vm.ActionEditor.AppearanceCooldownMs = 950;
        vm.ActionEditor.TryCommit().Should().BeTrue();
        var source = (WaitUntilStep)vm.GetStepAt(0)!;
        source.RepeatWhileVisible.Should().BeFalse();
        source.WaitWhile.Should().BeFalse();
        source.AppearanceRedirect!.TargetStepId.Should().Be(targetId);
        source.AppearanceRedirect.CooldownMs.Should().Be(950);
        vm.ActionEditor.RouteTargets.Should().NotContain(t => t.StepId == source.StepId);

        vm.DuplicateStepCommand.Execute(null);
        var copied = (WaitUntilStep)vm.GetStepAt(1)!;
        copied.StepId.Should().NotBe(source.StepId);
        copied.AppearanceRedirect.Should().BeEquivalentTo(source.AppearanceRedirect);
        string export = Path.Combine(folder.Root, "export.zbackup");
        vm.ExportMacroFile(export, StorageTestPassphrase).Should().BeTrue();
        var loaded = new ProtectedMacroStore(folder.Library).Load(export, StorageTestPassphrase).Model;
        ((WaitUntilStep)loaded.Steps[1]).AppearanceRedirect.Should().BeEquivalentTo(source.AppearanceRedirect);

        vm.ActionEditor.RepeatImageWhileVisible = true;
        ((WaitUntilStep)vm.GetStepAt(1)!).AppearanceRedirect.Should().BeNull();
    }

    [Fact]
    public void Import_CreatesOwnedCopy_PreservesSourceExistingScriptAndMetadata()
    {
        using var folder = new RoutingTempFolder();
        using var vm = CreatePersistentEditor(folder.Library);
        vm.AddDelayCommand.Execute(null);
        vm.SaveMacroCommand.Execute(null);
        var existing = vm.SelectedScript!;
        string existingPayload = new MacroJsonSerializer().Serialize(new ProtectedMacroStore(folder.Library).Load(existing.FilePath).Model);
        var step = new DelayStep(TimeSpan.FromMilliseconds(73));
        var input = new MacroModel
        {
            Id = existing.Id, Name = "Imported copy", Description = "Keep my description", IsFavorite = true,
            CreatedAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Settings = new MacroSettings { Hotkey = "Ctrl+9", IsActive = false, RepeatCount = 7, PlaybackSpeed = 1.5 },
            Steps = [step]
        };
        string source = Path.Combine(folder.Root, "source.json");
        var serializer = new MacroJsonSerializer();
        serializer.SerializeToFile(input, source);
        byte[] original = File.ReadAllBytes(source);

        vm.ImportMacroFile(source).Should().BeTrue();
        var imported = vm.SelectedScript!;
        imported.Id.Should().NotBe(existing.Id);
        imported.FilePath.Should().StartWith(folder.Library);
        imported.Hotkey.Should().Be("Ctrl+9");
        imported.IsActive.Should().BeFalse();
        imported.RepeatCount.Should().Be(7);
        imported.CreatedAt.Should().Be(input.CreatedAt);
        imported.IsFavorite.Should().BeTrue();
        vm.Description.Should().Be(input.Description);
        vm.GetStepAt(0)!.StepId.Should().Be(step.StepId);
        File.ReadAllBytes(source).Should().Equal(original);
        new MacroJsonSerializer().Serialize(new ProtectedMacroStore(folder.Library).Load(existing.FilePath).Model).Should().Be(existingPayload);
        vm.DeleteScript(imported);
        File.ReadAllBytes(source).Should().Equal(original);
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"schemaVersion\":999,\"steps\":[]}")]
    public void Import_InvalidDocument_ReportsFailureAndKeepsSelection(string json)
    {
        using var folder = new RoutingTempFolder();
        using var vm = CreatePersistentEditor(folder.Library);
        vm.AddDelayCommand.Execute(null);
        var selected = vm.SelectedScript;
        int count = vm.MacroScripts.Count;
        string source = Path.Combine(folder.Root, "invalid.json");
        File.WriteAllText(source, json);
        var statuses = new List<string>();
        vm.StatusChanged += statuses.Add;
        vm.ImportMacroFile(source).Should().BeFalse();
        vm.SelectedScript.Should().BeSameAs(selected);
        vm.MacroScripts.Should().HaveCount(count);
        File.ReadAllText(source).Should().Be(json);
        statuses.Should().Contain(s => s.StartsWith("Could not import"));
    }

    [Fact]
    public void Export_CommitsDraft_AndLockedDestinationKeepsPreviousContents()
    {
        using var folder = new RoutingTempFolder();
        using var vm = CreatePersistentEditor(folder.Library);
        vm.AddDelayCommand.Execute(null);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.DurationMs = 1234;
        string target = Path.Combine(folder.Root, "export.zbackup");
        vm.ExportMacroFile(target, StorageTestPassphrase).Should().BeTrue();
        ((DelayStep)new ProtectedMacroStore(folder.Library).Load(target, StorageTestPassphrase).Model.Steps[0]).Duration
            .Should().Be(TimeSpan.FromMilliseconds(1234));
        byte[] previous = File.ReadAllBytes(target);
        vm.ActionEditor.DurationMs = 4321;
        var statuses = new List<string>();
        vm.StatusChanged += statuses.Add;
        using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            vm.ExportMacroFile(target, StorageTestPassphrase).Should().BeFalse();
        File.ReadAllBytes(target).Should().Equal(previous);
        statuses.Should().Contain(s => s.StartsWith("Could not export"));
        statuses.Should().NotContain(s => s.StartsWith("Exported"));
    }

    [Fact]
    public void Export_InvalidDraft_DoesNotWriteOrSwitchScript()
    {
        using var folder = new RoutingTempFolder();
        using var vm = CreatePersistentEditor(folder.Library);
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, new IfThenElseStep(new VisualPresenceCondition(), []), reloadPanel: true);
        vm.ActionEditor.PaletteText = "invalid palette";
        string target = Path.Combine(folder.Root, "export.zbackup");
        File.WriteAllText(target, "keep existing export");
        vm.ExportMacroFile(target, StorageTestPassphrase).Should().BeFalse();
        File.ReadAllText(target).Should().Be("keep existing export");
    }

    private sealed class RoutingTempFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zenith-routing-ui-" + Guid.NewGuid().ToString("N"));
        public string Library => Path.Combine(Root, "library");
        public RoutingTempFolder() => Directory.CreateDirectory(Library);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
