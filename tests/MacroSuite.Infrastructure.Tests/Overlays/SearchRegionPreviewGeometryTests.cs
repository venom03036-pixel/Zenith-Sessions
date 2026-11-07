using System.Drawing;
using FluentAssertions;
using MacroSuite.App.Overlays;
using MacroSuite.Infrastructure.Windows;
using Xunit;
using WpfRect = System.Windows.Rect;

namespace MacroSuite.Infrastructure.Tests.Overlays;

public sealed class SearchRegionPreviewGeometryTests
{
    private static readonly Rectangle Desktop = new(-1920, -200, 3840, 1280);

    [Fact]
    public void ClipToDesktop_KeepsAnInsideRegionAtItsNegativePhysicalOrigin()
    {
        var region = new Rectangle(-1500, -100, 300, 200);
        SearchRegionPreviewGeometry.ClipToDesktop(region, Desktop).Should().Be(region);
    }

    [Fact]
    public void ClipToDesktop_ClipsPartialOverlapWithoutMovingTheVisiblePixels()
    {
        SearchRegionPreviewGeometry.ClipToDesktop(new Rectangle(-2000, -300, 200, 200), Desktop)
            .Should().Be(new Rectangle(-1920, -200, 120, 100));
        SearchRegionPreviewGeometry.ClipToDesktop(new Rectangle(1800, 1000, 200, 200), Desktop)
            .Should().Be(new Rectangle(1800, 1000, 120, 80));
        SearchRegionPreviewGeometry.ClipToDesktop(new Rectangle(-2000, -500, 4200, 2000), Desktop)
            .Should().Be(Desktop);
    }

    [Theory]
    [InlineData(1920, 0)]
    [InlineData(-1940, 0)]
    [InlineData(0, 1080)]
    [InlineData(0, -220)]
    [InlineData(3000, 3000)]
    public void ClipToDesktop_RejectsOutsideRegionsIncludingEdgeOnlyContact(int x, int y)
    {
        Action action = () => SearchRegionPreviewGeometry.ClipToDesktop(new Rectangle(x, y, 20, 20), Desktop);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToLocalDips_SubtractsNegativeMonitorOriginBeforeApplyingEachAxisScale()
    {
        var monitor = new Rectangle(-1920, -200, 1920, 1080);
        var region = new Rectangle(-1870, -125, 250, 300);

        WpfRect result = SearchRegionPreviewGeometry.ToLocalDips(region, monitor, 1.25, 1.5);

        result.Should().Be(new WpfRect(40, 50, 200, 200));
    }

    [Fact]
    public void ToLocalDips_CrossMonitorRegionKeepsItsActualEdgesAtMixedDpiWithoutAddingASeamBorder()
    {
        var region = new Rectangle(-100, 20, 300, 160);
        var leftMonitor = new Rectangle(-1920, 0, 1920, 1080);
        var rightMonitor = new Rectangle(0, 0, 2560, 1440);

        WpfRect left = SearchRegionPreviewGeometry.ToLocalDips(region, leftMonitor, 1.25, 1.25);
        WpfRect right = SearchRegionPreviewGeometry.ToLocalDips(region, rightMonitor, 1.5, 1.5);

        // The viewport clips the drawing; the search rectangle itself still crosses the seam.
        left.Right.Should().BeGreaterThan(leftMonitor.Width / 1.25);
        right.Left.Should().BeLessThan(0);
        AssertPhysicalEdges(left, leftMonitor, 1.25, 1.25, region);
        AssertPhysicalEdges(right, rightMonitor, 1.5, 1.5, region);
    }

    [Fact]
    public void ToLocalDips_DoesNotOverflowWhenMonitorAndRegionOriginsAreFarApart()
    {
        var monitor = new Rectangle(int.MinValue, int.MinValue, 1, 1);
        var region = new Rectangle(int.MaxValue - 10, int.MaxValue - 20, 10, 20);

        WpfRect result = SearchRegionPreviewGeometry.ToLocalDips(region, monitor, 1, 1);

        result.Should().Be(new WpfRect(4_294_967_285d, 4_294_967_275d, 10, 20));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(double.NegativeInfinity, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, double.NaN)]
    [InlineData(1, double.PositiveInfinity)]
    [InlineData(1, double.NegativeInfinity)]
    public void ToLocalDips_RejectsNonPositiveOrNonFiniteScale(double scaleX, double scaleY)
    {
        Action action = () => SearchRegionPreviewGeometry.ToLocalDips(new Rectangle(0, 0, 10, 10), Desktop, scaleX, scaleY);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, 0, -1, 1)]
    [InlineData(0, 0, 1, -1)]
    [InlineData(int.MaxValue, 0, 1, 1)]
    [InlineData(0, int.MaxValue, 1, 1)]
    [InlineData(int.MaxValue - 10, 0, 11, 1)]
    [InlineData(0, int.MaxValue - 10, 1, 11)]
    public void GeometryEntryPoints_RejectInvalidBoundsAndOverflow(int x, int y, int width, int height)
    {
        var invalid = new Rectangle(x, y, width, height);
        Action validate = () => Win32WindowPlacement.ValidateBounds(invalid);
        Action clipRegion = () => SearchRegionPreviewGeometry.ClipToDesktop(invalid, Desktop);
        Action clipDesktop = () => SearchRegionPreviewGeometry.ClipToDesktop(Desktop, invalid);
        Action localRegion = () => SearchRegionPreviewGeometry.ToLocalDips(invalid, Desktop, 1, 1);
        Action localMonitor = () => SearchRegionPreviewGeometry.ToLocalDips(Desktop, invalid, 1, 1);

        validate.Should().Throw<ArgumentOutOfRangeException>();
        clipRegion.Should().Throw<ArgumentOutOfRangeException>();
        clipDesktop.Should().Throw<ArgumentOutOfRangeException>();
        localRegion.Should().Throw<ArgumentOutOfRangeException>();
        localMonitor.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidateBounds_AcceptsNegativeOriginsAndExactlyRepresentableRightAndBottomEdges()
    {
        Action negative = () => Win32WindowPlacement.ValidateBounds(new Rectangle(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue));
        Action maximum = () => Win32WindowPlacement.ValidateBounds(new Rectangle(int.MaxValue - 10, int.MaxValue - 20, 10, 20));
        negative.Should().NotThrow();
        maximum.Should().NotThrow();
    }

    private static void AssertPhysicalEdges(WpfRect local, Rectangle monitor, double scaleX, double scaleY, Rectangle expected)
    {
        (monitor.X + local.Left * scaleX).Should().BeApproximately(expected.Left, 0.000001);
        (monitor.Y + local.Top * scaleY).Should().BeApproximately(expected.Top, 0.000001);
        (monitor.X + local.Right * scaleX).Should().BeApproximately(expected.Right, 0.000001);
        (monitor.Y + local.Bottom * scaleY).Should().BeApproximately(expected.Bottom, 0.000001);
    }
}
