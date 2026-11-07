using MacroSuite.Core.Rules;

namespace MacroSuite.Core.Vision;

public enum VisualSearchScope
{
    Fixed = 0,
    Nearby = 1,
    Screen = 2
}

public enum VisualMatchKind
{
    Pixel = 0,
    Region = 1,
    /// <summary>Scan a rectangle for a target color; used by visual presence conditions.</summary>
    ColorPresent = 2
}

public enum VisualColorAlgorithm
{
    /// <summary>Legacy WaitUntil ColorCondition: Manhattan RGB (unchanged semantics).</summary>
    LegacyManhattan = 0,
    Perceptual = 1
}

public enum VisualTimeoutBehavior
{
    Stop = 0,
    Skip = 1,
    Retry = 2
}

/// <summary>
/// Matcher configuration. VisualWaitStep and ColorCondition both project into this.
/// </summary>
public sealed class VisualMatchOptions
{
    public const int DefaultMaxTemplateEdge = 512;

    public VisualMatchKind Kind { get; init; } = VisualMatchKind.Pixel;
    public VisualSearchScope SearchScope { get; init; } = VisualSearchScope.Fixed;
    public VisualColorAlgorithm Algorithm { get; init; } = VisualColorAlgorithm.Perceptual;
    public bool MaximumAccuracy { get; init; }
    public bool AiEnabled { get; init; }

    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; } = 1;
    public int Height { get; init; } = 1;
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    public int SimilarityPercent { get; init; } = 90;
    public int NeighborhoodSize { get; init; } = 1;
    public double RequiredPixelFraction { get; init; } = 0.85;
    public IReadOnlyList<PixelColor>? AdditionalTargets { get; init; }
    public int PollIntervalMs { get; init; } = 150;
    public int RequiredConfirmations { get; init; } = 1;
    public int RequiredFalseConfirmations { get; init; } = 1;
    public int StabilityDurationMs { get; init; }
    public int TimeoutMs { get; init; } = 10_000;
    public VisualTimeoutBehavior OnTimeout { get; init; } = VisualTimeoutBehavior.Stop;
    public ConditionFailurePolicy FailurePolicy { get; init; } = ConditionFailurePolicy.Stop;
    public ColorComparisonMode ComparisonMode { get; init; } = ColorComparisonMode.Cie76;
    public int SampleStride { get; init; } = 1;
    public int RetryCount { get; init; } = 3;
    public int SearchRadius { get; init; } = 50;
    public int AiMinIntervalMs { get; init; } = 1500;
    public int LegacyTolerance { get; init; }

    public byte[]? TemplateBgra { get; init; }
    public int TemplateWidth { get; init; }
    public int TemplateHeight { get; init; }
    public int MaxTemplateEdge { get; init; } = DefaultMaxTemplateEdge;

    public static bool IsTemplateOversized(int width, int height, int maxEdge = DefaultMaxTemplateEdge)
        => width > maxEdge || height > maxEdge || (long)width * height > (long)maxEdge * maxEdge;
}
