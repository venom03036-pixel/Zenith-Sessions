using System.Diagnostics;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Simulates a keyboard key press and release via the controlled input pipeline (Rule 4).
/// </summary>
public sealed class KeyPressStep : MacroStep
{
    /// <summary>Win32 Virtual-Key code.</summary>
    public int KeyCode { get; init; }
    /// <summary>When true, KeyCode is a preserved UTF-16 code unit rather than a virtual key.</summary>
    public bool IsUnicode { get; init; }

    /// <summary>Optional pause in milliseconds after pressing the key.</summary>
    public int DelayAfterMs { get; init; } = 30;

    /// <summary>Press = down+up (legacy). Down / Up are held-key primitives for IF/WHILE bodies.</summary>
    public string Stroke { get; init; } = "Press";

    /// <summary>
    /// Timestamp relative to the beginning of the macro recording session.
    /// Used for absolute, drift-safe playback scheduling.
    /// </summary>
    public TimeSpan RelativeTimestamp { get; init; } = TimeSpan.Zero;

    /// <summary>Constructs a key press step.</summary>
    public KeyPressStep()
    {
        Label = "Key Press";
    }

    /// <summary>Constructs a key press step with specified virtual-key code.</summary>
    public KeyPressStep(int keyCode, int delayAfterMs = 30)
    {
        KeyCode = keyCode;
        DelayAfterMs = Math.Max(0, delayAfterMs);
        Label = $"Press Key (VK={keyCode})";
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

        long ticks = Stopwatch.GetTimestamp();
        if (IsUnicode && KeyCode is < 0 or > 0xffff) throw new InvalidDataException("The recorded Unicode code unit is invalid.");
        InputEventType downType = IsUnicode ? InputEventType.Unicode : InputEventType.KeyDown;
        InputEventType upType = IsUnicode ? InputEventType.UnicodeUp : InputEventType.KeyUp;
        string stroke = Stroke ?? "Press";
        InputEvent[] events;
        if (stroke.Equals("Down", StringComparison.OrdinalIgnoreCase))
        {
            events = [new InputEvent(downType, ticks, 0, 0, KeyCode, 0)];
        }
        else if (stroke.Equals("Up", StringComparison.OrdinalIgnoreCase))
        {
            events = [new InputEvent(upType, ticks, 0, 0, KeyCode, 0)];
        }
        else
        {
            events =
            [
                new InputEvent(downType, ticks, 0, 0, KeyCode, 0),
                new InputEvent(upType, ticks, 0, 0, KeyCode, 0)
            ];
        }

        await context.InputPlayer.PlayAsync(events, context.ToPlaybackOptions(), ct).ConfigureAwait(false);

        if (RelativeTimestamp > TimeSpan.Zero)
            return;

        if (DelayAfterMs > 0 && !context.IsAborted && !ct.IsCancellationRequested)
        {
            await context.DelayAsync(StepTimeline.Scale(TimeSpan.FromMilliseconds(DelayAfterMs), context.PlaybackSpeed), ct).ConfigureAwait(false);
        }
    }
}
