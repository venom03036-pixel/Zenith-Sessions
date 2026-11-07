using System.Windows;
using System.Windows.Controls;

namespace MacroSuite.App.Common;

/// <summary>Content-sized cards in two equal columns, stacked when the page is narrow.</summary>
public sealed class ResponsiveColumnsPanel : Panel
{
    public static readonly DependencyProperty ColumnThresholdProperty = DependencyProperty.Register(
        nameof(ColumnThreshold), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(760d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double ColumnThreshold { get => (double)GetValue(ColumnThresholdProperty); set => SetValue(ColumnThresholdProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = availableSize.Width >= ColumnThreshold ? 2 : 1;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : ColumnThreshold;
        double cellWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        double height = 0;
        for (int i = 0; i < InternalChildren.Count; i += columns)
        {
            double rowHeight = 0;
            for (int column = 0; column < columns && i + column < InternalChildren.Count; column++)
            {
                var child = InternalChildren[i + column];
                child.Measure(new Size(cellWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight + (i > 0 ? Gap : 0);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = finalSize.Width >= ColumnThreshold ? 2 : 1;
        double cellWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        double top = 0;
        for (int i = 0; i < InternalChildren.Count; i += columns)
        {
            double rowHeight = 0;
            for (int column = 0; column < columns && i + column < InternalChildren.Count; column++)
            {
                var child = InternalChildren[i + column];
                child.Arrange(new Rect(column * (cellWidth + Gap), top, cellWidth, child.DesiredSize.Height));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            top += rowHeight + Gap;
        }
        return finalSize;
    }
}
