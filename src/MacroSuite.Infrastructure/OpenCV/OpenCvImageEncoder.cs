using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Capture;
using MacroSuite.Core.Models;
using OpenCvSharp;

namespace MacroSuite.Infrastructure.OpenCV;

/// <summary>
/// High-performance OpenCV-based image encoder implementing <see cref="IImageEncoder"/>.
/// Encodes raw memory frame buffers into compressed JPEG or PNG formats without unnecessary managed copies.
/// </summary>
public sealed class OpenCvImageEncoder : IImageEncoder
{
    /// <inheritdoc/>
    public unsafe byte[] EncodeJpeg(CaptureFrame frame, int quality = 80)
        => EncodeJpeg(frame, quality, 0, 0);

    public unsafe byte[] EncodeJpeg(CaptureFrame frame, int quality, int maxEdge)
        => EncodeJpeg(frame, quality, maxEdge, maxEdge);

    public unsafe byte[] EncodeJpeg(CaptureFrame frame, int quality, int maxWidth, int maxHeight)
    {
        ValidateFrame(frame);

        quality = Math.Clamp(quality, 1, 100);
        var fit = ImageDownscale.Fit(frame.Width, frame.Height, maxWidth, maxHeight);

        fixed (byte* pBytes = frame.Span)
        {
            MatType matType = frame.Format == FramePixelFormat.Bgr24
                ? MatType.CV_8UC3
                : MatType.CV_8UC4;

            using var mat = new Mat(frame.Height, frame.Width, matType, (IntPtr)pBytes, frame.Stride);
            using var bgrMat = new Mat();

            if (matType == MatType.CV_8UC4)
                Cv2.CvtColor(mat, bgrMat, frame.Format == FramePixelFormat.Rgba32
                    ? ColorConversionCodes.RGBA2BGR : ColorConversionCodes.BGRA2BGR);
            else
                mat.CopyTo(bgrMat);

            int[] prms = [(int)ImwriteFlags.JpegQuality, quality];
            if (fit.WasDownscaled)
            {
                using var resized = new Mat();
                Cv2.Resize(
                    bgrMat,
                    resized,
                    new Size(fit.DestWidth, fit.DestHeight),
                    0,
                    0,
                    InterpolationFlags.Area);
                Cv2.ImEncode(".jpg", resized, out byte[] resizedBytes, prms);
                return resizedBytes;
            }

            Cv2.ImEncode(".jpg", bgrMat, out byte[] outputBytes, prms);
            return outputBytes;
        }
    }

    /// <inheritdoc/>
    public unsafe byte[] EncodePng(CaptureFrame frame)
        => EncodePng(frame, 0, 0);

    public unsafe byte[] EncodePng(CaptureFrame frame, int maxWidth, int maxHeight)
    {
        ValidateFrame(frame);

        var fit = ImageDownscale.Fit(frame.Width, frame.Height, maxWidth, maxHeight);

        fixed (byte* pBytes = frame.Span)
        {
            MatType matType = frame.Format == FramePixelFormat.Bgr24
                ? MatType.CV_8UC3
                : MatType.CV_8UC4;

            using var mat = new Mat(frame.Height, frame.Width, matType, (IntPtr)pBytes, frame.Stride);
            // OpenCV's encoders expect BGR/BGRA ordering. Keep existing native BGRA/BGR
            // frames zero-copy; normalize only a frame explicitly declared RGBA.
            using var normalized = frame.Format == FramePixelFormat.Rgba32 ? new Mat() : null;
            Mat pixels = mat;
            if (normalized is not null)
            {
                Cv2.CvtColor(mat, normalized, ColorConversionCodes.RGBA2BGRA);
                pixels = normalized;
            }
            if (!fit.WasDownscaled)
            {
                Cv2.ImEncode(".png", pixels, out byte[] outputBytes);
                return outputBytes;
            }

            using var resized = new Mat();
            Cv2.Resize(
                pixels,
                resized,
                new Size(fit.DestWidth, fit.DestHeight),
                0,
                0,
                InterpolationFlags.Area);
            Cv2.ImEncode(".png", resized, out byte[] resizedBytes);
            return resizedBytes;
        }
    }

    private static void ValidateFrame(CaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed) throw new ObjectDisposedException(nameof(frame));
        if (frame.Format is not (FramePixelFormat.Bgr24 or FramePixelFormat.Bgra32 or FramePixelFormat.Rgba32))
            throw new ArgumentException("The frame's pixel format is not supported.", nameof(frame));
        long rowBytes = (long)frame.Width * CaptureFrame.GetBytesPerPixel(frame.Format);
        if (frame.Stride < rowBytes)
            throw new ArgumentException("The frame's row stride is smaller than its physical pixel width.", nameof(frame));
    }
}
