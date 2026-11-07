using System.Diagnostics;

namespace MacroSuite.Core.Input;

/// <summary>
/// Process-wide input mode. Hook callbacks may read <see cref="Current"/> (volatile int) but must not allocate.
/// </summary>
public static class InputModeGate
{
    private static int _mode;
    private static long _captureEndedTimestamp;

    public static InputMode Current => (InputMode)Volatile.Read(ref _mode);

    public static bool AcceptsRecording => Current == InputMode.Recording;

    public static bool IsResourceCapture => Current is
        InputMode.PickingLocation or
        InputMode.PickingColor or
        InputMode.CapturingImage or
        InputMode.SelectingRegion or
        InputMode.TestingVisualMatch or
        InputMode.TestingAIVision;

    public static void Set(InputMode mode)
    {
        var previous = Current;
        Volatile.Write(ref _mode, (int)mode);
        if (IsCapture(previous) && !IsCapture(mode))
            Volatile.Write(ref _captureEndedTimestamp, Stopwatch.GetTimestamp());
    }

    public static IDisposable Enter(InputMode mode)
    {
        var previous = Current;
        Set(mode);
        return new Restore(previous);
    }

    /// <summary>True for a short window after leaving a picker so mouse-up is not recorded.</summary>
    public static bool InPostCaptureGrace(long nowTimestamp, double graceMs = 120)
    {
        long ended = Volatile.Read(ref _captureEndedTimestamp);
        if (ended == 0)
            return false;
        double elapsedMs = (nowTimestamp - ended) * 1000.0 / Stopwatch.Frequency;
        return elapsedMs >= 0 && elapsedMs < graceMs;
    }

    private static bool IsCapture(InputMode mode) => mode is
        InputMode.PickingLocation or
        InputMode.PickingColor or
        InputMode.CapturingImage or
        InputMode.SelectingRegion or
        InputMode.TestingVisualMatch or
        InputMode.TestingAIVision;

    private sealed class Restore : IDisposable
    {
        private readonly InputMode _previous;
        private int _disposed;

        public Restore(InputMode previous) => _previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Set(_previous);
        }
    }
}
