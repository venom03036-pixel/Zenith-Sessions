using System.Drawing;
using FluentAssertions;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void TargetImage_CancelledCaptureOrImport_DoesNotCreateDraftOrUndo()
    {
        var source = new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(40, 20)));
        using var vm = CreateImageSearchAreaEditor(source);
        vm.ImageImportPrompter = _ => null;
        vm.ImageCaptureOnlyPrompter = _ => null;
        vm.ActionEditor.ImportImageTemplateCommand.Execute(null);
        vm.ActionEditor.CaptureTargetImageCommand.Execute(null);
        vm.GetStepAt(0).Should().BeSameAs(source);
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.ActionEditor.ImageTemplateDimensions.Should().Be("40 × 20 physical pixels");
        // The fixture already inserted a delay and replaced it with the image step.
        // Cancelling either picker must leave exactly those two undo operations.
        vm.UndoEditCommand.Execute(null);
        vm.GetStepAt(0).Should().NotBeSameAs(source);
        vm.UndoEditCommand.Execute(null);
        vm.Steps.Should().BeEmpty();
        vm.UndoEditCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void TargetImage_ImportedImageAndPhysicalAreaStayDraftUntilApply()
    {
        string path = Path.Combine(Path.GetTempPath(), $"zenith-image-import-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, SearchAreaTemplatePng(128, 96));
        try
        {
            var source = new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(40, 20)));
            using var vm = CreateImageSearchAreaEditor(source);
            var placed = new Rectangle(-300, 180, 128, 96);
            vm.ImageImportPrompter = confidence => new ImageTemplateSelection(path, confidence, false, null, placed, placed.Size);
            vm.ActionEditor.ImportImageTemplateCommand.Execute(null);
            vm.GetStepAt(0).Should().BeSameAs(source);
            vm.ActionEditor.HasDraftChanges.Should().BeTrue();
            vm.ActionEditor.ImageTemplateDimensions.Should().Be("128 × 96 physical pixels");
            vm.ActionEditor.ImageSearchRegion.Should().Be(placed);
            vm.ActionEditor.Apply().Should().BeTrue();
            var image = (ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition;
            image.TemplatePath.Should().Be(path);
            image.SearchRegion.Should().Be(placed);
            image.ScanOptions!.SearchScope.Should().Be(ImageSearchScope.PlacedArea);
            image.ScanOptions.Resolution.Should().Be(ImageScanResolution.Original);
            vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TargetImage_ConfidenceCanBeEditedRestoredAndRevertedWithoutFalseDirty()
    {
        var source = new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(40, 20), .83));
        using var vm = CreateImageSearchAreaEditor(source);
        vm.ActionEditor.ImageConfidencePercent.Should().Be(83);
        vm.ActionEditor.ImageConfidencePercent = 94;
        vm.ActionEditor.HasDraftChanges.Should().BeTrue();
        vm.GetStepAt(0).Should().BeSameAs(source);
        vm.ActionEditor.ImageConfidencePercent = 83;
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.ActionEditor.ImageConfidencePercent = 91;
        vm.ActionEditor.CancelEdits();
        vm.ActionEditor.ImageConfidencePercent.Should().Be(83);
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.ActionEditor.ImageConfidencePercent = 91;
        vm.ActionEditor.Apply().Should().BeTrue();
        ((ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition).MinConfidence.Should().Be(.91);
    }

    [Fact]
    public void TargetImage_ModeSelectionIsOneDraftChangeAndCanBeReverted()
    {
        var source = new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(40, 20)));
        using var vm = CreateImageSearchAreaEditor(source);
        vm.ActionEditor.ImageDetectionModeIndex.Should().Be(0);
        vm.ActionEditor.ImageDetectionModeIndex = 1;
        vm.ActionEditor.HasDraftChanges.Should().BeTrue();
        vm.ActionEditor.RepeatImageWhileVisible.Should().BeTrue();
        vm.ActionEditor.MatchAllImageOccurrences.Should().BeTrue();
        vm.GetStepAt(0).Should().BeSameAs(source);
        vm.ActionEditor.CancelEdits();
        vm.ActionEditor.ImageDetectionModeIndex.Should().Be(0);
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.ActionEditor.ImageDetectionModeIndex = 1;
        vm.ActionEditor.Apply().Should().BeTrue();
        ((WaitUntilStep)vm.GetStepAt(0)!).RepeatWhileVisible.Should().BeTrue();
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
    }
}
