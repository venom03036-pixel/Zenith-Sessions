using System.Runtime.InteropServices;

namespace MacroSuite.Core.Models;

/// <summary>
/// A single captured input event. Designed for zero-heap-allocation use on the hook callback
/// stack: constructed with <c>new</c> and written to a <c>Channel&lt;InputEvent&gt;</c> by value.
/// </summary>
/// <remarks>
/// <para>
/// Layout (Pack=1, no padding):
/// <list type="bullet">
///   <item><description>Type         — 1 byte  (InputEventType : byte)</description></item>
///   <item><description>_pad         — 3 bytes (alignment to long boundary)</description></item>
///   <item><description>TimestampTicks — 8 bytes</description></item>
///   <item><description>X            — 4 bytes</description></item>
///   <item><description>Y            — 4 bytes</description></item>
///   <item><description>Data         — 4 bytes</description></item>
///   <item><description>ExtraInfo    — 4 bytes</description></item>
/// </list>
/// Total: 28 bytes.
/// </para>
/// <para>
/// <b>Data field semantics:</b>
/// <list type="bullet">
///   <item><description>MouseDown / MouseUp — cast to <see cref="MouseButton"/></description></item>
///   <item><description>KeyDown / KeyUp     — Windows Virtual-Key code</description></item>
    ///   <item><description>MouseWheel          — signed vertical delta (positive = forward)</description></item>
    ///   <item><description>MouseHWheel         — signed horizontal / tilt delta</description></item>
    ///   <item><description>Unicode / UnicodeUp — UTF-16 code unit</description></item>
/// </list>
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct InputEvent
{
    /// <summary>The type of input event.</summary>
    public readonly InputEventType Type;

    /// <summary>
    /// High-resolution timestamp from <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>.
    /// Use <see cref="System.Diagnostics.Stopwatch.Frequency"/> to convert to seconds.
    /// </summary>
    public readonly long TimestampTicks;

    /// <summary>Screen X coordinate (virtual screen coordinates).</summary>
    public readonly int X;

    /// <summary>Screen Y coordinate (virtual screen coordinates).</summary>
    public readonly int Y;

    /// <summary>
    /// Auxiliary data whose meaning depends on <see cref="Type"/>:
    /// <list type="bullet">
    ///   <item><description>MouseDown / MouseUp → <see cref="MouseButton"/></description></item>
    ///   <item><description>KeyDown / KeyUp     → Windows Virtual-Key code</description></item>
    ///   <item><description>MouseWheel          → signed vertical wheel delta</description></item>
    ///   <item><description>MouseHWheel         → signed horizontal wheel delta</description></item>
    ///   <item><description>Unicode / UnicodeUp → UTF-16 code unit</description></item>
    /// </list>
    /// </summary>
    public readonly int Data;

    /// <summary>
    /// The value of <c>dwExtraInfo</c> from the Windows hook structure.
    /// May be used to identify synthesized vs. real events.
    /// </summary>
    public readonly uint ExtraInfo;

    /// <summary>
    /// Constructs an <see cref="InputEvent"/>. This is the only constructor; all fields are set
    /// here so the struct can be created on the hook callback stack without any heap allocation.
    /// </summary>
    public InputEvent(
        InputEventType type,
        long timestampTicks,
        int x,
        int y,
        int data,
        uint extraInfo = 0)
    {
        Type           = type;
        TimestampTicks = timestampTicks;
        X              = x;
        Y              = y;
        Data           = data;
        ExtraInfo      = extraInfo;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Type} ({X},{Y}) t={TimestampTicks} d={Data}";
}
