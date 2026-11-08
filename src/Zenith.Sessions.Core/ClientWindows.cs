namespace Zenith.Sessions.Core;

public enum ClientWindowAction { Minimize, Restore }
/// <summary>
/// What Zenith does with client windows when a client first reports Connected in its log. None (the default) leaves windows
/// alone; Arrange tiles every connected client that is not minimized or hidden on the saved monitor; Minimize minimizes the
/// client that connected.
/// </summary>
public enum ClientArrivalWindowAction { None, Arrange, Minimize }
public sealed record WindowActionResult(bool Applied,string Explanation);

public interface IClientWindowControl
{
    Task<WindowActionResult> ChangeAsync(SessionObservation session,ClientWindowAction action,CancellationToken ct);
}

/// <summary>Explicit selected-client state changes only; never restores saved session state or starts automation.</summary>
public interface IClientWindowBatchControl : IClientWindowControl
{
    Task<ClientWindowArrangementResult> ChangeManyAsync(IReadOnlyList<SessionObservation> sessions, ClientWindowAction action, CancellationToken ct);
    void CancelWindowOperations();
}

public sealed record ClientDisplay(string Id, string Name, PixelRectangle WorkArea, int ScalePercent, bool IsPrimary)
{
    // Windows monitor scale expressed as effective DPI; this is not the panel's raw physical DPI.
    public double EffectiveDpi => ScalePercent * 96d / 100d;
    public bool IsValid => !string.IsNullOrWhiteSpace(Id) && Id.Length <= 100 && !Id.Any(char.IsControl)
        && WorkArea.IsValid && ScalePercent is >= 100 and <= 500;
}
public sealed record ClientDisplayList(IReadOnlyList<ClientDisplay> Displays, string Explanation);
public sealed record ClientWindowTileOptions(int GapDip = 8, int MinimumWidthDip = 320, int MinimumHeightDip = 240, int? Columns = null)
{
    public ClientWindowTileOptions Validated()
    {
        if (GapDip is < 0 or > 64 || MinimumWidthDip is < 160 or > 8192 || MinimumHeightDip is < 120 or > 8192
            || Columns is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(ClientWindowTileOptions));
        return this;
    }
}
public sealed record ClientWindowTilePlan(bool Fits, IReadOnlyList<PixelRectangle> Cells, string Explanation);
public enum ClientWindowPlacementStatus { Applied, InvalidClient, UnsupportedState, DisplayUnavailable, LayoutUnavailable, Denied, NotConfirmed, Cancelled }
public sealed record ClientWindowPlacementResult(SessionObservation Session, PixelRectangle? RequestedBounds,
    ClientWindowPlacementStatus Status, string Explanation);
public sealed record ClientWindowArrangementResult(IReadOnlyList<ClientWindowPlacementResult> Results, bool Cancelled, string Explanation)
{
    public bool AllApplied => Results.Count > 0 && Results.All(result => result.Status == ClientWindowPlacementStatus.Applied);
}
public interface IClientWindowArranger
{
    Task<ClientDisplayList> GetDisplaysAsync(CancellationToken ct);
    /// <summary>Tiles the clients in the order given (see <see cref="ClientWindowSlotOrder"/>): the first client gets the first cell.</summary>
    Task<ClientWindowArrangementResult> ArrangeAsync(IReadOnlyList<SessionObservation> sessions, string displayId,
        ClientWindowTileOptions options, CancellationToken ct);
    void CancelWindowOperations();
}

