using System.Drawing;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Overlays;

public sealed class ImageSearchAreaGeometryTests
{
    [Fact]
    public void ImportedTemplateCentersOnChosenCurrentDisplay_InPhysicalPixels()
    {
        var desktop = new Rectangle(-2160, -1447, 6000, 3840);
        var currentDisplay = new Rectangle(-2160, -1447, 2160, 3840);
        var size = new Size(241, 81);
        var box = ImageSearchAreaGeometry.CenterOnDisplay(size, currentDisplay, desktop);
        Assert.Equal(new Rectangle(-1201, 432, 241, 81), box);
        Assert.True(currentDisplay.Contains(box));
    }

    [Fact]
    public void ResetUsesExactTemplateSizeAndKeepsCurrentPhysicalLocation()
    {
        var desktop = new Rectangle(-1920, -200, 4480, 1640);
        var enlarged = new Rectangle(-80, 30, 420, 170);
        var reset = ImageSearchAreaGeometry.ResetToTemplateSize(enlarged, new Size(240, 80), desktop);
        Assert.Equal(new Rectangle(-80, 30, 240, 80), reset);
        // Value-based reset does not mutate the caller's saved/draft rectangle.
        Assert.Equal(new Size(420, 170), enlarged.Size);
    }

    [Fact]
    public void DefaultBoxExactlyMatchesPhysicalTemplateSize_AndMovesAcrossNegativeOrigin()
    {
        var desktop = new Rectangle(-1920, -200, 4480, 1640);
        var size = new Size(240, 80);
        var box = ImageSearchAreaGeometry.Place(null, size, desktop);
        Assert.Equal(size, box.Size);
        var moved = ImageSearchAreaGeometry.Move(box, -5000, -5000, size, desktop);
        Assert.Equal(new Rectangle(-1920, -200, 240, 80), moved);
    }
    [Fact]
    public void ResizeNeverShrinksBelowTemplate_AndNeverRunsPastDesktopEdge()
    {
        var desktop = new Rectangle(0, 0, 1920, 1080);
        var size = new Size(120, 60);
        var box = new Rectangle(1800, 1020, 120, 60);
        Assert.Equal(box, ImageSearchAreaGeometry.Resize(box, -1000, -1000, size, desktop));
        Assert.Equal(box, ImageSearchAreaGeometry.Resize(box, 1000, 1000, size, desktop));
    }
    [Fact]
    public void OversizedTemplateIsRejectedInsteadOfSilentlyClippingIt()
    {
        Assert.Throws<ArgumentException>(() => ImageSearchAreaGeometry.Place(null, new Size(1921, 20), new Rectangle(0, 0, 1920, 1080)));
    }

    [Fact]
    public void EnlargedPlacedAreaSurvivesSaveReload_AndKeepsPhysicalEdgesAcrossMixedDpiDisplays()
    {
        var desktop = new Rectangle(-1920, -200, 4480, 1640);
        var template = new Size(240, 80);
        var placed = ImageSearchAreaGeometry.Place(new Rectangle(-700, -100, 240, 80), template, desktop);
        var moved = ImageSearchAreaGeometry.Move(placed, 620, 130, template, desktop);
        var resized = ImageSearchAreaGeometry.Resize(moved, 180, 90, template, desktop);
        Assert.Equal(new Rectangle(-80, 30, 420, 170), resized);

        var serializer = new MacroJsonSerializer();
        var saved = new MacroModel
        {
            Steps = [new WaitUntilStep(new ImageCondition("target.png", searchRegion: resized)
            { ScanOptions = ImageScanOptions.ForPlacedArea() }) { RepeatWhileVisible = true }]
        };
        var restored = serializer.Deserialize(serializer.Serialize(saved));
        var image = Assert.IsType<ImageCondition>(Assert.IsType<WaitUntilStep>(restored.Steps[0]).Condition);
        Assert.Equal(resized, image.SearchRegion);
        Assert.Equal(ImageScanOptions.ForPlacedArea(), image.ScanOptions);
        Assert.Equal(resized, ImageSearchAreaGeometry.Place(image.SearchRegion, template, desktop));

        foreach (var (monitor, scale) in new[]
        {
            (new Rectangle(-1920, -200, 1920, 1080), 1.25),
            (new Rectangle(0, 0, 2560, 1440), 2.0)
        })
        {
            var drawing = SearchRegionPreviewGeometry.ToLocalDips(image.SearchRegion!.Value, monitor, scale, scale);
            Assert.Equal(resized.Left, monitor.Left + drawing.Left * scale, 7);
            Assert.Equal(resized.Top, monitor.Top + drawing.Top * scale, 7);
            Assert.Equal(resized.Right, monitor.Left + drawing.Right * scale, 7);
            Assert.Equal(resized.Bottom, monitor.Top + drawing.Bottom * scale, 7);
        }
    }

    [Fact]
    public void LargerReplacementTemplateExpandsAndRepositionsAnEdgeAreaWithoutClipping()
    {
        var previousArea = new Rectangle(1800, 1030, 120, 50);
        var replacement = new Size(280, 100);
        var restored = ImageSearchAreaGeometry.Place(previousArea, replacement, new Rectangle(0, 0, 1920, 1080));
        Assert.Equal(new Rectangle(1640, 980, 280, 100), restored);
    }
}
