using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Capture;

public sealed class CaptureFrameIntegrityTests
{
    [Fact]
    public void DisposedFrame_FailsClosed()
    {
        var frame = TestFrameFactory.CreateUnpopulatedDxgi(4, 4);
        frame.Dispose();
        CaptureFrameIntegrity.IsUnpopulated(frame).Should().BeTrue();
        var act = () => CaptureFrameIntegrity.ThrowIfUnpopulated(frame);
        act.Should().Throw<InvalidOperationException>().WithMessage("*unpopulated*");
    }

    [Fact]
    public void ZeroedDxgiBuffer_IsValidBlackFrame()
    {
        using var frame = TestFrameFactory.CreateUnpopulatedDxgi(4, 4);
        CaptureFrameIntegrity.IsUnpopulated(frame).Should().BeFalse();
    }

    [Fact]
    public void PopulatedDxgiWithOpaqueAlpha_IsNotEmpty()
    {
        using var frame = TestFrameFactory.CreateSolidColor(4, 4, 0, 0, 0, source: CaptureSource.Dxgi);
        CaptureFrameIntegrity.IsUnpopulated(frame).Should().BeFalse();
    }
}
