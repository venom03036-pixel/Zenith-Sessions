using System.Drawing;
using FluentAssertions;
using MacroSuite.App.Services;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void AppliedAction_AlsoCommitsMacroInspectorMetadata()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        vm.MacroName = "Image helper"; vm.Description = "Handle independent image groups";
        vm.AddDelayCommand.Execute(null);
        vm.SelectedScript!.Name.Should().Be("Image helper");
        vm.SelectedScript.Description.Should().Be("Handle independent image groups");
    }

    [Fact]
    public void BlockMove_RejectsMovingReferencedTargetIntoNestedScope()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        var target = new DelayStep(TimeSpan.Zero);
        var block = new IfThenElseStep(new VisualPresenceCondition(), [])
        { Routing = new BlockRoutingOptions { OnNotSatisfied = FlowRoute.Jump, TargetStepId = target.StepId } };
        vm.Steps.ReplaceAll([block, target]); vm.SelectedStep = vm.Steps[0];
        string? status = null; vm.StatusChanged += text => status = text;
        vm.MoveNextIntoSelectedBlock("then");
        vm.Steps.Count.Should().Be(2);
        ((IfThenElseStep)vm.Steps[0].Step).ThenSteps.Should().BeEmpty();
        status.Should().Contain("Jump target");
    }

    [Fact]
    public void BlockOutcome_ResolvesElseAndInheritedFailureSeparately()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        var condition = new VisualPresenceCondition { FailurePolicy = ConditionFailurePolicy.Stop };
        vm.Steps.ReplaceAll([new IfThenElseStep(condition, [], [new DelayStep(TimeSpan.Zero)])]);
        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.BlockOutcomeSummary.Should().Contain("Run Else actions, then continue");
        vm.ActionEditor.BlockOutcomeSummary.Should().Contain("Inherited condition policy: Stop macro");
        vm.ActionEditor.TryCommit().Should().BeTrue();
        vm.UndoEditCommand.CanExecute(null).Should().BeFalse("selecting an unchanged block must not create an edit");
    }

    [Fact]
    public void Outline_NestedPropertyEdit_ReplacesOnlyChildAndCanUndo()
    {
        using var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        var child = new DelayStep(TimeSpan.FromMilliseconds(120));
        var other = new KeyPressStep(77);
        var block = new LoopStep(3, [child, other]);
        vm.Steps.ReplaceAll([block]);
        vm.SelectedStep = (MacroStepItemViewModel)vm.OutlineRows[1]!;
        vm.ActionEditor.DurationMs = 840;
        vm.ActionEditor.Apply().Should().BeTrue();
        var changed = (LoopStep)vm.Steps[0].Step;
        changed.StepId.Should().Be(block.StepId);
        changed.BodySteps[0].StepId.Should().Be(child.StepId);
        ((DelayStep)changed.BodySteps[0]).Duration.Should().Be(TimeSpan.FromMilliseconds(840));
        changed.BodySteps[1].Should().BeSameAs(other);
        vm.UndoEditCommand.Execute(null);
        ((DelayStep)((LoopStep)vm.Steps[0].Step).BodySteps[0]).Duration.Should().Be(TimeSpan.FromMilliseconds(120));
    }

    [Fact]
    public void Outline_MoveWholeBlock_KeepsNestedChildrenAndRouteIds()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        var target = new DelayStep(TimeSpan.Zero);
        var child = new KeyPressStep(77);
        var block = new IfThenElseStep(new VisualPresenceCondition(), [new LoopStep(2, [child])])
        { Routing = new BlockRoutingOptions { OnNotSatisfied = FlowRoute.Jump, TargetStepId = target.StepId } };
        vm.Steps.ReplaceAll([block, target]);
        vm.SelectedStep = vm.Steps[0];
        vm.MoveDownCommand.Execute(null);
        var moved = (IfThenElseStep)vm.Steps[1].Step;
        moved.Routing.TargetStepId.Should().Be(target.StepId);
        ((LoopStep)moved.ThenSteps[0]).BodySteps[0].StepId.Should().Be(child.StepId);
        vm.UndoEditCommand.Execute(null);
        vm.Steps[0].Step.StepId.Should().Be(block.StepId);
    }

    [Fact]
    public void Outline_NestedReorderingAndDuplication_StayInContainingScope()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        var a = new DelayStep(TimeSpan.FromMilliseconds(10)); var b = new DelayStep(TimeSpan.FromMilliseconds(20));
        vm.Steps.ReplaceAll([new LoopStep(2, [a, b]), new KeyPressStep(65)]);
        vm.SelectedStep = (MacroStepItemViewModel)vm.OutlineRows[1]!;
        vm.MoveDownCommand.CanExecute(null).Should().BeTrue();
        vm.MoveDownCommand.Execute(null);
        ((LoopStep)vm.Steps[0].Step).BodySteps.Select(s => s.StepId).Should().Equal(b.StepId, a.StepId);
        vm.DuplicateStepCommand.Execute(null);
        var children = ((LoopStep)vm.Steps[0].Step).BodySteps;
        children.Should().HaveCount(3);
        children[2].StepId.Should().NotBe(a.StepId);
        vm.Steps.Count.Should().Be(2);
    }

    [Fact]
    public void Outline_CollapseKeepsExecutionInsideVisibleAndBoundsMaterializedRows()
    {
        var store = new MacroActionStore();
        var children = Enumerable.Range(0, 1600).Select(_ => (MacroStep)new DelayStep(TimeSpan.Zero)).ToArray();
        var loop = new LoopStep(2, children); store.Add(loop);
        var roots = new VirtualizingStepCollection(store);
        var outline = new StructuredActionCollection(roots);
        for (int i = 0; i < outline.Count; i++) _ = outline[i];
        outline.CachedChildCount.Should().BeLessThanOrEqualTo(VirtualizingStepCollection.DefaultCacheCap);
        roots.CachedViewModelCount.Should().Be(1);
        outline.SetExecution(children[900].StepId);
        outline.Toggle(loop.StepId);
        outline.Count.Should().Be(1);
        var row = (MacroStepItemViewModel)outline[0]!;
        row.ContainsExecutingAction.Should().BeTrue();
        row.ExecutionLabel.Should().Be("Running inside");
        outline.FindVisibleExecution(children[900].StepId).Should().BeSameAs(row);
    }

    [Fact]
    public void Presentation_ReadableNamesDescribeDifferentImageModes()
    {
        var image = new ImageCondition("heal.png");
        MacroStepItemViewModel.FromStep(new WaitUntilStep(image), 0).ReadableName.Should().Be("Wait for image");
        MacroStepItemViewModel.FromStep(new WaitUntilStep(image) { WaitWhile = true }, 0).ReadableName.Should().Be("Wait for image to disappear");
        MacroStepItemViewModel.FromStep(new WaitUntilStep(image) { RepeatWhileVisible = true }, 0).ReadableName.Should().Be("Repeat while image is visible");
        MacroStepItemViewModel.FromStep(new KeyPressStep(77), 0).ReadableName.Should().Be("Press M");
    }

    [Fact]
    public void GeneratedImport_PreservesDisabledStateAndDetachesSourceWithoutRunning()
    {
        using var vm = CreateViewModel();
        var step = new KeyPressStep(77);
        var source = new MacroModel { Name = "Generated", Settings = new MacroSettings { IsActive = false, Hotkey = "F12" }, Steps = [step] };
        vm.AddGeneratedMacro(source).Should().BeTrue();
        vm.SelectedScript!.Name.Should().Be("Generated");
        vm.SelectedScript.IsActive.Should().BeFalse();
        vm.SelectedScript.Hotkey.Should().BeEmpty();
        vm.Steps[0].Step.Should().NotBeSameAs(step);
        vm.IsRunning.Should().BeFalse();
        vm.ShowMacroProperties.Should().BeTrue();
    }

    [Fact]
    public async Task Playback_ExternalInputActivityBlocksStartingAndRecording()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null); vm.AddDelayCommand.Execute(null);
        vm.ExternalInputIsActive = () => true;
        var statuses = new List<string>(); vm.StatusChanged += statuses.Add;
        await vm.RunMacroAsync(); await vm.StartRecordingAsync();
        vm.IsRunning.Should().BeFalse(); vm.IsRecording.Should().BeFalse();
        statuses.Should().Contain("Stop Auto Clicker before running a macro.");
        statuses.Should().Contain("Stop Auto Clicker before recording a macro.");
    }

    [Fact]
    public async Task Playback_SelectionIsIndependentAndEditingIsLocked()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null); vm.ActionEditor.DurationMs = 20; vm.ActionEditor.Apply();
        var first = vm.SelectedScript!;
        vm.NewMacroCommand.Execute(null); var second = vm.SelectedScript!;
        vm.SelectedScript = first; vm.SelectedStep = vm.Steps[0];
        vm.EnterCompactOverlay = () =>
        {
            vm.IsEditingLocked.Should().BeTrue();
            vm.SelectedScript = second;
            vm.TransportStateText.Should().Contain(first.Name);
            vm.RunningMacroName.Should().Be(first.Name);
            vm.AddDelayCommand.Execute(null);
            vm.Steps.Count.Should().Be(0);
            vm.PrimaryTransportText.Should().Be("Pause");
        };
        await vm.RunMacroAsync(first);
        vm.SelectedScript.Should().BeSameAs(second);
        vm.IsEditingLocked.Should().BeFalse();
        vm.PrimaryTransportText.Should().Be("Run");
    }

    [Fact]
    public async Task Playback_UsesCurrentNameAndKeepsFailedChildVisibleInCollapsedBlock()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        vm.MacroName = "Inventory check";
        var failure = new EditorFailureStep();
        var block = new LoopStep(1, [failure]);
        vm.Steps.ReplaceAll([block]); vm.SelectedStep = null;
        vm.OutlineRows.Toggle(block.StepId);
        vm.EnterCompactOverlay = () => vm.RunningMacroName.Should().Be("Inventory check");
        await vm.RunMacroAsync();
        vm.HasExecutionError.Should().BeTrue();
        vm.LastExecutionError.Should().Contain("Image template missing");
        var row = (MacroStepItemViewModel)vm.OutlineRows[0]!;
        row.HasFailure.Should().BeTrue();
        row.StatusLabel.Should().Contain("Error");
        row.IsExecuting.Should().BeFalse();
    }

    [Fact]
    public void CapturedAction_InsertUsesHistoryAndSelectedNestedScope()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        var child = new DelayStep(TimeSpan.FromMilliseconds(10));
        vm.Steps.ReplaceAll([new LoopStep(2, [child])]);
        vm.SelectedStep = (MacroStepItemViewModel)vm.OutlineRows[1]!;
        var captured = new WaitUntilStep(new ImageCondition(new byte[] { 1 }));
        vm.TryInsertStepAfterSelection(captured).Should().BeTrue();
        ((LoopStep)vm.Steps[0].Step).BodySteps.Should().ContainInOrder(child, captured);
        vm.UndoEditCommand.Execute(null);
        ((LoopStep)vm.Steps[0].Step).BodySteps.Should().ContainSingle().Which.StepId.Should().Be(child.StepId);
    }

    [Fact]
    public async Task ShutdownAsync_WaitsForPlaybackCleanupAndRejectsNewWork()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        var step = new EditorDrainingStep();
        vm.Steps.ReplaceAll([step]);
        Task run = vm.RunMacroAsync();
        await step.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task shutdown = vm.ShutdownAsync();
        try
        {
            await step.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            shutdown.IsCompleted.Should().BeFalse("shared services must stay alive until the action finishes its cleanup");
            vm.TryInsertStepAfterSelection(new DelayStep(TimeSpan.Zero)).Should().BeFalse();
        }
        finally { step.FinishCleanup.TrySetResult(); }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        await run;
        vm.IsRunning.Should().BeFalse();
        vm.RunProgressText.Should().StartWith("Run elapsed:");
        await vm.RunMacroAsync(); await vm.StartRecordingAsync();
        step.Starts.Should().Be(1);
        vm.IsRecording.Should().BeFalse();
        vm.ShutdownAsync().Should().BeSameAs(shutdown);
    }

    [Fact]
    public async Task Playback_SelectionDuringStartupDoesNotChangeRepeatSnapshot()
    {
        using var vm = CreateViewModel(); vm.NewMacroCommand.Execute(null);
        var counter = new EditorCountingStep();
        vm.Steps.ReplaceAll([counter]); vm.RepeatCount = 2; vm.RepeatUntilStopped = false;
        var first = vm.SelectedScript!;
        vm.NewMacroCommand.Execute(null); vm.RepeatCount = 7;
        var second = vm.SelectedScript!;
        vm.SelectedScript = first;
        vm.EnterCompactOverlay = () => vm.SelectedScript = second;
        await vm.RunMacroAsync();
        counter.Count.Should().Be(2);
    }

    private sealed class EditorCountingStep : MacroStep
    {
        public int Count { get; private set; }
        public override Task ExecuteAsync(MacroSuite.Core.Execution.MacroContext context, CancellationToken ct = default)
        { Count++; return Task.CompletedTask; }
    }

    private sealed class EditorFailureStep : MacroStep
    {
        public override Task ExecuteAsync(MacroSuite.Core.Execution.MacroContext context, CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("Image template missing"));
    }

    private sealed class EditorDrainingStep : MacroStep
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts { get; private set; }
        public override async Task ExecuteAsync(MacroSuite.Core.Execution.MacroContext context, CancellationToken ct = default)
        {
            Starts++; Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { CancellationObserved.TrySetResult(); await FinishCleanup.Task; }
        }
    }

    [Fact]
    public async Task DetectionProbe_UsesOneSharedRegionFrameAndDisposesIt()
    {
        using var capture = new DummyCaptureService(); using var matcher = new ProbeMatcher();
        var probe = new ImageDetectionProbe(capture, matcher);
        var result = await probe.TestAsync(new ImageCondition(new byte[] { 1 }), new Rectangle(20, 40, 30, 50), CancellationToken.None);
        matcher.Calls.Should().Be(1);
        matcher.Frame!.Width.Should().Be(30); matcher.Frame.Height.Should().Be(50);
        matcher.Frame.IsDisposed.Should().BeTrue();
        result.Status.Should().Be(MatchOutcome.NotFound);
    }

    [Fact]
    public void ShortcutRegistry_AutoClickerReservesPanicAndRejectsUnknownOwners()
    {
        using var vm = CreateViewModel();
        vm.AutoClickerHotkey = "F1";
        vm.TryAssignBind("autoclicker", vm.PanicHotkey).Should().BeFalse();
        vm.AutoClickerHotkey.Should().Be("F1");
        vm.TryAssignBind("unknown", "Shift+9").Should().BeFalse();
        vm.TryAssignBind("autoclicker", "Ctrl+Shift+L").Should().BeTrue();
        vm.AutoClickerHotkey.Should().Be("Ctrl+Shift+L");
    }

    private sealed class ProbeMatcher : IImageMatcher
    {
        public int Calls { get; private set; }
        public CaptureFrame? Frame { get; private set; }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, byte[] bytes, double minConfidence = 0.8, Rectangle? searchRegion = null, CancellationToken ct = default)
        { Calls++; Frame = frame; return Task.FromResult(MatchResult.NotFound); }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, string path, double minConfidence = 0.8, Rectangle? searchRegion = null, CancellationToken ct = default)
            => MatchAsync(frame, Array.Empty<byte>(), minConfidence, searchRegion, ct);
        public void Dispose() { }
    }
}