/// <summary>Pure bounded tiling; no native calls. Scaled gaps/minima, outer margins and all cells remain in the chosen workarea.</summary>
public static class ClientWindowTilePlanner
{
    public static ClientWindowTilePlan Plan(ClientDisplay display, int count, ClientWindowTileOptions options)
    {
        options.Validated();
        if (!display.IsValid || count is < 1 or > 128) return new(false, Array.Empty<PixelRectangle>(), "Choose a current display and between 1 and 128 clients.");
        int gap = Scale(options.GapDip, display.ScalePercent), minWidth = Scale(options.MinimumWidthDip, display.ScalePercent),
            minHeight = Scale(options.MinimumHeightDip, display.ScalePercent);
        int bestColumns = 0; double bestScore = double.MaxValue;
        for (int columns = 1; columns <= count; columns++)
        {
            if (options.Columns is { } selected && selected != columns) continue;
            int rows = (count + columns - 1) / columns;
            long availableWidth = (long)display.WorkArea.Width - (long)gap * (columns + 1);
            long availableHeight = (long)display.WorkArea.Height - (long)gap * (rows + 1);
            if (availableWidth / columns < minWidth || availableHeight / rows < minHeight) continue;
            double aspect = (availableWidth / (double)columns) / (availableHeight / (double)rows);
            double score = Math.Abs(Math.Log(aspect / (16d / 9d))) + (columns * rows - count) * .01;
            if (score < bestScore) { bestScore = score; bestColumns = columns; }
        }
        if (bestColumns == 0) return new(false, Array.Empty<PixelRectangle>(), "The selected display cannot fit every client at the chosen minimum size and gap. Select fewer clients or change the layout.");
        int rowCount = (count + bestColumns - 1) / bestColumns;
        long width = (long)display.WorkArea.Width - (long)gap * (bestColumns + 1), height = (long)display.WorkArea.Height - (long)gap * (rowCount + 1);
        long baseWidth = width / bestColumns, baseHeight = height / rowCount, extraWidth = width % bestColumns, extraHeight = height % rowCount;
        var cells = new PixelRectangle[count];
        for (int index = 0; index < count; index++)
        {
            int column = index % bestColumns, row = index / bestColumns;
            long x = display.WorkArea.X + (long)gap + column * (baseWidth + gap) + Math.Min(column, extraWidth);
            long y = display.WorkArea.Y + (long)gap + row * (baseHeight + gap) + Math.Min(row, extraHeight);
            cells[index] = new((int)x, (int)y, (int)(baseWidth + (column < extraWidth ? 1 : 0)), (int)(baseHeight + (row < extraHeight ? 1 : 0)));
            if (!display.WorkArea.Contains(cells[index])) return new(false, Array.Empty<PixelRectangle>(), "The physical display bounds could not be represented safely.");
        }
        return new(true, Array.AsReadOnly(cells), "Physical workarea layout prepared.");
    }
    private static int Scale(int dip, int percent) => (int)(((long)dip * percent + 99) / 100);
}

/// <summary>
/// Pure invisible-border compensation. On Windows 10/11 the outer rect of a thick-frame window includes invisible
/// DWM resize borders (about 7 px at 100% on the left, right and bottom); the visible frame is inset from it.
/// Tile cells describe the visible frame, so the outer rect to post is the cell grown by the measured borders.
/// </summary>
public static class ClientWindowFrame
{
    /// <summary>Largest plausible invisible border per side (about 37 px at the 500% scale limit).</summary>
    public const int MaximumBorder = 64;

    /// <summary>Visible frame used for confirmation: the DWM frame when it is a plausible inset of the outer rect, else the outer rect.</summary>
    public static PixelRectangle VisibleOrOuter(PixelRectangle windowRect, PixelRectangle? visibleFrame)
        => visibleFrame is { } frame && Compensate(windowRect, windowRect, frame) is not null ? frame : windowRect;

    /// <summary>
    /// Outer rect that puts the visible frame on <paramref name="cell"/>, or null when the frame is not a plausible inset
    /// of the window rect (unavailable, outside it, or a border above <see cref="MaximumBorder"/>) or the result overflows.
    /// </summary>
    public static PixelRectangle? Compensate(PixelRectangle cell, PixelRectangle windowRect, PixelRectangle visibleFrame)
    {
        if (!cell.IsValid || !windowRect.Contains(visibleFrame)) return null;
        long left = (long)visibleFrame.X - windowRect.X, top = (long)visibleFrame.Y - windowRect.Y;
        long right = (long)windowRect.X + windowRect.Width - ((long)visibleFrame.X + visibleFrame.Width);
        long bottom = (long)windowRect.Y + windowRect.Height - ((long)visibleFrame.Y + visibleFrame.Height);
        if (left > MaximumBorder || top > MaximumBorder || right > MaximumBorder || bottom > MaximumBorder) return null;
        long x = cell.X - left, y = cell.Y - top, width = cell.Width + left + right, height = cell.Height + top + bottom;
        if (x < int.MinValue || y < int.MinValue || width > int.MaxValue || height > int.MaxValue) return null;
        var outer = new PixelRectangle((int)x, (int)y, (int)width, (int)height);
        return outer.IsValid ? outer : null;
    }
}
