using System.Buffers;
using System.Drawing;
using MacroSuite.Core.Models;
using OpenCvSharp;

namespace MacroSuite.Infrastructure.OpenCV;

internal static class ImageScanFrameConverter
{
    private static long _generation;
    public static unsafe ImageScanFrame Convert(CaptureFrame native, ImageScanResolution resolution)
    {
        System.Drawing.Size size = ImageScanFrame.Fit(new(native.Width, native.Height), resolution);
        int channels = CaptureFrame.GetBytesPerPixel(native.Format);
        int stride = checked(size.Width * channels);
        var owner = MemoryPool<byte>.Shared.Rent(checked(stride * size.Height));
        try
        {
            fixed (byte* source = native.Span)
            fixed (byte* destination = owner.Memory.Span)
            {
                using var src = new Mat(native.Height, native.Width, channels == 3 ? MatType.CV_8UC3 : MatType.CV_8UC4, (IntPtr)source, native.Stride);
                using var dst = new Mat(size.Height, size.Width, src.Type(), (IntPtr)destination, stride);
                if (size.Width == native.Width && size.Height == native.Height) src.CopyTo(dst);
                else Cv2.Resize(src, dst, new OpenCvSharp.Size(size.Width, size.Height), 0, 0, InterpolationFlags.Linear);
            }
            return new ImageScanFrame(new Rectangle(native.OriginX, native.OriginY, native.Width, native.Height),
                size, stride, native.Format, owner, native.TimestampTicks, Interlocked.Increment(ref _generation));
        }
        catch { owner.Dispose(); throw; }
    }

    public static unsafe Mat Gray(ReadOnlySpan<byte> pixels, int width, int height, int stride, FramePixelFormat format)
    {
        var gray = new Mat();
        try
        {
            fixed (byte* source = pixels)
            {
                using var color = new Mat(height, width, format == FramePixelFormat.Bgr24 ? MatType.CV_8UC3 : MatType.CV_8UC4, (IntPtr)source, stride);
                Cv2.CvtColor(color, gray, format == FramePixelFormat.Bgr24 ? ColorConversionCodes.BGR2GRAY :
                    format == FramePixelFormat.Rgba32 ? ColorConversionCodes.RGBA2GRAY : ColorConversionCodes.BGRA2GRAY);
            }
            return gray;
        }
        catch { gray.Dispose(); throw; }
    }
}
