using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using OpenCvSharp;

namespace MacroSuite.Infrastructure.OpenCV;

/// <summary>
/// High-performance template matching implementation using OpenCV Normalized Cross-Correlation (CCOEFF_NORMED).
/// Uses <see cref="TemplateCache"/> to avoid redundant disk reads and pre-computes grayscale representations.
/// </summary>
public sealed class OpenCvMatcher : IImageMatcher, IImageScanProvider
{
    private readonly TemplateCache _templateCache;
    private readonly bool _ownsCache;
    private bool _disposed;

    public IImageScanSession CreateScanSession(IScreenCaptureService capture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new OpenCvImageScanSession(capture);
    }

    /// <summary>
    /// Initializes a new instance of <see cref="OpenCvMatcher"/> with an optional shared <see cref="TemplateCache"/>.
    /// </summary>
    public OpenCvMatcher(TemplateCache? templateCache = null)
    {
        if (templateCache is null)
        {
            _templateCache = new TemplateCache();
            _ownsCache = true;
        }
        else
        {
            _templateCache = templateCache;
            _ownsCache = false;
        }
    }

    /// <inheritdoc/>
    public Task<MatchResult> MatchAsync(
        CaptureFrame frame,
        string templatePath,
        double minConfidence = 0.8,
        Rectangle? searchRegion = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
        {
            return Task.FromResult(MatchResult.Failed);
        }

        try
        {
            CachedTemplate template = _templateCache.GetOrAdd(templatePath);
            return Task.Run(() => MatchCore(frame, template, minConfidence, searchRegion, ct), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new MatchResult(false, 0.0, Rectangle.Empty, MatchOutcome.Failed, ex.GetType().Name));
        }
    }

    /// <inheritdoc/>
    public Task<MatchResult> MatchAsync(
        CaptureFrame frame,
        byte[] templateImageBytes,
        double minConfidence = 0.8,
        Rectangle? searchRegion = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (templateImageBytes == null || templateImageBytes.Length == 0)
        {
            return Task.FromResult(MatchResult.Failed);
        }

        try
        {
            CachedTemplate template = _templateCache.GetOrAdd(templateImageBytes);
            return Task.Run(() => MatchCore(frame, template, minConfidence, searchRegion, ct), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new MatchResult(false, 0.0, Rectangle.Empty, MatchOutcome.Failed, ex.GetType().Name));
        }
    }

