using MacroSuite.App.ViewModels;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed class EditorActionAppearanceTests
{
    [Theory]
    [InlineData("Pixel", false, 0, 0, "Color")]
    [InlineData("Pixel", true, 1, 1, "Color")]
    [InlineData("Region", true, 1, 1, "Image")]
    [InlineData("region", true, 1, 1, "Image")]
    [InlineData("Region", false, 1, 1, "Color")]
    [InlineData("Region", true, 0, 1, "Color")]
    [InlineData("Region", true, 1, 0, "Color")]
    public void VisualWaitIcon_FollowsTheActualColorOrTemplateMatchingPath(
        string mode, bool hasTemplate, int width, int height, string expectedCategory)
    {
        var step = new VisualWaitStep
        {
            Mode = mode,
            TemplateBgra = hasTemplate ? new byte[] { 10, 20, 30, 255 } : null,
            TemplateWidth = width,
            TemplateHeight = height
        };

        var row = MacroStepItemViewModel.FromStep(step, 0);

        Assert.Equal(expectedCategory, row.VisualCategory);
    }
}
