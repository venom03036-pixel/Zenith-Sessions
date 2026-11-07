using System.Windows;
using System.Windows.Controls;

namespace Zenith.Sessions.App;

/// <summary>Finite overview only: all bounded observed tiles receive equal, non-overlapping cells.</summary>
public sealed class WindowTilePanel : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(WindowTilePanel),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        value => value is double gap && double.IsFinite(gap) && gap is >= 0 and <= 32);
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size viewport = Finite(availableSize);
        var grid = Geometry(InternalChildren.Count, viewport, Gap);
        foreach (UIElement child in InternalChildren) child.Measure(grid.Cell);
        return viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var grid = Geometry(InternalChildren.Count, Finite(finalSize), Gap);
        for (int index = 0; index < InternalChildren.Count; index++)
            InternalChildren[index].Arrange(new Rect((index % grid.Columns) * (grid.Cell.Width + grid.Gap),
                (index / grid.Columns) * (grid.Cell.Height + grid.Gap), grid.Cell.Width, grid.Cell.Height));
        return Finite(finalSize);
    }
    private Size Finite(Size size) => new(double.IsFinite(size.Width) ? size.Width : Math.Max(0, ActualWidth),
        double.IsFinite(size.Height) ? size.Height : Math.Max(0, ActualHeight));
    private static (int Columns, Size Cell, double Gap) Geometry(int count, Size size, double requestedGap)
    {
        if (count <= 0 || size.Width <= 0 || size.Height <= 0) return (1, new Size(), 0);
        // Use the largest actual 16:9 picture after reserving the unchanged caption height.
        // Rounding a viewport aspect ratio alone can add a mostly empty row and make every
        // picture smaller. Enumerating the bounded roster needs no allocation or new owner.
        int columns = 1, spare = int.MaxValue;
        double bestArea = -1, chosenGap = 0;
        Size chosenCell = new();
        for (int candidate = 1; candidate <= count; candidate++)
        {
            int rows = (count + candidate - 1) / candidate;
            double gap = Math.Min(requestedGap, Math.Min(size.Width / (candidate * 2), size.Height / (rows * 2)));
            var cell = new Size(Math.Max(0, (size.Width - gap * (candidate - 1)) / candidate),
                Math.Max(0, (size.Height - gap * (rows - 1)) / rows));
            double pictureWidth = Math.Min(cell.Width, Math.Max(0, cell.Height - 28) * 16 / 9);
            double area = pictureWidth * pictureWidth * 9 / 16;
            int empty = candidate * rows - count;
            if (area > bestArea + .001 || Math.Abs(area - bestArea) <= .001 && empty < spare)
            { columns = candidate; spare = empty; bestArea = area; chosenGap = gap; chosenCell = cell; }
        }
        return (columns, chosenCell, chosenGap);
    }
}
