using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;

namespace MacroSuite.Infrastructure.Capture;

/// <summary>
/// Coordinates primary (DXGI/WGC) and fallback (GDI BitBlt) capture pipelines.
/// Implements automatic failover with a 30-second re-initialization backoff (Rule 5, Rule 15).
/// If GDI fallback is disabled by configuration, primary failures are surfaced immediately.
/// </summary>
public sealed class FallbackCaptureCoordinator : IScreenCaptureService, IImageScanCaptureService
{
    private readonly IScreenCaptureService _primary;
    private readonly IScreenCaptureService _fallback;
    private readonly object _stateLock = new();

    private DateTimeOffset? _lastPrimaryFailureUtc;
    private bool _disposed;

    /// <summary>
    /// Default backoff duration before attempting to re-use primary capture after a failure.
    /// </summary>
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets whether GDI BitBlt fallback is permitted when primary capture fails.
    /// Defaults to <c>true</c>. When set to <c>false</c>, primary failures will be surfaced to caller.
    /// </summary>
    public bool AllowGdiFallback { get; set; } = true;

    /// <summary>
    /// Backoff duration before attempting to re-engage the primary engine.
    /// </summary>
    public TimeSpan PrimaryRetryBackoff { get; set; } = DefaultBackoff;

    /// <summary>
    /// Timestamp of the most recent primary capture failure, if currently in a degraded state.
    /// </summary>
    public DateTimeOffset? LastPrimaryFailureUtc
    {
        get { lock (_stateLock) return _lastPrimaryFailureUtc; }
        private set { lock (_stateLock) _lastPrimaryFailureUtc = value; }
    }

    /// <inheritdoc/>
    public CaptureSource CurrentSource
    {
        get
        {
            lock (_stateLock)
            {
                if (!_primary.IsSupported ||
                    (_lastPrimaryFailureUtc.HasValue &&
                     DateTimeOffset.UtcNow - _lastPrimaryFailureUtc.Value < PrimaryRetryBackoff))
                {
                    return CaptureSource.Gdi;
                }
                return CaptureSource.Dxgi;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsSupported => _primary.IsSupported || (AllowGdiFallback && _fallback.IsSupported);

    /// <summary>
    /// Initializes coordinator with default <see cref="WindowsGraphicsCaptureService"/> and <see cref="GdiBitBltCaptureService"/>.
    /// </summary>
    public FallbackCaptureCoordinator()
        : this(new WindowsGraphicsCaptureService(), new GdiBitBltCaptureService())
    {
    }

    /// <summary>
    /// Initializes coordinator with custom/mockable primary and fallback implementations.
    /// </summary>
    public FallbackCaptureCoordinator(IScreenCaptureService primary, IScreenCaptureService fallback)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public async Task<ImageScanFrame> CaptureImageAsync(Rectangle? region, ImageScanResolution resolution, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (ShouldAttemptPrimary() && _primary is IImageScanCaptureService sampled)
        {
            try
            {
                var result = await sampled.CaptureImageAsync(region, resolution, ct).ConfigureAwait(false);
                LastPrimaryFailureUtc = null;
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastPrimaryFailureUtc = DateTimeOffset.UtcNow;
                if (!AllowGdiFallback) throw new InvalidOperationException("Sampled hardware capture failed and fallback is disabled.", ex);
            }
        }
        using var native = region.HasValue
            ? await CaptureRegionAsync(region.Value, ct).ConfigureAwait(false)
            : await CaptureScreenAsync(ct).ConfigureAwait(false);
        return OpenCV.ImageScanFrameConverter.Convert(native, resolution);
    }

    /// <inheritdoc/>
    public async Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        bool tryPrimary = ShouldAttemptPrimary();

        if (tryPrimary)
        {
            try
            {
                CaptureFrame frame = await _primary.CaptureRegionAsync(region, ct).ConfigureAwait(false);
                if (CaptureFrameIntegrity.IsUnpopulated(frame))
                {
                    frame.Dispose();
                    throw new InvalidOperationException("Primary capture returned an empty or unpopulated buffer.");
                }

                LastPrimaryFailureUtc = null; // Primary restored
                return frame;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastPrimaryFailureUtc = DateTimeOffset.UtcNow;

                if (!AllowGdiFallback)
                {
                    throw new InvalidOperationException("Primary screen capture failed and GDI fallback is disabled.", ex);
                }
            }
        }

        if (!AllowGdiFallback)
        {
            throw new InvalidOperationException(
                "Primary screen capture is in backoff period and GDI fallback is disabled.");
        }

        // Execute fallback
        return await _fallback.CaptureRegionAsync(region, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        bool tryPrimary = ShouldAttemptPrimary();

        if (tryPrimary)
        {
            try
            {
                CaptureFrame frame = await _primary.CaptureScreenAsync(ct).ConfigureAwait(false);
                if (CaptureFrameIntegrity.IsUnpopulated(frame))
                {
                    frame.Dispose();
                    throw new InvalidOperationException("Primary capture returned an empty or unpopulated buffer.");
                }

                LastPrimaryFailureUtc = null;
                return frame;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastPrimaryFailureUtc = DateTimeOffset.UtcNow;

                if (!AllowGdiFallback)
                {
                    throw new InvalidOperationException("Primary screen capture failed and GDI fallback is disabled.", ex);
                }
            }
        }

        if (!AllowGdiFallback)
        {
            throw new InvalidOperationException(
                "Primary screen capture is in backoff period and GDI fallback is disabled.");
        }

        return await _fallback.CaptureScreenAsync(ct).ConfigureAwait(false);
    }

    private bool ShouldAttemptPrimary()
    {
        lock (_stateLock)
        {
            if (!_primary.IsSupported)
            {
                return false;
            }

            if (!_lastPrimaryFailureUtc.HasValue)
            {
                return true;
            }

            return DateTimeOffset.UtcNow - _lastPrimaryFailureUtc.Value >= PrimaryRetryBackoff;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _primary.Dispose();
        _fallback.Dispose();
    }
}
