using System.Drawing;
using FluentAssertions;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using OpenCvSharp;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Theory]
    [InlineData(947, 257, 934, 269)]
    [InlineData(602, 365, 598, 369)]
    [InlineData(332, 1224, 456, 1205)]
    public void ImageSearchArea_ApplyRejectsUndersizedSavedArea_WithoutChangingActionOrDraft(
        int templateWidth, int templateHeight, int areaWidth, int areaHeight)
    {
        var area = new Rectangle(-400, 20, areaWidth, areaHeight);
        var source = new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(templateWidth, templateHeight), searchRegion: area))
        { RepeatWhileVisible = true, RepeatDelayMs = 500 };
        using var vm = CreateImageSearchAreaEditor(source);
        vm.ActionEditor.ImageRepeatDelayMs = 750;
        var messages = new List<string>();
        vm.StatusChanged += messages.Add;

        vm.ActionEditor.Apply().Should().BeFalse();

        vm.GetStepAt(0).Should().BeSameAs(source);
        vm.ActionEditor.ImageSearchRegion.Should().Be(area);
        vm.ActionEditor.ImageRepeatDelayMs.Should().Be(750);
        vm.ActionEditor.DraftError.Should().Contain($"at least {templateWidth} × {templateHeight} pixels");
        messages.Should().Contain(vm.ActionEditor.DraftError!);
    }

    [Fact]
    public void ImageSearchArea_ChooseRejectsTooSmallArea_AndAcceptsExactTemplateBoundary()
    {
        var saved = new Rectangle(-300, -100, 500, 400);
        var source = new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(320, 240), searchRegion: saved));
        using var vm = CreateImageSearchAreaEditor(source);
        vm.RegionPrompter = () => new Rectangle(-250, -90, 319, 240);

        vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);

        vm.ActionEditor.ImageSearchRegion.Should().Be(saved);
        vm.GetStepAt(0).Should().BeSameAs(source);
        vm.ActionEditor.DraftError.Should().Contain("at least 320 × 240 pixels");

        var exact = new Rectangle(-250, -90, 320, 240);
        vm.RegionPrompter = () => exact;
        vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
        vm.ActionEditor.ImageSearchRegion.Should().Be(exact);
        vm.ActionEditor.DraftError.Should().BeNull();
        vm.ActionEditor.Apply().Should().BeTrue();
        ((ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition).SearchRegion.Should().Be(exact);
    }

    [Theory]
    [InlineData(10, 10, 320, 240, false)]
    [InlineData(320, 240, 10, 10, true)]
    public void ImageSearchArea_EmbeddedTemplateDimensionsTakePrecedenceOverPath(
        int fileWidth, int fileHeight, int embeddedWidth, int embeddedHeight, bool expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"zenith-area-size-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, SearchAreaTemplatePng(fileWidth, fileHeight));
        try
        {
            var source = new WaitUntilStep(new ImageCondition(path, SearchAreaTemplatePng(embeddedWidth, embeddedHeight),
                searchRegion: new Rectangle(0, 0, 10, 10)));
            using var vm = CreateImageSearchAreaEditor(source);

            vm.ActionEditor.Apply().Should().Be(expected);

            if (!expected) vm.GetStepAt(0).Should().BeSameAs(source);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ImageSearchArea_ReplacementChecksNewTemplate_AndKeepsItPendingUntilAreaFits()
    {
        string path = Path.Combine(Path.GetTempPath(), $"zenith-area-replacement-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, SearchAreaTemplatePng(128, 96));
        try
        {
            var saved = new Rectangle(40, 50, 80, 50);
            var source = new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(64, 32), searchRegion: saved))
            { RepeatWhileVisible = true, RepeatDelayMs = 950 };
            using var vm = CreateImageSearchAreaEditor(source);
            vm.ImagePrompter = () => new ImageTemplateSelection(path, 0.92, false, null);

            vm.ActionEditor.CaptureImageTemplateCommand.Execute(null);

            vm.GetStepAt(0).Should().BeSameAs(source);
            vm.ActionEditor.ImageSearchRegion.Should().Be(saved);
            vm.ActionEditor.ImageTemplateSummary.Should().Contain(Path.GetFileName(path));
            vm.ActionEditor.DraftError.Should().Contain("at least 128 × 96 pixels");
            vm.ActionEditor.Apply().Should().BeFalse();

            var larger = new Rectangle(40, 50, 128, 96);
            vm.RegionPrompter = () => larger;
            vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
            vm.ActionEditor.Apply().Should().BeTrue();
            var wait = (WaitUntilStep)vm.GetStepAt(0)!;
            var image = (ImageCondition)wait.Condition;
            image.TemplatePath.Should().Be(path);
            image.TemplateBytes.Should().BeNull();
            image.SearchRegion.Should().Be(larger);
            wait.RepeatWhileVisible.Should().BeTrue();
            wait.RepeatDelayMs.Should().Be(950);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ImageSearchArea_CancelAfterSizeFailurePreservesArea_AndDefaultRemovesSizeGate()
    {
        var area = new Rectangle(20, 30, 9, 10);
        var source = new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(10, 10), searchRegion: area))
        { RepeatWhileVisible = true };
        using var vm = CreateImageSearchAreaEditor(source);
        vm.ActionEditor.Apply().Should().BeFalse();
        vm.RegionPrompter = () => null;
        vm.ActionEditor.ChooseImageSearchAreaCommand.Execute(null);
        vm.ActionEditor.ImageSearchRegion.Should().Be(area);
        vm.GetStepAt(0).Should().BeSameAs(source);

        vm.ActionEditor.ResetImageSearchAreaCommand.Execute(null);
        vm.ActionEditor.DraftError.Should().BeNull();
        vm.ActionEditor.Apply().Should().BeTrue();
        ((ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition).SearchRegion.Should().BeNull();
    }

    private static byte[] SearchAreaTemplatePng(int width, int height)
    {
        using var image = new Mat(height, width, MatType.CV_8UC4, new Scalar(30, 40, 50, 255));
        Cv2.ImEncode(".png", image, out byte[] bytes);
        return bytes;
    }
}
