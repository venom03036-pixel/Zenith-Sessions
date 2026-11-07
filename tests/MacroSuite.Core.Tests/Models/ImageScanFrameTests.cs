using System.Buffers;
using System.Drawing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Models;

public sealed class ImageScanFrameTests
{
    [Theory]
    [InlineData(3840,2160,720,1280,720)]
    [InlineData(3840,2160,1080,1920,1080)]
    [InlineData(3840,2160,480,853,480)]
    [InlineData(2160,3840,720,405,720)]
    [InlineData(7680,2160,720,1280,360)]
    [InlineData(300,80,720,300,80)]
    [InlineData(3841,2161,0,3841,2161)]
    public void FitPreservesAspectAndNeverUpscales(int width, int height, int resolution, int expectedWidth, int expectedHeight)
        => Assert.Equal(new Size(expectedWidth,expectedHeight), ImageScanFrame.Fit(new(width,height),(ImageScanResolution)resolution));

    [Fact]
    public void MappingUsesPhysicalNegativeDesktopOriginAndRoundsOutward()
    {
        using var frame = new ImageScanFrame(new Rectangle(-3840,-100,3840,2160),new Size(1280,720),5120,
            FramePixelFormat.Bgra32,MemoryPool<byte>.Shared.Rent(5120*720),123,7);
        Assert.Equal(new Rectangle(-3540,500,90,60),frame.ToPhysical(new Rectangle(100,200,30,20)));
        Assert.Equal(123,frame.TimestampTicks); Assert.Equal(7,frame.Generation);
        frame.Dispose(); Assert.True(frame.Memory.IsEmpty);
    }

    [Fact]
    public void BudgetReducesEffectsFirstThenBacksOffAndRecoversGradually()
    {
        var budget = new ImageScanBudget();
        budget.Observe(9,null,10,Size.Empty);
        Assert.True(budget.Snapshot.IsThrottled); Assert.Equal(10,budget.EffectiveInterval(10));
        budget.Observe(9,null,10,Size.Empty);
        int slow = budget.EffectiveInterval(10); Assert.True(slow>10);
        budget.Observe(.5,null,10,Size.Empty);
        Assert.InRange(budget.EffectiveInterval(10),10,slow-1);
        Assert.Null(budget.Snapshot.GpuPercent);
        budget.Enabled=false; Assert.Equal(10,budget.EffectiveInterval(10));
    }

    [Fact]
    public void ResourceSamplePreservesMeasuredCadenceWithoutTreatingItAsSchedulingMinimum()
    {
        var budget = new ImageScanBudget();
        var id = Guid.NewGuid();
        budget.RecordScanInterval(id, 10, 175.5);
        budget.Observe(1, null, 10, new Size(1280, 720));
        Assert.Equal(10, budget.EffectiveInterval(10));
        Assert.Equal(175.5, budget.Snapshot.MeasuredIntervalMs);
        Assert.Equal(id, budget.Snapshot.MeasuredActionId);
        Assert.Equal(10, budget.Snapshot.MeasuredRequestedIntervalMs);
    }
}
