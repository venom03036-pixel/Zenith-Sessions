namespace MacroSuite.Core.Models;

/// <summary>
/// Fail-closed checks for captured pixel buffers. Empty, disposed, or undersized frames
/// must throw so <c>FallbackCaptureCoordinator</c> can use GDI (Rule 5, Rule 15).
/// Black pixels are valid; this does not treat an all-zero DXGI desktop as a stub.
/// </summary>
public static class CaptureFrameIntegrity
{
    public static void ThrowIfUnpopulated(CaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (IsUnpopulated(frame))
        {
            throw new InvalidOperationException(
                $"Capture buffer from {frame.Source} is empty or unpopulated ({frame.Width}x{frame.Height}, stride {frame.Stride}).");
        }
    }

    public static bool IsUnpopulated(CaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed || frame.Width <= 0 || frame.Height <= 0 || frame.Stride <= 0)
            return true;

        int bytesPerPixel = CaptureFrame.GetBytesPerPixel(frame.Format);
        if (frame.Stride < frame.Width * bytesPerPixel)
            return true;

        ReadOnlySpan<byte> span = frame.Span;
        int required = checked(frame.Height * frame.Stride);
        return span.IsEmpty || span.Length < required;
    }
}
