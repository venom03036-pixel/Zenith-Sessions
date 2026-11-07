using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Coordinates;
using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Windows;

public sealed class Win32VirtualDesktopGeometryTests
{
    [Fact]
    public void Query_ReturnsPositiveVirtualDesktopAndAtLeastOneMonitor()
    {
        var geometry = Win32VirtualDesktopGeometry.Query();
        geometry.VirtualDesktopBounds.Width.Should().BeGreaterThan(0);
        geometry.VirtualDesktopBounds.Height.Should().BeGreaterThan(0);
        geometry.Monitors.Should().NotBeEmpty();

        var mapper = geometry.CreateMapper();
        mapper.VirtualDesktopBounds.Should().Be(geometry.VirtualDesktopBounds);
        mapper.ClampCaptureRegion(new Rectangle(
                geometry.VirtualDesktopBounds.X - 40,
                geometry.VirtualDesktopBounds.Y,
                80,
                20))
            .Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Mapper_PreservesNegativeOriginWhenPresent()
    {
        var geometry = Win32VirtualDesktopGeometry.Query();
        var mapper = geometry.CreateMapper();
        Point origin = new(geometry.VirtualDesktopBounds.X, geometry.VirtualDesktopBounds.Y);
        mapper.CaptureToScreenPoint(origin).Should().Be(origin);
        if (geometry.VirtualDesktopBounds.X < 0)
        {
            mapper.ClampCaptureRegion(new Rectangle(geometry.VirtualDesktopBounds.X, geometry.VirtualDesktopBounds.Y, 10, 10))
                .X.Should().Be(geometry.VirtualDesktopBounds.X);
        }
    }
}
