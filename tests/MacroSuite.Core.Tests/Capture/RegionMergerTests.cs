using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Capture;
using Xunit;

namespace MacroSuite.Core.Tests.Capture;

public sealed class RegionMergerTests
{
    [Fact]
    public void ComputeBoundingUnion_EmptySpan_ReturnsEmpty()
    {
        Rectangle union = RegionMerger.ComputeBoundingUnion(ReadOnlySpan<Rectangle>.Empty);
        union.Should().Be(Rectangle.Empty);
    }

    [Fact]
    public void ComputeBoundingUnion_SingleRect_ReturnsSameRect()
    {
        var rect = new Rectangle(100, 200, 300, 400);
        Rectangle union = RegionMerger.ComputeBoundingUnion(new[] { rect }.AsSpan());

        union.Should().Be(rect);
    }

    [Fact]
    public void ComputeBoundingUnion_MultipleDisjointRects_ReturnsCorrectUnion()
    {
        var r1 = new Rectangle(10, 20, 50, 60);   // Right = 60, Bottom = 80
        var r2 = new Rectangle(100, 150, 40, 50); // Left = 100, Top = 150, Right = 140, Bottom = 200

        Rectangle union = RegionMerger.ComputeBoundingUnion(new[] { r1, r2 }.AsSpan());

        union.X.Should().Be(10);
        union.Y.Should().Be(20);
        union.Width.Should().Be(130); // 140 - 10
        union.Height.Should().Be(180); // 200 - 20
    }

    [Fact]
    public void ComputeBoundingUnion_SkipsZeroDimensionRectangles()
    {
        var r1 = new Rectangle(10, 10, 0, 50);
        var r2 = new Rectangle(50, 50, 20, 0);
        var valid = new Rectangle(100, 100, 50, 50);

        Rectangle union = RegionMerger.ComputeBoundingUnion(new[] { r1, r2, valid }.AsSpan());

        union.Should().Be(valid);
    }

    [Fact]
    public void ToRelative_ComputesOffsetCorrectly()
    {
        var union = new Rectangle(100, 200, 500, 400);
        var subRegion = new Rectangle(150, 250, 60, 70);

        var relative = RegionMerger.ToRelative(subRegion, union);

        relative.X.Should().Be(50);
        relative.Y.Should().Be(50);
        relative.Width.Should().Be(60);
        relative.Height.Should().Be(70);
    }
}
