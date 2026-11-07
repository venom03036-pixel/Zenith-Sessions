using System.Drawing;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Usage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectionTest_CompletedOrCancelled_PreservesCleanDraftAndUndo(bool cancel)
    {
        await WpfTestApplication.RunAsync(async () =>
        {
        using var matcher = new DraftStateProbeMatcher();
        var featureGate = new DummyFeatureGate();
        using var vm = new MacroEditorViewModel(new DummyCaptureService(), new DummyInputPlayer(),
            new AIVisionRouter(new InMemorySecretVault()), new DummyApprovalGate(), featureGate,
            new UsageTracker(new DummyUsageRepository(), featureGate), new DummyAuditLogger(),
            imageMatcher: matcher);
        vm.NewMacroCommand.Execute(null);
        var original = new WaitUntilStep(new ImageCondition(new byte[] { 1 }, .8, new Rectangle(20, 40, 30, 50)));
        vm.Steps.ReplaceAll([original]);
        vm.SelectedStep = vm.Steps[0];
        string originalSaveState = vm.SaveStateText;
        bool originalUndo = vm.UndoEditCommand.CanExecute(null);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ActionEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActionPropertiesViewModel.IsTestingDetection) && !vm.ActionEditor.IsTestingDetection)
                completed.TrySetResult();
        };

        vm.ActionEditor.TestDetectionCommand.CanExecute(null).Should().BeTrue();
        vm.ActionEditor.TestDetectionCommand.Execute(null);
        await matcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.ActionEditor.IsTestingDetection.Should().BeTrue();
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.SaveStateText.Should().Be(originalSaveState);
        if (cancel) vm.ActionEditor.CancelDetectionTestCommand.Execute(null);
        else matcher.Result.TrySetResult(MatchResult.NotFound);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.ActionEditor.DetectionTestResult.Should().Contain(cancel ? "cancelled" : "Not found");
        vm.ActionEditor.IsTestingDetection.Should().BeFalse();
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.HasMacroMetadataChanges.Should().BeFalse();
        vm.SaveStateText.Should().Be(originalSaveState);
        vm.Steps[0].Step.Should().BeSameAs(original);
        vm.UndoEditCommand.CanExecute(null).Should().Be(originalUndo);
        matcher.Frame.Should().NotBeNull();
        matcher.Frame!.IsDisposed.Should().BeTrue();
        vm.ActionEditor.TryCommit().Should().BeTrue();
        vm.UndoEditCommand.CanExecute(null).Should().Be(originalUndo);
        });
    }

    private sealed class DraftStateProbeMatcher : IImageMatcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MatchResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureFrame? Frame { get; private set; }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, byte[] bytes, double minConfidence = .8,
            Rectangle? searchRegion = null, CancellationToken ct = default)
        {
            Frame = frame;
            Started.TrySetResult();
            return Result.Task.WaitAsync(ct);
        }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, string path, double minConfidence = .8,
            Rectangle? searchRegion = null, CancellationToken ct = default)
            => MatchAsync(frame, [], minConfidence, searchRegion, ct);
        public void Dispose() => Result.TrySetCanceled();
    }
}
