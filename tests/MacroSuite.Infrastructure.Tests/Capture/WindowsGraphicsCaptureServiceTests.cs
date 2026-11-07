using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Capture;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Capture;

public sealed class WindowsGraphicsCaptureServiceTests
{
    [Fact]
    public void IsSupported_DoesNotThrow_AndReportsDeviceAvailability()
    {
        using var service = new WindowsGraphicsCaptureService();
        bool supported = service.IsSupported;
        service.CurrentSource.Should().Be(CaptureSource.Dxgi);
        supported.Should().Be(service.IsSupported);
    }

    [Fact]
    public async Task CaptureRegionAsync_WhenUnsupported_ThrowsPlatformException()
    {
        using var service = new WindowsGraphicsCaptureService();
        if (service.IsSupported)
            return;

        var act = () => service.CaptureRegionAsync(new Rectangle(0, 0, 8, 8));
        await act.Should().ThrowAsync<PlatformNotSupportedException>();
    }

    [Fact]
    public async Task CaptureRegionAsync_WhenSupported_ReturnsPopulatedBgra()
    {
        using var service = new WindowsGraphicsCaptureService();
        if (!service.IsSupported)
            return;

        using CaptureFrame frame = await service.CaptureRegionAsync(new Rectangle(0, 0, 8, 8));
        frame.Source.Should().Be(CaptureSource.Dxgi);
        frame.Format.Should().Be(FramePixelFormat.Bgra32);
        CaptureFrameIntegrity.IsUnpopulated(frame).Should().BeFalse();
        frame.Width.Should().BeGreaterThan(0);
        frame.Height.Should().BeGreaterThan(0);
    }
}
