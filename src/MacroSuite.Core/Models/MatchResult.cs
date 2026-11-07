using System.Drawing;

namespace MacroSuite.Core.Models;

/// <summary>
/// Outcome of a template scan. Not-found is a successful scan with no hit;
/// failed is capture/matcher/template error and must not be treated as absent.
/// </summary>
public enum MatchOutcome
{
    Matched = 0,
    NotFound = 1,
    Failed = 2
}

/// <summary>
/// Result of an image or template matching operation.
/// </summary>
public sealed record MatchResult(
    bool Success,
    double Confidence,
    Rectangle BoundingBox,
    MatchOutcome? Outcome = null,
    string? ErrorCode = null)
{
    /// <summary>Optional diagnostic for a failed match, separate from its stable error code.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Optional human-readable evidence from an explicit image test, including incomplete coverage.</summary>
    public string? DiagnosticSummary { get; init; }

    /// <summary>Successful scan that found no template hit.</summary>
    public static readonly MatchResult NotFound = new(false, 0.0, Rectangle.Empty, MatchOutcome.NotFound);

    /// <summary>Matcher, capture, or template error — not a business miss.</summary>
    public static readonly MatchResult Failed = new(false, 0.0, Rectangle.Empty, MatchOutcome.Failed, "MatchFailed");

    public MatchOutcome Status => Outcome ?? (Success ? MatchOutcome.Matched : MatchOutcome.NotFound);

    /// <summary>Center point of the matched bounding box.</summary>
    public Point Center => Success && !BoundingBox.IsEmpty
        ? new Point(BoundingBox.X + (BoundingBox.Width / 2), BoundingBox.Y + (BoundingBox.Height / 2))
        : Point.Empty;

    public static MatchResult Matched(double confidence, Rectangle boundingBox)
        => new(true, confidence, boundingBox, MatchOutcome.Matched);
}