    private static unsafe MatchResult MatchCore(
        CaptureFrame frame,
        CachedTemplate template,
        double minConfidence,
        Rectangle? searchRegion,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (frame.IsDisposed || frame.Width <= 0 || frame.Height <= 0 || template.Width <= 0 || template.Height <= 0)
        {
            return MatchResult.Failed;
        }

        Rectangle roi = searchRegion.HasValue
            ? Clamp(searchRegion.Value, frame.Width, frame.Height)
            : new Rectangle(0, 0, frame.Width, frame.Height);

        if (roi.Width < template.Width || roi.Height < template.Height)
        {
            return new MatchResult(false, 0.0, Rectangle.Empty, MatchOutcome.Failed, "ImageSearchRegionTooSmall")
            {
                ErrorMessage = $"The checked search area is {roi.Width} x {roi.Height} pixels, but the template is "
                    + $"{template.Width} x {template.Height} pixels. Choose a larger search area or capture a smaller template."
            };
        }

        try
        {
            // CCOEFF_NORMED is undefined for a constant template. OpenCV returns
            // 1 everywhere in that case, which would turn a blank crop into a match.
            Cv2.MeanStdDev(template.GrayMat, out _, out Scalar templateDeviation);
            if (templateDeviation.Val0 <= 1e-6)
                return new MatchResult(false, 0.0, Rectangle.Empty, MatchOutcome.Failed, "ImageTemplateHasNoDetail");

            fixed (byte* pBytes = frame.Span)
            {
                MatType matType = frame.Format == FramePixelFormat.Bgr24
                    ? MatType.CV_8UC3
                    : MatType.CV_8UC4;

                using var fullSceneMat = new Mat(frame.Height, frame.Width, matType, (IntPtr)pBytes, frame.Stride);
                using var roiMat = new Mat(fullSceneMat, new OpenCvSharp.Rect(roi.X, roi.Y, roi.Width, roi.Height));
                using var sceneGray = new Mat();

                if (roiMat.Channels() == 4)
                {
                    Cv2.CvtColor(roiMat, sceneGray, ColorConversionCodes.BGRA2GRAY);
                }
                else if (roiMat.Channels() == 3)
                {
                    Cv2.CvtColor(roiMat, sceneGray, ColorConversionCodes.BGR2GRAY);
                }
                else
                {
                    roiMat.CopyTo(sceneGray);
                }

                ct.ThrowIfCancellationRequested();

                const int coarseMinEdge = 320;
                bool useCoarse = roi.Width >= coarseMinEdge
                    && roi.Height >= coarseMinEdge
                    && template.ScaledGrayMat.Width >= 8
                    && template.ScaledGrayMat.Height >= 8
                    && sceneGray.Width / 2 >= template.ScaledGrayMat.Width
                    && sceneGray.Height / 2 >= template.ScaledGrayMat.Height;

                if (useCoarse)
                {
                    using var sceneHalf = new Mat();
                    Cv2.Resize(
                        sceneGray,
                        sceneHalf,
                        new OpenCvSharp.Size(Math.Max(1, sceneGray.Width / 2), Math.Max(1, sceneGray.Height / 2)),
                        0,
                        0,
                        InterpolationFlags.Area);

                    using var coarseResult = new Mat();
                    Cv2.MatchTemplate(sceneHalf, template.ScaledGrayMat, coarseResult, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(coarseResult, out _, out double coarseMax, out _, out OpenCvSharp.Point coarseLoc);
                    ct.ThrowIfCancellationRequested();

                    int guessX = coarseLoc.X * 2;
                    int guessY = coarseLoc.Y * 2;
                    int pad = Math.Max(12, template.Width / 4);
                    int rx = Math.Max(0, guessX - pad);
                    int ry = Math.Max(0, guessY - pad);
                    int rw = Math.Min(sceneGray.Width - rx, template.Width + pad * 2);
                    int rh = Math.Min(sceneGray.Height - ry, template.Height + pad * 2);
                    if (coarseMax >= minConfidence * 0.72 && rw >= template.Width && rh >= template.Height)
                    {
                        using var refineRoi = new Mat(sceneGray, new OpenCvSharp.Rect(rx, ry, rw, rh));
                        using var refineResult = new Mat();
                        Cv2.MatchTemplate(refineRoi, template.GrayMat, refineResult, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(refineResult, out _, out double refineMax, out _, out OpenCvSharp.Point refineLoc);
                        ct.ThrowIfCancellationRequested();

                        if (refineMax >= minConfidence)
                        {
                            var boundingBox = new Rectangle(
                                roi.X + rx + refineLoc.X,
                                roi.Y + ry + refineLoc.Y,
                                template.Width,
                                template.Height);
                            return new MatchResult(true, refineMax, boundingBox);
                        }

                    }
                }

                // Downsampling can erase thin text or choose a lookalike elsewhere.
                // A coarse/refined miss is only a hint: search the full requested ROI
                // before declaring the template absent. Confirmed matches above retain
                // the fast path; capture ownership and cancellation remain unchanged.
                ct.ThrowIfCancellationRequested();
                using var matchResultMat = new Mat();
                Cv2.MatchTemplate(sceneGray, template.GrayMat, matchResultMat, TemplateMatchModes.CCoeffNormed);

                Cv2.MinMaxLoc(matchResultMat, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

                ct.ThrowIfCancellationRequested();

                if (maxVal >= minConfidence)
                {
                    var boundingBox = new Rectangle(
                        roi.X + maxLoc.X,
                        roi.Y + maxLoc.Y,
                        template.Width,
                        template.Height);

                    return new MatchResult(true, maxVal, boundingBox);
                }

                return new MatchResult(false, maxVal, Rectangle.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MatchResult(false, 0.0, Rectangle.Empty, MatchOutcome.Failed, ex.GetType().Name);
        }
    }

    private static Rectangle Clamp(Rectangle rect, int maxWidth, int maxHeight)
    {
        int x = Math.Max(0, rect.X);
        int y = Math.Max(0, rect.Y);
        int right = Math.Min(maxWidth, rect.Right);
        int bottom = Math.Min(maxHeight, rect.Bottom);

        int width = Math.Max(0, right - x);
        int height = Math.Max(0, bottom - y);

        return new Rectangle(x, y, width, height);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsCache)
        {
            _templateCache.Dispose();
        }
    }
}
