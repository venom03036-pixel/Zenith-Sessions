using System.Drawing;
using System.Reflection;
using FluentAssertions;
using MacroSuite.App.Overlays;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void ImageSearchArea_LoadChooseApplySaveAndClone_PreserveTemplateAndRepeatSettings()
    {
        var originalArea = new Rectangle(-400, 90, 800, 600);
        var selectedArea = new Rectangle(-320, 100, 640, 480);
        var source = new WaitUntilStep(new ImageCondition("original.png", new byte[] { 1, 2, 3, 4 }, 0.87, originalArea))
        {
            RepeatWhileVisible = true, RepeatDelayMs = 950,
            Timeout = TimeSpan.FromSeconds(7), PollInterval = TimeSpan.FromMilliseconds(110),
            RequiredConfirmations = 3, RequiredFalseConfirmations = 2,
            StabilityDurationMs = 120, ThrowOnTimeout = true
        };
        using var vm = CreateImageSearchAreaEditor(source);
        vm.ActionEditor.ImageSearchRegion.Should().Be(originalArea);
        vm.ActionEditor.ImageSearchAreaSummary.Should().Contain("800 × 600").And.Contain("(-400, 90)");
        vm.RegionPrompter = () => selectedArea;

        vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);

        vm.ActionEditor.ImageSearchRegion.Should().Be(selectedArea);
        ((ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition).SearchRegion.Should().Be(originalArea,
            "choosing an area only changes the draft until it is applied");
        vm.ActionEditor.Apply().Should().BeTrue();
        var applied = (WaitUntilStep)vm.GetStepAt(0)!;
        var serializer = new MacroJsonSerializer();
        var loaded = (WaitUntilStep)serializer.Deserialize(serializer.Serialize(new MacroModel { Steps = [applied] })).Steps[0];
        var clone = (WaitUntilStep)MacroActionFactory.Clone(applied);
        foreach (var wait in new[] { applied, loaded, clone })
        {
            var image = wait.Condition.Should().BeOfType<ImageCondition>().Subject;
            image.SearchRegion.Should().Be(selectedArea);
            image.TemplatePath.Should().Be("original.png");
            image.TemplateBytes.Should().Equal(1, 2, 3, 4);
            image.MinConfidence.Should().Be(0.87);
            wait.RepeatWhileVisible.Should().BeTrue();
            wait.RepeatDelayMs.Should().Be(950);
            wait.Timeout.Should().Be(source.Timeout);
            wait.PollInterval.Should().Be(source.PollInterval);
            wait.RequiredConfirmations.Should().Be(3);
            wait.RequiredFalseConfirmations.Should().Be(2);
            wait.StabilityDurationMs.Should().Be(120);
            wait.ThrowOnTimeout.Should().BeTrue();
        }
        applied.StepId.Should().Be(source.StepId);
        clone.StepId.Should().NotBe(source.StepId);
    }

    [Fact]
    public void ImageSearchArea_CancelPickerPreservesDraft_AndCancelEditsRestoresSavedArea()
    {
        var savedArea = new Rectangle(10, 20, 600, 400);
        var draftArea = new Rectangle(30, 40, 700, 500);
        using var vm = CreateImageSearchAreaEditor(new WaitUntilStep(new ImageCondition("target.png", searchRegion: savedArea)));
        vm.RegionPrompter = () => draftArea;
        vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
        vm.RegionPrompter = () => null;
        vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
        vm.ActionEditor.ImageSearchRegion.Should().Be(draftArea);

        vm.ActionEditor.CancelEdits();

        vm.ActionEditor.ImageSearchRegion.Should().Be(savedArea);
        ((ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition).SearchRegion.Should().Be(savedArea);
    }

    [Fact]
    public void ImageSearchArea_ResetReturnsToDefaultOnlyAfterApply()
    {
        var savedArea = new Rectangle(10, 20, 600, 400);
        using var vm = CreateImageSearchAreaEditor(new WaitUntilStep(new ImageCondition("target.png", searchRegion: savedArea))
        { RepeatWhileVisible = true, RepeatDelayMs = 800 });
        vm.ActionEditor.ResetImageSearchAreaCommand.Execute(null);
        vm.ActionEditor.ImageSearchRegion.Should().BeNull();
        vm.ActionEditor.ImageSearchAreaSummary.Should().Contain("Full screen");
        ((ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition).SearchRegion.Should().Be(savedArea);

        vm.ActionEditor.Apply().Should().BeTrue();

        var wait = (WaitUntilStep)vm.GetStepAt(0)!;
        ((ImageCondition)wait.Condition).SearchRegion.Should().BeNull();
        wait.RepeatWhileVisible.Should().BeTrue();
        wait.RepeatDelayMs.Should().Be(800);
    }

    [Fact]
    public void ImageSearchArea_TwoImageGroupsKeepIndependentAreas()
    {
        var firstArea = new Rectangle(-700, 50, 600, 400);
        var secondArea = new Rectangle(500, 200, 800, 600);
        using var vm = CreateImageSearchAreaEditor(new WaitUntilStep(new ImageCondition("OK.png", searchRegion: firstArea))
        { RepeatWhileVisible = true, RepeatDelayMs = 700 });
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(1, new WaitUntilStep(new ImageCondition("X.png", searchRegion: secondArea))
        { RepeatWhileVisible = true, RepeatDelayMs = 1200 }, reloadPanel: true);
        vm.SelectedStep = vm.Steps[1];
        vm.ActionEditor.ImageSearchRegion.Should().Be(secondArea);
        var changedSecond = new Rectangle(600, 250, 700, 500);
        vm.RegionPrompter = () => changedSecond;
        vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
        vm.ActionEditor.Apply().Should().BeTrue();

        vm.SelectedStep = vm.Steps[0];
        vm.ActionEditor.ImageSearchRegion.Should().Be(firstArea);
        vm.ActionEditor.ImageRepeatDelayMs.Should().Be(700);
        vm.SelectedStep = vm.Steps[1];
        vm.ActionEditor.ImageSearchRegion.Should().Be(changedSecond);
        vm.ActionEditor.ImageRepeatDelayMs.Should().Be(1200);
        ((ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition).TemplatePath.Should().Be("OK.png");
        ((ImageCondition)((WaitUntilStep)vm.GetStepAt(1)!).Condition).TemplatePath.Should().Be("X.png");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void ImageSearchArea_DefaultPreviewUsesTheActionMode_AndDoesNotMutateTheStep(
        bool repeat, bool redirect, bool expectedFullScreen)
    {
        using var vm = CreateImageSearchAreaEditor(new WaitUntilStep(new ImageCondition("target.png"))
        {
            RepeatWhileVisible = repeat,
            AppearanceRedirect = redirect ? new AppearanceRedirectOptions { TargetStepId = Guid.NewGuid() } : null
        });
        var stored = vm.GetStepAt(0);
        var previews = new List<(Rectangle? Area, bool FullScreen, string Label)>();
        vm.ImageSearchAreaPreviewer = (area, fullScreen, label) => previews.Add((area, fullScreen, label));

        vm.ActionEditor.ShowImageSearchAreaCommand.Execute(null);

        previews.Should().ContainSingle();
        previews[0].Area.Should().BeNull();
        previews[0].FullScreen.Should().Be(expectedFullScreen);
        previews[0].Label.Should().Contain("target.png");
        vm.ActionEditor.ImageSearchAreaSummary.Should().Contain(expectedFullScreen ? "Full screen" : "Automatic active window");
        vm.GetStepAt(0).Should().BeSameAs(stored);
    }

    [Fact]
    public async Task ImageSearchArea_PreviewUsesUnsavedRegion_AndModeSummaryUpdates()
    {
        using var vm = CreateImageSearchAreaEditor(new WaitUntilStep(new ImageCondition("target.png")));
        vm.ActionEditor.RepeatImageWhileVisible = true;
        vm.ActionEditor.ImageSearchAreaSummary.Should().Contain("Full screen");
        vm.ActionEditor.RedirectOnAppearance = true;
        vm.ActionEditor.ImageSearchAreaSummary.Should().Contain("Automatic active window");
        var region = new Rectangle(-100, -200, 900, 700);
        vm.RegionPrompter = () => region;
        using (LateContinuations.Install())
            vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
        await WaitForEditorInputRetirementAsync(vm);
        Rectangle? previewArea = null;
        vm.ImageSearchAreaPreviewer = (area, _, _) => previewArea = area;

        vm.ActionEditor.ShowImageSearchAreaCommand.Execute(null);

        previewArea.Should().Be(region);
        ((ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition).SearchRegion.Should().BeNull();
    }

    [Fact]
    public async Task ImageSearchArea_ReplacingTemplateKeepsChosenRegionAndRepeatSettings()
    {
        var savedArea = new Rectangle(0, 0, 800, 600);
        var chosenArea = new Rectangle(100, 200, 900, 700);
        using var vm = CreateImageSearchAreaEditor(new WaitUntilStep(new ImageCondition(new byte[] { 1, 2, 3 }, searchRegion: savedArea))
        { RepeatWhileVisible = true, RepeatDelayMs = 950 });
        vm.RegionPrompter = () => chosenArea;
        using (LateContinuations.Install())
            vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
        await WaitForEditorInputRetirementAsync(vm);
        vm.ImagePrompter = () => new ImageTemplateSelection("replacement.png", 0.92, false, null);

        vm.ActionEditor.CaptureImageTemplateCommand.Execute(null);

        var wait = (WaitUntilStep)vm.GetStepAt(0)!;
        var image = (ImageCondition)wait.Condition;
        image.SearchRegion.Should().Be(chosenArea);
        image.TemplatePath.Should().Be("replacement.png");
        image.TemplateBytes.Should().BeNull();
        image.MinConfidence.Should().Be(0.92);
        wait.RepeatWhileVisible.Should().BeTrue();
        wait.RepeatDelayMs.Should().Be(950);
    }

    [Theory]
    [InlineData("recording")]
    [InlineData("starting")]
    [InlineData("running")]
    public void ImageSearchArea_CommandsAreDisabledAndGuardedWhileBusy(string busyState)
    {
        var region = new Rectangle(0, 0, 800, 600);
        using var vm = CreateImageSearchAreaEditor(new WaitUntilStep(new ImageCondition("target.png", searchRegion: region)));
        int calls = 0;
        vm.RegionPrompter = () => { calls++; return new Rectangle(5, 6, 700, 500); };
        vm.ImageSearchAreaPreviewer = (_, _, _) => calls++;
        SetImageSearchAreaTestBusy(vm, busyState, true);
        try
        {
            foreach (var command in new[] { vm.ActionEditor.ShowImageSearchAreaCommand,
                         vm.ActionEditor.ChooseImageSearchAreaCommand, vm.ActionEditor.ResetImageSearchAreaCommand })
            {
                command.CanExecute(null).Should().BeFalse();
                command.Execute(null); // Guard direct invocations too, not just the button's state.
            }
            calls.Should().Be(0);
            vm.ActionEditor.ImageSearchRegion.Should().Be(region);
        }
        finally { SetImageSearchAreaTestBusy(vm, busyState, false); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImageSearchArea_PickerResultIsDiscardedIfSelectionOrBusyStateChanges(bool startsRecording)
    {
        var region = new Rectangle(0, 0, 800, 600);
        using var vm = CreateImageSearchAreaEditor(new WaitUntilStep(new ImageCondition("target.png", searchRegion: region)));
        vm.RegionPrompter = () =>
        {
            if (startsRecording) vm.IsRecording = true;
            else vm.ReplaceStepAt(0, new WaitUntilStep(new ImageCondition("other.png", searchRegion: region)), reloadPanel: true);
            return new Rectangle(5, 6, 700, 500);
        };
        try
        {
            vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
            vm.ActionEditor.ImageSearchRegion.Should().Be(region);
        }
        finally { vm.IsRecording = false; }
    }

    [Fact]
    public async Task ImageSearchArea_PickerAndPreviewFailuresReportStatusWithoutChangingRegion()
    {
        var region = new Rectangle(0, 0, 800, 600);
        using var vm = CreateImageSearchAreaEditor(new WaitUntilStep(new ImageCondition("target.png", searchRegion: region)));
        var messages = new List<string>();
        vm.StatusChanged += messages.Add;
        vm.RegionPrompter = () => throw new InvalidOperationException("Picker unavailable");
        vm.ImageSearchAreaPreviewer = (_, _, _) => throw new InvalidOperationException("Preview unavailable");

        vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
        if (vm.InputOperations.Snapshot.OperationId is { } pickerId)
            await vm.InputOperations.WaitForRetirementAsync(pickerId).WaitAsync(TimeSpan.FromSeconds(3));
        vm.InputOperations.Snapshot.IsQuarantined.Should().BeFalse();
        vm.ActionEditor.ShowImageSearchAreaCommand.Execute(null);
        if (vm.InputOperations.Snapshot.OperationId is { } previewId)
            await vm.InputOperations.WaitForRetirementAsync(previewId).WaitAsync(TimeSpan.FromSeconds(3));

        messages.Should().Contain(m => m.Contains("Picker unavailable"));
        messages.Should().Contain(m => m.Contains("Preview unavailable"));
        vm.ActionEditor.ImageSearchRegion.Should().Be(region);
    }

    [Fact]
    public void ImageSearchArea_InvalidSelectionDoesNotReplaceSavedRegion()
    {
        var region = new Rectangle(0, 0, 800, 600);
        using var vm = CreateImageSearchAreaEditor(new WaitUntilStep(new ImageCondition("target.png", searchRegion: region)));
        vm.RegionPrompter = () => new Rectangle(1, 2, 0, 300);
        vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
        vm.ActionEditor.ImageSearchRegion.Should().Be(region);
    }

    /// <summary>
    /// A finished picker retires its input activity on a later continuation. In the app the
    /// dispatcher runs that continuation before the user's next click arrives; a test that calls
    /// the next command directly on this thread must wait for it, as the dispatcher would.
    /// </summary>
    private static async Task WaitForEditorInputRetirementAsync(MacroEditorViewModel vm)
    {
        if (vm.InputOperations.Snapshot.OperationId is { } activity)
            await vm.InputOperations.WaitForRetirementAsync(activity).WaitAsync(WpfTestApplication.LivenessBound);
        vm.InputOperations.Snapshot.IsBusy.Should().BeFalse("the finished picker must have released the editor's input activity");
    }

    /// <summary>
    /// Models a briefly slow machine for an editor that is not on a dispatcher: work the
    /// editor posts to the current context (such as retiring a finished picker) runs late.
    /// </summary>
    private sealed class LateContinuations : SynchronizationContext, IDisposable
    {
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);
        private readonly SynchronizationContext? _previous = Current;
        private LateContinuations() { }
        public static LateContinuations Install()
        {
            var context = new LateContinuations();
            SetSynchronizationContext(context);
            return context;
        }
        public override void Post(SendOrPostCallback callback, object? state)
            => _ = Task.Delay(Delay).ContinueWith(_ => callback(state), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        public void Dispose() => SetSynchronizationContext(_previous);
    }

    private MacroEditorViewModel CreateImageSearchAreaEditor(WaitUntilStep step)
    {
        var vm = CreateViewModel();
        vm.NewMacroCommand.Execute(null);
        vm.AddDelayCommand.Execute(null);
        vm.ReplaceStepAt(0, step, reloadPanel: true);
        return vm;
    }

    private static void SetImageSearchAreaTestBusy(MacroEditorViewModel vm, string state, bool value)
    {
        // Set transport state without starting hooks, playback, or desktop overlays.
        if (state == "recording") vm.IsRecording = value;
        else if (state == "running") typeof(MacroEditorViewModel).GetProperty(nameof(MacroEditorViewModel.IsRunning))!.SetValue(vm, value);
        else typeof(MacroEditorViewModel).GetField("_isStartingRecording", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
    }
}
