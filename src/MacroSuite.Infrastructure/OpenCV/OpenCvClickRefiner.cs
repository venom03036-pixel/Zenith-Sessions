using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Models;
using OpenCvSharp;

namespace MacroSuite.Infrastructure.OpenCV;

/// <summary>
/// Snaps a coarse AI click to a nearby edge/contour on a native-resolution crop (Rule 8).
/// Color detection and template matching stay on native frames; this only refines AI clicks.
/// </summary>
public sealed class OpenCvClickRefiner : IClickRefiner
{
    public const int DefaultRadius = 60;
    private const int MinEdge = 8;

    public async Task<(int x, int y)> RefineAsync(
        IScreenCaptureService capture,
        int nativeX,
        int nativeY,
        Rectangle allowedRegion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ct.ThrowIfCancellationRequested();

        Rectangle roi = BuildRoi(nativeX, nativeY, allowedRegion, DefaultRadius);
        if (roi.Width < MinEdge || roi.Height < MinEdge)
            return (nativeX, nativeY);

        using CaptureFrame frame = await capture.CaptureRegionAsync(roi, ct).ConfigureAwait(false);
        if (frame.IsDisposed || frame.Width < MinEdge || frame.Height < MinEdge)
            return (nativeX, nativeY);

        (int localX, int localY) = SnapToContour(frame, nativeX - roi.X, nativeY - roi.Y);
        return (roi.X + localX, roi.Y + localY);
    }

    internal static Rectangle BuildRoi(int x, int y, Rectangle allowed, int radius)
    {
        int left = x - radius;
        int top = y - radius;
        int right = x + radius;
        int bottom = y + radius;

        if (allowed.Width > 0 && allowed.Height > 0)
        {
            left = Math.Max(left, allowed.Left);
            top = Math.Max(top, allowed.Top);
            right = Math.Min(right, allowed.Right);
            bottom = Math.Min(bottom, allowed.Bottom);
        }

        int w = Math.Max(0, right - left);
        int h = Math.Max(0, bottom - top);
        return new Rectangle(left, top, w, h);
    }

    private static unsafe (int x, int y) SnapToContour(CaptureFrame frame, int localX, int localY)
    {
        localX = Math.Clamp(localX, 0, frame.Width - 1);
        localY = Math.Clamp(localY, 0, frame.Height - 1);

        fixed (byte* pBytes = frame.Span)
        {
            MatType matType = frame.Format == FramePixelFormat.Bgr24
                ? MatType.CV_8UC3
                : MatType.CV_8UC4;
            using var mat = new Mat(frame.Height, frame.Width, matType, (IntPtr)pBytes, frame.Stride);
            using var gray = new Mat();
            if (mat.Channels() == 4)
                Cv2.CvtColor(mat, gray, ColorConversionCodes.BGRA2GRAY);
            else if (mat.Channels() == 3)
                Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
            else
                mat.CopyTo(gray);

            using var edges = new Mat();
            Cv2.Canny(gray, edges, 50, 150);
            Cv2.FindContours(
                edges,
                out OpenCvSharp.Point[][] contours,
                out _,
                RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);

            if (contours is null || contours.Length == 0)
                return (localX, localY);

            var center = new OpenCvSharp.Point(localX, localY);
            double bestInside = double.MinValue;
            int bestInsideIndex = -1;
            double bestNear = double.MaxValue;
            int bestNearIndex = -1;

            for (int i = 0; i < contours.Length; i++)
            {
                OpenCvSharp.Point[] contour = contours[i];
                if (contour is null || contour.Length < 3)
                    continue;

                double dist = Cv2.PointPolygonTest(contour, center, measureDist: true);
                if (dist >= 0 && dist > bestInside)
                {
                    bestInside = dist;
                    bestInsideIndex = i;
                }

                double abs = dist < 0 ? -dist : dist;
                if (abs < bestNear)
                {
                    bestNear = abs;
                    bestNearIndex = i;
                }
            }

            int pick = bestInsideIndex >= 0 ? bestInsideIndex : bestNearIndex;
            if (pick < 0)
                return (localX, localY);

            Moments m = Cv2.Moments(contours[pick]);
            if (Math.Abs(m.M00) < 0.001)
                return (localX, localY);

            int cx = (int)Math.Round(m.M10 / m.M00, MidpointRounding.AwayFromZero);
            int cy = (int)Math.Round(m.M01 / m.M00, MidpointRounding.AwayFromZero);
            cx = Math.Clamp(cx, 0, frame.Width - 1);
            cy = Math.Clamp(cy, 0, frame.Height - 1);
            return (cx, cy);
        }
    }
}
