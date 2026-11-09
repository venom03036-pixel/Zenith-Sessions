using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

public sealed class WindowTilePanelTests
{
    public static IEnumerable<object[]> Viewports()
    {
        foreach (int count in new[] { 0, 1, 4, 16, 128 })
            foreach (Size size in new[] { new Size(690, 350), new Size(1040, 580), new Size(1800, 1100) })
                yield return [count, size.Width, size.Height];
    }
    [Theory]
    [MemberData(nameof(Viewports))]
    public Task EveryTileFitsTheFiniteViewportWithEqualNonoverlappingCells(int count, double width, double height) => OnSta(() =>
    {
        var panel = new WindowTilePanel();
        for (int index = 0; index < count; index++) panel.Children.Add(new Border { Child = new TextBlock { Text = $"Observed client {index}" } });
        panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout();
        var bounds = panel.Children.Cast<UIElement>().Select(child => child.TransformToAncestor(panel).TransformBounds(new Rect(child.RenderSize))).ToArray();
        Assert.Equal(count, bounds.Length);
        foreach (Rect current in bounds)
        {
            Assert.True(current.Width > 0 && current.Height > 0);
            Assert.InRange(current.Left, 0, width); Assert.InRange(current.Top, 0, height);
            Assert.InRange(current.Right, 0, width + .001); Assert.InRange(current.Bottom, 0, height + .001);
            Assert.InRange(Math.Abs(current.Width - bounds[0].Width), 0, .001); Assert.InRange(Math.Abs(current.Height - bounds[0].Height), 0, .001);
        }
        for (int a = 0; a < bounds.Length; a++) for (int b = a + 1; b < bounds.Length; b++) Assert.False(bounds[a].IntersectsWith(bounds[b]));
    });

    [Fact]
    public Task WideShallowViewportUsesLargerPicturesInsteadOfMostlyEmptyRows() => OnSta(() =>
    {
        var panel = new WindowTilePanel();
        for (int i = 0; i < 16; i++) panel.Children.Add(new Border());
        panel.Measure(new Size(1085, 395)); panel.Arrange(new Rect(0, 0, 1085, 395));
        Size cell = panel.Children[0].RenderSize;
        double pictureWidth = Math.Min(cell.Width, Math.Max(0, cell.Height - 28) * 16 / 9);
        Assert.True(pictureWidth >= 170, "The 16-window overview must use the available picture area rather than a mostly empty fourth row.");
    });

    [Fact]
    public Task ResizeAndRosterChangesRearrangeTheSameChildrenWithoutRecreatingThem() => OnSta(() =>
    {
        var panel = new WindowTilePanel(); var children = Enumerable.Range(0, 16).Select(_ => new Border()).ToArray();
        foreach (var child in children) panel.Children.Add(child);
        foreach (Size size in new[] { new Size(900, 500), new Size(500, 900), new Size(1100, 640) })
        {
            panel.Measure(size); panel.Arrange(new Rect(new Point(), size)); panel.UpdateLayout();
            Assert.Equal(children, panel.Children.Cast<Border>());
            Assert.All(children, child => Assert.True(child.RenderSize.Width > 0 && child.RenderSize.Height > 0));
        }
        panel.Children.Remove(children[^1]); panel.Measure(new Size(900, 500)); panel.Arrange(new Rect(0, 0, 900, 500));
        Assert.Equal(15, panel.Children.Count); Assert.Same(children[0], panel.Children[0]);
    });

    [Fact]
    public Task ZeroViewportAndInvalidSpacingCannotCreateUnboundedGeometry() => OnSta(() =>
    {
        var panel = new WindowTilePanel(); panel.Children.Add(new Border()); panel.Measure(new Size()); panel.Arrange(new Rect());
        Assert.Equal(new Size(), panel.Children[0].RenderSize);
        Assert.Throws<ArgumentException>(() => panel.Gap = double.NaN); Assert.Throws<ArgumentException>(() => panel.Gap = 33);
    });
    private static Task OnSta(Action action)
        => PackagedLayoutCheck.RunOnStaAsync(() => new object(), _ => { action(); return Task.CompletedTask; },
            _ => { }, CancellationToken.None);
}
