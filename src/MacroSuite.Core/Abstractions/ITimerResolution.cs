namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Requests a finer OS sleep quantum for high-rate input loops.
/// Windows implementation uses timeBeginPeriod; other platforms no-op.
/// </summary>
public interface ITimerResolution
{
    /// <summary>
    /// Requests <paramref name="periodMs"/> millisecond timer resolution
    /// until the returned scope is disposed.
    /// </summary>
    IDisposable Request(int periodMs = 1);
}
