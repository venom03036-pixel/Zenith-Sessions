using System.Diagnostics;
using System.Drawing;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Simulates a mouse click at a specified coordinate via the controlled input pipeline (Rule 4).
/// </summary>
public sealed class ClickStep : MacroStep
{
    /// <summary>Target position in virtual screen coordinates.</summary>
    public Point Position { get; init; }

    /// <summary>Which mouse button to click.</summary>
    public MouseButton Button { get; init; } = MouseButton.Left;

    /// <summary>Number of clicks (1 for single click, 2 for double click).</summary>
    public int ClickCount { get; init; } = 1;

    /// <summary>
    /// Press = down+up (legacy click). Down / Up are hold primitives so a
    /// left- or right-click drag can replay as hold → moves → release.
    /// </summary>
    public string Stroke { get; init; } = "Press";

    /// <summary>Optional pause in milliseconds after clicking.</summary>
    public int DelayAfterMs { get; init; } = 50;

    /// <summary>
    /// Timestamp relative to the beginning of the macro recording session.
    /// Used for absolute, drift-safe playback scheduling.
    /// </summary>
    public TimeSpan RelativeTimestamp { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// When <see langword="true"/>, <see cref="Position"/> is relative to the client area
    /// of the active or target window.
    /// </summary>
    public bool IsClientRelative { get; init; }

    /// <summary>
    /// Optional window title or process name of the target window at recording time.
    /// </summary>
    public string? TargetWindowName { get; init; }

    /// <summary>Client width when the click was recorded. Used to scale client-relative playback.</summary>
    public int RecordedClientWidth { get; init; }

    /// <summary>Client height when the click was recorded. Used to scale client-relative playback.</summary>
    public int RecordedClientHeight { get; init; }

    /// <summary>Optional execution-context variable used instead of <see cref="Position"/> X.</summary>
    public string? XVariable { get; init; }

    /// <summary>Optional execution-context variable used instead of <see cref="Position"/> Y.</summary>
    public string? YVariable { get; init; }

    /// <summary>Pixel offset added to the resolved X coordinate before client-to-screen conversion.</summary>
    public int OffsetX { get; init; }

    /// <summary>Pixel offset added to the resolved Y coordinate before client-to-screen conversion.</summary>
    public int OffsetY { get; init; }

    /// <summary>Constructs a click step with default position (0, 0).</summary>
    public ClickStep()
    {
        Label = $"Click {Button}";
    }

    /// <summary>Constructs a click step at the specified point.</summary>
    public ClickStep(Point position, MouseButton button = MouseButton.Left, int clickCount = 1, int delayAfterMs = 50)
    {
        Position = position;
        Button = button;
        ClickCount = Math.Max(1, clickCount);
        DelayAfterMs = Math.Max(0, delayAfterMs);
        Label = clickCount > 1 ? $"Double Click {button} at ({position.X}, {position.Y})" : $"Click {button} at ({position.X}, {position.Y})";
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
            context.WriteTrace(
                "Input",
                "Click was not executed because no input player is configured.",
                ExecutionTraceLevel.Warning,
                StepId);
            return;
        }

        int targetX = checked(CoordinateVariableResolver.Resolve(context, XVariable, Position.X, "X") + OffsetX);
        int targetY = checked(CoordinateVariableResolver.Resolve(context, YVariable, Position.Y, "Y") + OffsetY);
        IntPtr playbackWindow = IsClientRelative ? context.PlaybackSession?.Target?.Window?.Handle
            ?? (context.InputPlayer as MacroSuite.Core.Abstractions.IClientRelativePlaybackTarget)?.TargetWindowHandle ?? IntPtr.Zero : IntPtr.Zero;
        if (IsClientRelative && playbackWindow == IntPtr.Zero)
        {
            if (context.WindowCoordinates is null)
            {
                throw new InvalidOperationException(
                    "Client-relative click requires a window coordinate service; the click was not executed.");
            }

            var screen = ClientCoordinateResolver.ResolveScreenPoint(
                context.WindowCoordinates,
                new Point(targetX, targetY),
                TargetWindowName,
                RecordedClientWidth,
                RecordedClientHeight,
                context.PlaybackSession);
            targetX = screen.X;
            targetY = screen.Y;
        }
        long ticks = Stopwatch.GetTimestamp();
        long gap = Stopwatch.Frequency / 500; // ~2 ms between move / down / up
        string stroke = Stroke ?? "Press";
        var events = new List<InputEvent>(4);

        // Down/Up are hold primitives — do not yank the cursor first (breaks locked-cursor look).
        bool holdStroke = stroke.Equals("Down", StringComparison.OrdinalIgnoreCase)
            || stroke.Equals("Up", StringComparison.OrdinalIgnoreCase);
        if (!holdStroke)
            events.Add(new InputEvent(InputEventType.MouseMove, ticks, targetX, targetY, 0, 0));

        if (stroke.Equals("Down", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new InputEvent(InputEventType.MouseDown, ticks + gap, targetX, targetY, (int)Button, 0));
        }
        else if (stroke.Equals("Up", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new InputEvent(InputEventType.MouseUp, ticks + gap, targetX, targetY, (int)Button, 0));
        }
        else
        {
            for (int i = 0; i < ClickCount; i++)
            {
                events.Add(new InputEvent(InputEventType.MouseDown, ticks + gap * (1 + i * 2), targetX, targetY, (int)Button, 0));
                events.Add(new InputEvent(InputEventType.MouseUp, ticks + gap * (2 + i * 2), targetX, targetY, (int)Button, 0));
            }
        }

        await context.InputPlayer.PlayAsync(events, context.ToPlaybackOptions(playbackWindow, RecordedClientWidth, RecordedClientHeight), ct).ConfigureAwait(false);

        // Recorded clicks are paced by RelativeTimestamp in the executor.
        if (RelativeTimestamp > TimeSpan.Zero)
            return;

        if (DelayAfterMs > 0 && !context.IsAborted && !ct.IsCancellationRequested)
        {
            await context.DelayAsync(StepTimeline.Scale(TimeSpan.FromMilliseconds(DelayAfterMs), context.PlaybackSpeed), ct).ConfigureAwait(false);
        }
    }
}
