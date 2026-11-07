using MacroSuite.Core.Models;

namespace MacroSuite.Core.Abstractions;

/// <summary>
/// One-shot screen-coordinate capture. Arming is not recording.
/// Implementations must keep hook callbacks allocation-light and always pass the click through.
/// </summary>
public interface ILocationPicker : IDisposable
{
    bool IsArmed { get; }

    /// <summary>
    /// Arms the picker and waits for Tab (confirm cursor position) or Escape (cancel).
    /// Returns null when cancelled.
    /// </summary>
    Task<ScreenPoint?> ArmAndWaitAsync(CancellationToken ct = default);

    void Cancel();
}
