using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Models;

public sealed class MacroActionStoreTests
{
    [Fact]
    public void AddRange_KeepsAuthoritativeCount()
    {
        var store = new MacroActionStore();
        var steps = Enumerable.Range(0, 10_000).Select(i => (MacroStep)new DelayStep(TimeSpan.FromMilliseconds(i % 50 + 1)));
        store.AddRange(steps);
        store.Count.Should().Be(10_000);
        store[0].Should().BeOfType<DelayStep>();
        store[9_999].Should().BeOfType<DelayStep>();
    }

    [Fact]
    public void SizePolicy_WarnsAtThresholds()
    {
        MacroSizePolicy.WarningForCount(49_999).Should().BeNull();
        MacroSizePolicy.WarningForCount(50_000).Should().Contain("50,000");
        MacroSizePolicy.WarningForCount(100_000).Should().Contain("100,000");
        MacroSizePolicy.WarningForCount(500_000).Should().Contain("500,000");
    }

    [Fact]
    public void TemplateCap_RejectsHugeRegions()
    {
        VisualMatchOptions.IsTemplateOversized(20, 20).Should().BeFalse();
        VisualMatchOptions.IsTemplateOversized(512, 512).Should().BeFalse();
        VisualMatchOptions.IsTemplateOversized(513, 1).Should().BeTrue();
        VisualMatchOptions.IsTemplateOversized(1920, 1080).Should().BeTrue();
    }

    [Fact]
    public void VisualWait_ToOptions_DefaultsToFixedPerceptual()
    {
        var step = new VisualWaitStep { SearchMode = "Fixed", Mode = "Pixel", R = 1, G = 2, B = 3 };
        var options = step.ToOptions();
        options.SearchScope.Should().Be(VisualSearchScope.Fixed);
        options.Algorithm.Should().Be(VisualColorAlgorithm.Perceptual);
        options.Kind.Should().Be(VisualMatchKind.Pixel);
    }
}
