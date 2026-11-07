using MacroSuite.Core.Input;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Replays a sequence of <see cref="InputEvent"/> records through the OS input simulation
/// pipeline. All implementations must use the single controlled input pipeline (Rule 4).
/// </summary>
public interface IInputPlayer : IDisposable
{
    /// <summary>
    /// Asynchronously replays <paramref name="events"/> with the specified options.
    /// </summary>
    /// <param name="events">The ordered sequence of events to replay.</param>
    /// <param name="options">Playback configuration. Defaults to <see cref="PlaybackOptions.Default"/>.</param>
    /// <param name="ct">Cancellation token. Cancellation causes an orderly stop; panic is handled separately.</param>
    /// <returns>A task that completes when all events have been played or cancellation is requested.</returns>
    /// <remarks>
    /// Implementations must call <see cref="ReleaseAllHeldKeys"/> before returning on cancellation
    /// to prevent stuck keys or buttons.
    /// </remarks>
    Task PlayAsync(
        IReadOnlyList<InputEvent> events,
        PlaybackOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Immediately releases all currently held keys and mouse buttons via synthesized key-up
    /// and mouse-up events. Called on panic/emergency stop (Rule 11).
    /// </summary>
    void ReleaseAllHeldKeys();

    /// <summary>
    /// Gets a value indicating whether playback is currently in progress.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Simulates an immediate mouse click at the current cursor position or optional (x, y) coordinates.
    /// Allocation-free and safe for high-frequency autoclicker execution (Rule 4, Rule 7).
    /// </summary>
    void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false);

    /// <summary>
    /// Immediate relative cursor / look delta. Stays on the calling thread,
    /// does not release held buttons, and is safe for a high-frequency camera loop.
    /// </summary>
    void SendRelativeMove(int dx, int dy);

    /// <summary>
    /// Same as <see cref="SendRelativeMove(int, int)"/>. When
    /// <paramref name="dualBackend"/> is false, only one OS path is used so
    /// a game that sees both SendInput and mouse_event does not get a double look.
    /// </summary>
    void SendRelativeMove(int dx, int dy, bool dualBackend)
        => SendRelativeMove(dx, dy);
}
