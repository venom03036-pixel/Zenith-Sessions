using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class MacroEditorViewModelTests
{
    [Fact]
    public void ImageMonitor_MissingSavedDisplayIsExplicitAndUnrelatedEditPreservesPhysicalBounds()
    {
        var savedBounds = new Rectangle(-100_000, -50_000, 1280, 720);
        var source = new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(40, 20))
        {
            ScanOptions = new ImageScanOptions
            {
                SearchScope = ImageSearchScope.Anywhere,
                MonitorBounds = savedBounds,
                Resolution = ImageScanResolution.P720
            }
        });
        using var vm = CreateImageSearchAreaEditor(source);

        vm.ActionEditor.ImageMonitorIndex.Should().BeGreaterThan(0);
        vm.ActionEditor.ImageMonitorNames[vm.ActionEditor.ImageMonitorIndex].Should()
            .Be("Saved display area (display changed) · 1280 × 720 at (-100000, -50000)");
        vm.ActionEditor.ImageSearchAreaSummary.Should().Contain("display changed")
            .And.Contain("1280 × 720").And.Contain("(-100000, -50000)");
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
        vm.GetStepAt(0).Should().BeSameAs(source);

        vm.ActionEditor.ImageResolutionIndex = 1;
        vm.ActionEditor.Apply().Should().BeTrue();
        var appliedImage = (ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition;
        appliedImage.ScanOptions!.MonitorBounds.Should().Be(savedBounds);
        appliedImage.ScanOptions.Resolution.Should().Be(ImageScanResolution.P480);
        appliedImage.SearchRegion.Should().BeNull();
        vm.ActionEditor.ImageMonitorIndex.Should().BeGreaterThan(0);
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();
    }

    [Fact]
    public void ImageMonitor_AllDisplaysRequiresExplicitEditAndRevertRestoresSavedDisplayEntry()
    {
        var savedBounds = new Rectangle(-100_000, -50_000, 1280, 720);
        var source = new WaitUntilStep(new ImageCondition(SearchAreaTemplatePng(40, 20))
        {
            ScanOptions = new ImageScanOptions { SearchScope = ImageSearchScope.Anywhere, MonitorBounds = savedBounds }
        });
        using var vm = CreateImageSearchAreaEditor(source);
        int choicesWithSavedArea = vm.ActionEditor.ImageMonitorNames.Count;

        vm.ActionEditor.ImageMonitorIndex = 0;
        vm.ActionEditor.ImageMonitorNames.Count.Should().Be(choicesWithSavedArea - 1);
        vm.ActionEditor.ImageSearchAreaSummary.Should().Be("Anywhere on all connected displays");
        vm.ActionEditor.HasDraftChanges.Should().BeTrue();
        ((ImageCondition)source.Condition).ScanOptions!.MonitorBounds.Should().Be(savedBounds);

        vm.ActionEditor.CancelEdits();
        vm.ActionEditor.ImageMonitorNames.Count.Should().Be(choicesWithSavedArea);
        vm.ActionEditor.ImageMonitorIndex.Should().BeGreaterThan(0);
        vm.ActionEditor.HasDraftChanges.Should().BeFalse();

        vm.ActionEditor.ImageMonitorIndex = 0;
        vm.ActionEditor.Apply().Should().BeTrue();
        ((ImageCondition)((WaitUntilStep)vm.GetStepAt(0)!).Condition).ScanOptions!.MonitorBounds.Should().BeNull();
        vm.ActionEditor.ImageMonitorIndex.Should().Be(0);
        vm.ActionEditor.ImageMonitorNames.Should().NotContain(name => name.StartsWith("Saved display area"));
    }
}
