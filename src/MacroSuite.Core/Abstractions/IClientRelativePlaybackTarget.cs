namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Optional first-party recipient binding for client-relative playback. This cached handle
/// does not assert foreground state, account identity or input permission. The implementing
/// controlled player must revalidate its exact recipient at the final emission boundary.
/// Existing players without this capability retain their current targeting behavior.
/// </summary>
public interface IClientRelativePlaybackTarget
{
    IntPtr TargetWindowHandle { get; }
}
