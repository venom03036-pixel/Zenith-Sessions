namespace Zenith.Sessions.Core;

/// <summary>Requested client/content area in physical pixels, independent of display scaling.</summary>
public sealed record ClientContentSize(int Width, int Height)
{
    public const int MinimumWidth = 160, MinimumHeight = 120, MaximumDimension = 8192;
    public ClientContentSize Validated()
    {
        if (Width is < MinimumWidth or > MaximumDimension || Height is < MinimumHeight or > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(ClientContentSize), "Content size must be 160–8192 pixels wide and 120–8192 pixels high.");
        return this;
    }
}

/// <summary>One observed physical geometry and its current monitor. It is not a window-operation lease.</summary>
public sealed record ClientWindowGeometry(PixelRectangle OuterBounds, PixelRectangle ContentBounds, ClientDisplay Display)
{
    public bool IsValid => Display is { IsValid: true } && OuterBounds.Contains(ContentBounds);
}

public sealed record ClientWindowResizePlan(bool Fits, PixelRectangle? OuterBounds, string Explanation);

public interface IClientWindowResizer
{
    // Selection and size are pinned before waiting for the existing common window-operation lane.
    // RequestedBounds in each result is the OUTER rectangle. Applied confirms the CONTENT size.
    Task<ClientWindowArrangementResult> ResizeAsync(IReadOnlyList<SessionObservation> sessions, ClientContentSize size, CancellationToken ct);
    void CancelWindowOperations();
}

/// <summary>Uses observed frame margins, never guessed DPI-scaled content. No native calls or persistence.</summary>
public static class ClientWindowResizePlanner
{
    public static ClientWindowResizePlan Plan(ClientWindowGeometry geometry, ClientContentSize size)
    {
        ArgumentNullException.ThrowIfNull(geometry); ArgumentNullException.ThrowIfNull(size); size.Validated();
        if (!geometry.IsValid) return new(false, null, "Current physical window content, frame and display geometry could not be confirmed.");
        PixelRectangle outer = geometry.OuterBounds, content = geometry.ContentBounds, work = geometry.Display.WorkArea;
        long width = (long)size.Width + outer.Width - content.Width, height = (long)size.Height + outer.Height - content.Height;
        if (width > work.Width || height > work.Height || width > int.MaxValue || height > int.MaxValue)
            return new(false, null, "The requested content plus the current window frame cannot fit this client's current display workarea. Choose a smaller size.");
        long x = Math.Clamp((long)outer.X, work.X, (long)work.X + work.Width - width);
        long y = Math.Clamp((long)outer.Y, work.Y, (long)work.Y + work.Height - height);
        var requested = new PixelRectangle((int)x, (int)y, (int)width, (int)height);
        return work.Contains(requested) ? new(true, requested, "Physical content size and current-display outer bounds prepared.")
            : new(false, null, "The physical window bounds could not be represented safely.");
    }
}
