using System.Diagnostics;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Replays a vertical and/or horizontal mouse-wheel tick. Not a cursor move.
/// </summary>
public sealed class MouseWheelStep : MacroStep
{
    /// <summary>Vertical wheel delta. Windows WHEEL_DELTA is 120 per notch.</summary>
    public int DeltaY { get; init; }

    /// <summary>Horizontal wheel delta (tilt wheel / WM_MOUSEHWHEEL).</summary>
    public int DeltaX { get; init; }

    public TimeSpan RelativeTimestamp { get; init; } = TimeSpan.Zero;

    public MouseWheelStep()
    {
        Label = "Scroll Wheel";
    }

    public MouseWheelStep(int deltaY, int deltaX = 0, TimeSpan relativeTimestamp = default)
    {
        DeltaY = deltaY;
        DeltaX = deltaX;
        RelativeTimestamp = relativeTimestamp;
        Label = deltaX != 0 && deltaY == 0
            ? $"Scroll X {deltaX}"
            : $"Scroll {deltaY}";
    }

    public override async Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsEnabled || context.IsAborted || ct.IsCancellationRequested)
            return;
        if (context.InputPlayer is null)
            return;

        long ticks = Stopwatch.GetTimestamp();
        var events = new List<InputEvent>(2);
        if (DeltaY != 0)
            events.Add(new InputEvent(InputEventType.MouseWheel, ticks, 0, 0, DeltaY, 0));
        if (DeltaX != 0)
            events.Add(new InputEvent(InputEventType.MouseHWheel, ticks, 0, 0, DeltaX, 0));
        if (events.Count == 0)
            return;

        await context.InputPlayer.PlayAsync(events, context.ToPlaybackOptions(), ct).ConfigureAwait(false);
    }
}
