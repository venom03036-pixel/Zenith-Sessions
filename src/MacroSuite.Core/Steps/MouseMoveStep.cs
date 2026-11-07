using System.Diagnostics;
using System.Drawing;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Simulates smooth or direct mouse cursor movement to a specified position (Rule 4).
/// Supports both absolute screen coordinates and target window client-relative coordinates.
/// </summary>
public sealed class MouseMoveStep : MacroStep
{
    /// <summary>
    /// Target coordinates. In absolute virtual screen coordinates if <see cref="IsClientRelative"/>
    /// is <see langword="false"/>, or client-area coordinates relative to the target window
    /// if <see cref="IsClientRelative"/> is <see langword="true"/>.
    /// </summary>
    public Point Position { get; init; }

    /// <summary>
    /// Original screen coordinates when recorded, used as fallback.
    /// </summary>
    public Point? ScreenPosition { get; init; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="Position"/> is relative to the client area
    /// of the active or target window.
    /// </summary>
    public bool IsClientRelative { get; init; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="Position"/> is a pixel delta (dx, dy)
    /// for locked-cursor / raw look playback instead of an absolute screen point.
    /// </summary>
    public bool IsRelative { get; init; }

    /// <summary>Absolute screen targeting versus relative look/drag deltas.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public MouseMoveMode Mode => IsRelative ? MouseMoveMode.Relative : MouseMoveMode.Absolute;

    /// <summary>
    /// Optional window title or process name of the target window at recording time.
    /// </summary>
    public string? TargetWindowName { get; init; }

    /// <summary>Client width when the move was recorded. Used to scale client-relative playback.</summary>
    public int RecordedClientWidth { get; init; }

    /// <summary>Client height when the move was recorded. Used to scale client-relative playback.</summary>
    public int RecordedClientHeight { get; init; }

    /// <summary>
    /// Timestamp relative to the beginning of the macro recording session.
    /// Used for absolute, drift-safe playback scheduling.
    /// </summary>
    public TimeSpan RelativeTimestamp { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Duration of movement interpolation in milliseconds.
    /// </summary>
    public int DurationMs { get; init; }

    /// <summary>Optional execution-context variable used instead of <see cref="Position"/> X. Ignored for relative look deltas.</summary>
    public string? XVariable { get; init; }

    /// <summary>Optional execution-context variable used instead of <see cref="Position"/> Y. Ignored for relative look deltas.</summary>
    public string? YVariable { get; init; }

    /// <summary>Default parameterless constructor for deserialization.</summary>
    public MouseMoveStep()
    {
        Label = "Move Cursor";
    }

    /// <summary>Constructs a mouse move step to the specified point.</summary>
    public MouseMoveStep(Point position, bool isClientRelative = false, TimeSpan relativeTimestamp = default)
    {
        Position = position;
        IsClientRelative = isClientRelative;
        RelativeTimestamp = relativeTimestamp;
        Label = isClientRelative
            ? $"Move to Client ({position.X}, {position.Y})"
            : $"Move to ({position.X}, {position.Y})";
    }

    /// <inheritdoc/>
    public override async Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsEnabled || context.IsAborted || ct.IsCancellationRequested)
        {
            return;
        }

        if (context.InputPlayer is null)
        {
            return;
        }

        int x = Position.X;
        int y = Position.Y;
        IntPtr playbackWindow = IsClientRelative && !IsRelative ? context.PlaybackSession?.Target?.Window?.Handle
            ?? (context.InputPlayer as MacroSuite.Core.Abstractions.IClientRelativePlaybackTarget)?.TargetWindowHandle ?? IntPtr.Zero : IntPtr.Zero;
        if (!IsRelative)
        {
            x = CoordinateVariableResolver.Resolve(context, XVariable, Position.X, "X");
            y = CoordinateVariableResolver.Resolve(context, YVariable, Position.Y, "Y");
            if (IsClientRelative && playbackWindow == IntPtr.Zero)
            {
                if (context.WindowCoordinates is null)
                {
                    throw new InvalidOperationException(
                        "Client-relative move requires a window coordinate service; the move was not executed.");
                }

                var screen = ClientCoordinateResolver.ResolveScreenPoint(
                    context.WindowCoordinates,
                    new Point(x, y),
                    TargetWindowName,
                    RecordedClientWidth,
                    RecordedClientHeight,
                    context.PlaybackSession);
                x = screen.X;
                y = screen.Y;
            }
        }

        long ticks = Stopwatch.GetTimestamp();
        var ev = IsRelative
            ? new InputEvent(InputEventType.MouseMoveRelative, ticks, x, y, 0, 0)
            : new InputEvent(InputEventType.MouseMove, ticks, x, y, 0, 0);

        await context.InputPlayer.PlayAsync(new[] { ev }, context.ToPlaybackOptions(playbackWindow, RecordedClientWidth, RecordedClientHeight), ct).ConfigureAwait(false);
    }
}
