using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenCvSharp;

namespace MacroSuite.Infrastructure.OpenCV;

/// <summary>
/// A cached template containing three pre-computed <see cref="Mat"/> representations for high-speed matching.
/// Owns the native Mat resources and disposes them when evicted or cleared (Rule 13).
/// </summary>
public sealed class CachedTemplate : IDisposable
{
    /// <summary>SHA-256 hash identifying this template.</summary>
    public string Sha256Hash { get; }

    /// <summary>Full color (BGRA/BGR) template.</summary>
    public Mat ColorMat { get; }

    /// <summary>Grayscale single-channel template.</summary>
    public Mat GrayMat { get; }

    /// <summary>Half-resolution (0.5x) grayscale template for fast pre-filtering.</summary>
    public Mat ScaledGrayMat { get; }

    /// <summary>Template width in pixels.</summary>
    public int Width => ColorMat.Width;

    /// <summary>Template height in pixels.</summary>
    public int Height => ColorMat.Height;

    public CachedTemplate(string sha256Hash, Mat colorMat, Mat grayMat, Mat scaledGrayMat)
    {
        Sha256Hash = sha256Hash;
        ColorMat = colorMat;
        GrayMat = grayMat;
        ScaledGrayMat = scaledGrayMat;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        ColorMat.Dispose();
        GrayMat.Dispose();
        ScaledGrayMat.Dispose();
    }
}

/// <summary>
/// Thread-safe in-memory cache for OpenCV template images (Rule 13, Rule 18).
/// Keyed by SHA-256 hash of file content. Prevents redundant disk I/O and matrix allocations.
/// </summary>
public sealed class TemplateCache : IDisposable
{
    private readonly ConcurrentDictionary<string, CachedTemplate> _cache = new();
    private readonly ConcurrentDictionary<string, PathCacheEntry> _pathIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    private readonly record struct PathCacheEntry(long LastWriteTicks, string Hash);

    /// <summary>Number of unique templates currently cached.</summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Retrieves a cached template from disk or decodes and caches it if not present.
    /// Paths are keyed by last-write time so unchanged files are not re-read (Rule 18).
    /// </summary>
    public CachedTemplate GetOrAdd(string filePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"Template image file not found: '{filePath}'", filePath);
        }

        long ticks = info.LastWriteTimeUtc.Ticks;
        if (_pathIndex.TryGetValue(filePath, out PathCacheEntry entry)
            && entry.LastWriteTicks == ticks
            && _cache.TryGetValue(entry.Hash, out CachedTemplate? cached))
        {
            return cached;
        }

        byte[] bytes = File.ReadAllBytes(filePath);
        CachedTemplate template = GetOrAdd(bytes);
        _pathIndex[filePath] = new PathCacheEntry(ticks, template.Sha256Hash);
        return template;
    }

    /// <summary>
    /// Retrieves a cached template from raw image bytes or decodes and caches it if not present.
    /// </summary>
    public CachedTemplate GetOrAdd(byte[] imageBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(imageBytes);

        if (imageBytes.Length == 0)
        {
            throw new ArgumentException("Template image byte array cannot be empty.", nameof(imageBytes));
        }

        string hash = Convert.ToHexString(SHA256.HashData(imageBytes));

        return _cache.GetOrAdd(hash, _ => DecodeTemplate(hash, imageBytes));
    }

    private static CachedTemplate DecodeTemplate(string hash, byte[] bytes)
    {
        Mat colorMat = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (colorMat.Empty())
        {
            colorMat.Dispose();
            throw new InvalidOperationException("Failed to decode template image bytes into an OpenCV Mat.");
        }

        Mat grayMat = new Mat();
        if (colorMat.Channels() == 4)
        {
            Cv2.CvtColor(colorMat, grayMat, ColorConversionCodes.BGRA2GRAY);
        }
        else if (colorMat.Channels() == 3)
        {
            Cv2.CvtColor(colorMat, grayMat, ColorConversionCodes.BGR2GRAY);
        }
        else
        {
            colorMat.CopyTo(grayMat);
        }

        int halfWidth = Math.Max(1, grayMat.Width / 2);
        int halfHeight = Math.Max(1, grayMat.Height / 2);
        Mat scaledGrayMat = new Mat();
        Cv2.Resize(grayMat, scaledGrayMat, new OpenCvSharp.Size(halfWidth, halfHeight), 0, 0, InterpolationFlags.Area);

        return new CachedTemplate(hash, colorMat, grayMat, scaledGrayMat);
    }

    /// <summary>
    /// Evicts all cached templates and disposes their native Mat resources.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var template in _cache.Values)
            {
                template.Dispose();
            }
            _cache.Clear();
            _pathIndex.Clear();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Clear();
    }
}
