using System.Drawing;
using FluentAssertions;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Hardening;

public class DpiCoordinateTransformTests
{
    public static Rectangle ConvertDipToPhysical(double screenX, double screenY, double dipWidth, double dipHeight, double dpiScaleX, double dpiScaleY)
    {
        int physicalX = (int)Math.Round(screenX);
        int physicalY = (int)Math.Round(screenY);
        int physicalW = Math.Max(1, (int)Math.Round(dipWidth * dpiScaleX));
        int physicalH = Math.Max(1, (int)Math.Round(dipHeight * dpiScaleY));

        return new Rectangle(physicalX, physicalY, physicalW, physicalH);
    }

    [Theory]
    [InlineData(1.0, 100, 200, 400, 300, 100, 200, 400, 300)]      // 100% DPI (standard 96 DPI)
    [InlineData(1.25, 100, 200, 400, 300, 100, 200, 500, 375)]     // 125% DPI (laptop displays)
    [InlineData(1.50, 100, 200, 400, 300, 100, 200, 600, 450)]     // 150% DPI (4K monitors)
    [InlineData(2.0, 0, 0, 500, 500, 0, 0, 1000, 1000)]            // 200% DPI (HiDPI / Retina)
    public void ConvertDipToPhysical_CalculatesExactPixelBounds(
        double scale,
        double inX, double inY, double inW, double inH,
        int expX, int expY, int expW, int expH)
    {
        var result = ConvertDipToPhysical(inX, inY, inW, inH, scale, scale);

        result.X.Should().Be(expX);
        result.Y.Should().Be(expY);
        result.Width.Should().Be(expW);
        result.Height.Should().Be(expH);
    }

    [Fact]
    public void MultiMonitor_NegativeVirtualScreenOffset_PreservesNegativeCoordinates()
    {
        // Monitor positioned to the left of the primary monitor: X = -1920
        double secondaryScreenX = -1920;
        double secondaryScreenY = 0;
        double dipWidth = 800;
        double dipHeight = 600;
        double scale = 1.25;

        var result = ConvertDipToPhysical(secondaryScreenX, secondaryScreenY, dipWidth, dipHeight, scale, scale);

        result.X.Should().Be(-1920);
        result.Y.Should().Be(0);
        result.Width.Should().Be(1000);
        result.Height.Should().Be(750);
    }

    [Fact]
    public void SubPixelSelection_ClampsToAtLeastOnePixel()
    {
        // Accidental 0.2px selection shouldn't create empty rectangle
        var result = ConvertDipToPhysical(100, 100, 0.2, 0.1, 1.0, 1.0);

        result.Width.Should().BeGreaterThanOrEqualTo(1);
        result.Height.Should().BeGreaterThanOrEqualTo(1);
    }
}
