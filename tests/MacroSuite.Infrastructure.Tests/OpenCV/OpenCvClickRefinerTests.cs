using System.Drawing;
using FluentAssertions;
using MacroSuite.Infrastructure.OpenCV;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.OpenCV;

public sealed class OpenCvClickRefinerTests
{
    [Fact]
    public void BuildRoi_ClampsToAllowedRegion()
    {
        var allowed = new Rectangle(0, 0, 200, 100);
        Rectangle roi = OpenCvClickRefiner.BuildRoi(10, 10, allowed, radius: 60);

        roi.X.Should().Be(0);
        roi.Y.Should().Be(0);
        roi.Width.Should().Be(70);
        roi.Height.Should().Be(70);
    }

    [Fact]
    public void BuildRoi_InteriorStaysSquare()
    {
        var allowed = new Rectangle(0, 0, 1000, 1000);
        Rectangle roi = OpenCvClickRefiner.BuildRoi(400, 400, allowed, 60);

        roi.Should().Be(new Rectangle(340, 340, 120, 120));
    }
}
