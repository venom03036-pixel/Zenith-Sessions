using System.Drawing;

namespace MacroSuite.Core.Capture;

/// <summary>
/// Combines multiple screen regions into a minimal bounding union for centralized capture.
/// Helps subscribers share a single captured frame without redundant OS capture operations (Rule 5, Rule 7).
/// </summary>
public static class RegionMerger
{
    /// <summary>
    /// Computes the minimal bounding rectangle that encompasses all specified regions.
    /// Returns <see cref="Rectangle.Empty"/> if <paramref name="regions"/> is empty.
    /// </summary>
    public static Rectangle ComputeBoundingUnion(ReadOnlySpan<Rectangle> regions)
    {
        if (regions.IsEmpty)
        {
            return Rectangle.Empty;
        }

        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;
        bool hasNonEmpty = false;

        for (int i = 0; i < regions.Length; i++)
        {
            ref readonly var rect = ref regions[i];
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            hasNonEmpty = true;
            if (rect.Left < minX) minX = rect.Left;
            if (rect.Top < minY) minY = rect.Top;
            if (rect.Right > maxX) maxX = rect.Right;
            if (rect.Bottom > maxY) maxY = rect.Bottom;
        }

        if (!hasNonEmpty)
        {
            return Rectangle.Empty;
        }

        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Computes the minimal bounding rectangle that encompasses all specified regions.
    /// </summary>
    public static Rectangle ComputeBoundingUnion(IEnumerable<Rectangle> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);

        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;
        bool hasNonEmpty = false;

        foreach (var rect in regions)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            hasNonEmpty = true;
            if (rect.Left < minX) minX = rect.Left;
            if (rect.Top < minY) minY = rect.Top;
            if (rect.Right > maxX) maxX = rect.Right;
            if (rect.Bottom > maxY) maxY = rect.Bottom;
        }

        if (!hasNonEmpty)
        {
            return Rectangle.Empty;
        }

        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Translates a region in global screen coordinates to relative coordinates inside a captured bounding frame.
    /// </summary>
    public static Rectangle ToRelative(in Rectangle globalRegion, in Rectangle boundingUnion)
    {
        return new Rectangle(
            globalRegion.X - boundingUnion.X,
            globalRegion.Y - boundingUnion.Y,
            globalRegion.Width,
            globalRegion.Height);
    }
}
