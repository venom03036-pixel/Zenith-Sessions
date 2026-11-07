using System.Drawing;

namespace MacroSuite.Core.Models;

public enum ImageScanStatus { Deferred, Partial, Complete, Failed, Cancelled, Superseded }

public sealed record ImageScanRequest(Guid Id, string? TemplatePath, byte[]? TemplateBytes,
    double MinConfidence, Rectangle? Region, ImageScanResolution Resolution,
    bool AllMatches = true, int PollIntervalMs = 100);

/// <summary>Bounds are always physical virtual-desktop pixels, including negative coordinates.</summary>
public sealed record ImageScanHit(Rectangle Bounds, double Confidence)
{
    public int CenterX => Bounds.X + Bounds.Width / 2;
    public int CenterY => Bounds.Y + Bounds.Height / 2;
    public long TimestampTicks { get; init; }
}

/// <summary>
/// Hits are incremental observations. Complete finishes a coverage sweep; consumers
/// combine all results with the same Generation before deciding absence. Deferred
/// and Partial never confirm absence. TimestampTicks describes this observation,
/// not an atomic screenshot of a multi-tile sweep.
/// </summary>
public sealed record ImageScanResult(Guid Id, ImageScanStatus Status, IReadOnlyList<ImageScanHit> Hits,
    long Generation, long TimestampTicks, Size SampledSize, Rectangle Coverage,
    string? ErrorCode = null, string? ErrorMessage = null)
{
    public TimeSpan Elapsed { get; init; }
    /// <summary>Native tiles fully searched in this observation; excludes unfinished paginated tiles.</summary>
    public IReadOnlyList<Rectangle> DetailCoverage { get; init; } = Array.Empty<Rectangle>();
}

public sealed record ImageScanPerformanceSnapshot(int EffectiveIntervalMs, double? CpuPercent,
    double? GpuPercent, bool IsThrottled, double TargetPercent)
{
    public int RequestedIntervalMs { get; init; } = 100;
    public Size SampledSize { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Observed time between this request's scan starts; not the scheduler's minimum interval.</summary>
    public double? MeasuredIntervalMs { get; init; }
    public Guid? MeasuredActionId { get; init; }
    public int? MeasuredRequestedIntervalMs { get; init; }
}
